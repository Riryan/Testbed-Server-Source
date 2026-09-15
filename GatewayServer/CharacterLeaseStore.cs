using Game.Shared.Backend;

namespace Game.BackendServer;

/// <summary>
/// Ephemeral backend-owned character authority leases for standalone GameServers.
/// Leases are intentionally not durable: process loss is recovered by TTL expiry.
/// An expired owner receives a short recovery handoff before another GameServer can
/// take over, which avoids split authority during brief backend/network interruptions.
/// </summary>
internal sealed class CharacterLeaseStore
{
    public const int MinLeaseSeconds = 15;
    public const int MaxLeaseSeconds = 120;
    public const int RecoveryHandoffSeconds = 20;
    private const int MaxOwnerTokenLength = 256;

    private sealed class LeaseEntry
    {
        public long AccountId;
        public long CharacterId;
        public string OwnerToken = string.Empty;
        public string OwnerProcessToken = string.Empty;
        public long ExpiresUtcTicks;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, LeaseEntry> _leases = new();
    private readonly HashSet<long> _deleteReservations = new();

    public CharacterLeaseDiagnostics GetDiagnostics()
    {
        long now = DateTime.UtcNow.Ticks;
        int active = 0;
        int recovery = 0;
        int stale = 0;
        int deleteReservations;

        lock (_gate)
        {
            foreach (LeaseEntry entry in _leases.Values)
            {
                if (entry.ExpiresUtcTicks > now)
                    active++;
                else if (now <= SafeAddRecovery(entry.ExpiresUtcTicks))
                    recovery++;
                else
                    stale++;
            }
            deleteReservations = _deleteReservations.Count;
        }

        return new CharacterLeaseDiagnostics(active, recovery, stale, deleteReservations);
    }

    public BackendCharacterLeaseAcquireResponse Acquire(BackendCharacterLeaseAcquireRequest request)
    {
        if (!TryValidateAcquire(request, out string error))
            return AcquireFailed(error);

        long now = DateTime.UtcNow.Ticks;
        long expires = checked(now + TimeSpan.FromSeconds(request.leaseSeconds).Ticks);
        string processToken = ExtractProcessToken(request.ownerToken);

        lock (_gate)
        {
            if (_deleteReservations.Contains(request.characterId))
                return AcquireRejected("character deletion is in progress");

            if (!_leases.TryGetValue(request.characterId, out LeaseEntry current))
            {
                _leases[request.characterId] = NewEntry(request, processToken, expires);
                return AcquireSucceeded(expires);
            }

            // Exact owner retries are idempotent and extend the lease.
            if (string.Equals(current.OwnerToken, request.ownerToken, StringComparison.Ordinal))
            {
                current.AccountId = request.accountId;
                current.ExpiresUtcTicks = expires;
                return AcquireSucceeded(expires);
            }

            if (current.ExpiresUtcTicks > now)
                return AcquireRejected("character is already active");

            long recoveryUntil = SafeAddRecovery(current.ExpiresUtcTicks);
            bool sameProcess =
                !string.IsNullOrEmpty(processToken) &&
                string.Equals(current.OwnerProcessToken, processToken, StringComparison.Ordinal);

            // The prior GameServer process gets a bounded recovery opportunity after expiry.
            // A new session on that same process may have a different session suffix, so the
            // process-token prefix is the stable recovery identity.
            if (sameProcess && now <= recoveryUntil)
            {
                _leases[request.characterId] = NewEntry(request, processToken, expires);
                return AcquireSucceeded(expires);
            }

            if (now <= recoveryUntil)
                return AcquireRejected("character lease recovery handoff is active");

            // Recovery window elapsed. The stale entry no longer blocks takeover.
            _leases[request.characterId] = NewEntry(request, processToken, expires);
            return AcquireSucceeded(expires);
        }
    }

    public BackendCharacterLeaseRenewBatchResponse Renew(BackendCharacterLeaseRenewBatchRequest request)
    {
        if (request == null ||
            request.leaseSeconds < MinLeaseSeconds ||
            request.leaseSeconds > MaxLeaseSeconds ||
            request.leases == null ||
            request.leases.Length > BackendServiceContracts.MaxCharacterBatchSize)
        {
            return new BackendCharacterLeaseRenewBatchResponse
            {
                success = false,
                error = "invalid character lease renewal",
                acknowledgements = Array.Empty<BackendCharacterLeaseRenewAckDto>(),
            };
        }

        long now = DateTime.UtcNow.Ticks;
        long expires = checked(now + TimeSpan.FromSeconds(request.leaseSeconds).Ticks);
        var acknowledgements = new BackendCharacterLeaseRenewAckDto[request.leases.Length];

        lock (_gate)
        {
            for (int i = 0; i < request.leases.Length; ++i)
            {
                BackendCharacterLeaseRenewDto renewal = request.leases[i];
                if (!IsValidCharacterIdAndToken(renewal?.characterId ?? 0, renewal?.ownerToken))
                {
                    acknowledgements[i] = RenewRejected(renewal?.characterId ?? 0, "invalid character lease renewal");
                    continue;
                }

                if (!_leases.TryGetValue(renewal.characterId, out LeaseEntry current) ||
                    !string.Equals(current.OwnerToken, renewal.ownerToken, StringComparison.Ordinal))
                {
                    acknowledgements[i] = RenewRejected(renewal.characterId, "character lease ownership changed");
                    continue;
                }

                long recoveryUntil = SafeAddRecovery(current.ExpiresUtcTicks);
                if (current.ExpiresUtcTicks <= now && now > recoveryUntil)
                {
                    _leases.Remove(renewal.characterId);
                    acknowledgements[i] = RenewRejected(renewal.characterId, "character lease expired");
                    continue;
                }

                current.ExpiresUtcTicks = expires;
                acknowledgements[i] = new BackendCharacterLeaseRenewAckDto
                {
                    characterId = renewal.characterId,
                    accepted = true,
                    expiresUtcTicks = expires,
                    error = string.Empty,
                };
            }
        }

        return new BackendCharacterLeaseRenewBatchResponse
        {
            success = true,
            error = string.Empty,
            acknowledgements = acknowledgements,
        };
    }

    public BackendCharacterLeaseReleaseResponse Release(BackendCharacterLeaseReleaseRequest request)
    {
        if (request == null || !IsValidCharacterIdAndToken(request.characterId, request.ownerToken))
        {
            return new BackendCharacterLeaseReleaseResponse
            {
                success = false,
                released = false,
                error = "invalid character lease release",
            };
        }

        bool released = false;
        lock (_gate)
        {
            if (_leases.TryGetValue(request.characterId, out LeaseEntry current) &&
                string.Equals(current.OwnerToken, request.ownerToken, StringComparison.Ordinal))
            {
                _leases.Remove(request.characterId);
                released = true;
            }
        }

        return new BackendCharacterLeaseReleaseResponse
        {
            success = true,
            released = released,
            error = string.Empty,
        };
    }

    /// <summary>
    /// Reserves a character for an archive-delete transaction. The reservation blocks
    /// new lease acquisition while the database transaction runs, closing the race where
    /// a different GameServer could enter the character between the lease check and delete.
    /// Expired leases remain protected through the recovery handoff window.
    /// </summary>
    public bool TryBeginDeletion(long characterId)
    {
        if (characterId <= 0)
            return false;

        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            if (_deleteReservations.Contains(characterId))
                return false;

            if (_leases.TryGetValue(characterId, out LeaseEntry current))
            {
                if (now <= SafeAddRecovery(current.ExpiresUtcTicks))
                    return false;
                _leases.Remove(characterId);
            }

            _deleteReservations.Add(characterId);
            return true;
        }
    }

    public void EndDeletion(long characterId)
    {
        if (characterId <= 0)
            return;
        lock (_gate)
            _deleteReservations.Remove(characterId);
    }

    /// <summary>
    /// Persistence mutation proof. Only a currently unexpired exact lease owner may write
    /// authoritative character/item state.
    /// </summary>
    public bool IsCurrentOwner(long characterId, string ownerToken)
    {
        if (!IsValidCharacterIdAndToken(characterId, ownerToken))
            return false;

        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            return _leases.TryGetValue(characterId, out LeaseEntry current) &&
                   current.ExpiresUtcTicks > now &&
                   string.Equals(current.OwnerToken, ownerToken, StringComparison.Ordinal);
        }
    }

    private static LeaseEntry NewEntry(
        BackendCharacterLeaseAcquireRequest request,
        string processToken,
        long expiresUtcTicks) =>
        new()
        {
            AccountId = request.accountId,
            CharacterId = request.characterId,
            OwnerToken = request.ownerToken,
            OwnerProcessToken = processToken,
            ExpiresUtcTicks = expiresUtcTicks,
        };

    private static bool TryValidateAcquire(BackendCharacterLeaseAcquireRequest request, out string error)
    {
        if (request == null || request.accountId <= 0 ||
            !IsValidCharacterIdAndToken(request.characterId, request.ownerToken) ||
            request.leaseSeconds < MinLeaseSeconds || request.leaseSeconds > MaxLeaseSeconds)
        {
            error = "invalid character lease request";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsValidCharacterIdAndToken(long characterId, string ownerToken) =>
        characterId > 0 &&
        !string.IsNullOrWhiteSpace(ownerToken) &&
        ownerToken.Length <= MaxOwnerTokenLength;

    private static string ExtractProcessToken(string ownerToken)
    {
        if (string.IsNullOrWhiteSpace(ownerToken))
            return string.Empty;

        int separator = ownerToken.IndexOf(':');
        return separator > 0 ? ownerToken.Substring(0, separator) : ownerToken;
    }

    private static long SafeAddRecovery(long expiresUtcTicks)
    {
        long recovery = TimeSpan.FromSeconds(RecoveryHandoffSeconds).Ticks;
        return expiresUtcTicks > long.MaxValue - recovery ? long.MaxValue : expiresUtcTicks + recovery;
    }

    private static BackendCharacterLeaseAcquireResponse AcquireSucceeded(long expiresUtcTicks) =>
        new()
        {
            success = true,
            acquired = true,
            expiresUtcTicks = expiresUtcTicks,
            error = string.Empty,
        };

    private static BackendCharacterLeaseAcquireResponse AcquireRejected(string error) =>
        new()
        {
            success = true,
            acquired = false,
            expiresUtcTicks = 0,
            error = error,
        };

    private static BackendCharacterLeaseAcquireResponse AcquireFailed(string error) =>
        new()
        {
            success = false,
            acquired = false,
            expiresUtcTicks = 0,
            error = error,
        };

    private static BackendCharacterLeaseRenewAckDto RenewRejected(long characterId, string error) =>
        new()
        {
            characterId = characterId,
            accepted = false,
            expiresUtcTicks = 0,
            error = error,
        };
}

internal readonly record struct CharacterLeaseDiagnostics(
    int Active,
    int RecoveryHandoff,
    int Stale,
    int DeleteReservations);
