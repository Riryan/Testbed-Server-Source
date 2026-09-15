using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Persistence;
using Game.Shared.Backend;
using Game.Shared.Identity;
using Game.UnityIntegration.Backend;

namespace Game.GameServer.Backend;

/// <summary>
/// Backend-owned character authority lease for standalone GameServers.
/// Acquisition is asynchronous so Backend latency cannot block the authoritative
/// GameServer loop. Renewals are batched asynchronously so active CCU does not create
/// one heartbeat request per character. Release is best-effort and non-blocking; TTL
/// expiry is the crash/failure safety net.
/// </summary>
internal sealed class BackendCharacterLeaseService : ICharacterLeaseService, ICharacterPersistenceLeaseProofProvider
{
    public const int LeaseDurationSeconds = 60;
    public const int RenewalSafetySeconds = 20;
    private const int RenewalBatchSpacingMilliseconds = 25;

    private sealed class HeldLease
    {
        public CharacterId CharacterId;
        public PlayerSessionId SessionId;
        public string OwnerToken;
        public long ExpiresUtcTicks;
    }

    internal sealed class RenewalResult
    {
        public bool BackendAvailable { get; }
        public PlayerSessionId[] AtRiskSessions { get; }

        public RenewalResult(bool backendAvailable, PlayerSessionId[] atRiskSessions)
        {
            BackendAvailable = backendAvailable;
            AtRiskSessions = atRiskSessions ?? Array.Empty<PlayerSessionId>();
        }
    }

    private readonly object _gate = new object();
    private readonly BackendInternalClient _backend;
    private readonly string _processToken = Guid.NewGuid().ToString("N");
    private readonly Dictionary<CharacterId, HeldLease> _held = new Dictionary<CharacterId, HeldLease>();
    private readonly HashSet<Task> _pendingReleases = new HashSet<Task>();

    public int HeldCount
    {
        get { lock (_gate) return _held.Count; }
    }

    public BackendCharacterLeaseService(BackendInternalClient backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<bool> TryAcquireAsync(
        AccountId accountId,
        CharacterId characterId,
        PlayerSessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (!accountId.IsValid || !characterId.IsValid || !sessionId.IsValid)
            return false;

        lock (_gate)
        {
            if (_held.TryGetValue(characterId, out HeldLease local))
                return local.SessionId == sessionId && local.ExpiresUtcTicks > DateTime.UtcNow.Ticks;
        }

        string ownerToken = BuildOwnerToken(sessionId);
        BackendCharacterLeaseAcquireResponse response;
        try
        {
            response = await _backend.AcquireCharacterLeaseAsync(
                    new BackendCharacterLeaseAcquireRequest
                    {
                        accountId = accountId.Value,
                        characterId = characterId.Value,
                        ownerToken = ownerToken,
                        leaseSeconds = LeaseDurationSeconds,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Character lease acquire failed for {characterId}: {ex.Message}");
            return false;
        }

        if (response == null ||
            !response.success ||
            !response.acquired ||
            response.expiresUtcTicks <= DateTime.UtcNow.Ticks)
        {
            return false;
        }

        lock (_gate)
        {
            if (_held.TryGetValue(characterId, out HeldLease existing) && existing.SessionId != sessionId)
                return false;

            _held[characterId] = new HeldLease
            {
                CharacterId = characterId,
                SessionId = sessionId,
                OwnerToken = ownerToken,
                ExpiresUtcTicks = response.expiresUtcTicks,
            };
        }

        return true;
    }

    public void Release(CharacterId characterId, PlayerSessionId sessionId)
    {
        HeldLease released = null;
        lock (_gate)
        {
            if (_held.TryGetValue(characterId, out HeldLease current) && current.SessionId == sessionId)
            {
                released = current;
                _held.Remove(characterId);
            }
        }

        if (released == null)
            return;

        TrackRelease(released);
    }

    public bool IsHeldBy(CharacterId characterId, PlayerSessionId sessionId)
    {
        lock (_gate)
        {
            return _held.TryGetValue(characterId, out HeldLease held) &&
                   held.SessionId == sessionId &&
                   held.ExpiresUtcTicks > DateTime.UtcNow.Ticks;
        }
    }

    public bool TryGetPersistenceLeaseOwnerToken(CharacterId characterId, out string ownerToken)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(characterId, out HeldLease held) &&
                held.ExpiresUtcTicks > DateTime.UtcNow.Ticks &&
                !string.IsNullOrWhiteSpace(held.OwnerToken))
            {
                ownerToken = held.OwnerToken;
                return true;
            }
        }

        ownerToken = string.Empty;
        return false;
    }

    /// <summary>
    /// Waits for already-issued best-effort lease releases to finish. This is intended
    /// for clean process shutdown after authoritative character persistence has succeeded.
    /// It never releases currently-held leases by itself; PlayerSessionService.Close remains
    /// the canonical ownership transition that removes a lease from local authority first.
    /// </summary>
    public async Task<bool> DrainPendingReleasesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            return PendingReleaseCount == 0;

        DateTime deadlineUtc = DateTime.UtcNow + timeout;
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                if (_pendingReleases.Count == 0)
                    return true;
                pending = new Task[_pendingReleases.Count];
                _pendingReleases.CopyTo(pending);
            }

            TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            Task all = Task.WhenAll(pending);
            Task delay = Task.Delay(remaining, cancellationToken);
            Task completed = await Task.WhenAny(all, delay).ConfigureAwait(false);
            if (completed == delay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            // Completion callbacks remove each task from _pendingReleases. Loop once more
            // so releases queued concurrently while this snapshot was awaiting are included.
            await all.ConfigureAwait(false);
        }
    }

    private int PendingReleaseCount
    {
        get { lock (_gate) return _pendingReleases.Count; }
    }

    public async Task<RenewalResult> RenewHeldAsync(CancellationToken cancellationToken)
    {
        HeldLease[] snapshot;
        lock (_gate)
        {
            snapshot = new HeldLease[_held.Count];
            int index = 0;
            foreach (HeldLease lease in _held.Values)
            {
                snapshot[index++] = new HeldLease
                {
                    CharacterId = lease.CharacterId,
                    SessionId = lease.SessionId,
                    OwnerToken = lease.OwnerToken,
                    ExpiresUtcTicks = lease.ExpiresUtcTicks,
                };
            }
        }

        if (snapshot.Length == 0)
            return new RenewalResult(true, Array.Empty<PlayerSessionId>());

        var lostSessions = new HashSet<PlayerSessionId>();
        bool backendAvailable = true;
        const int batchLimit = BackendServiceContracts.MaxCharacterBatchSize;

        for (int offset = 0; offset < snapshot.Length; offset += batchLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(batchLimit, snapshot.Length - offset);
            var requestEntries = new BackendCharacterLeaseRenewDto[count];
            for (int i = 0; i < count; ++i)
            {
                HeldLease expected = snapshot[offset + i];
                requestEntries[i] = new BackendCharacterLeaseRenewDto
                {
                    characterId = expected.CharacterId.Value,
                    ownerToken = expected.OwnerToken,
                };
            }

            BackendCharacterLeaseRenewBatchResponse response;
            try
            {
                response = await _backend.RenewCharacterLeasesAsync(
                        new BackendCharacterLeaseRenewBatchRequest
                        {
                            leaseSeconds = LeaseDurationSeconds,
                            leases = requestEntries,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                backendAvailable = false;
                break;
            }

            if (response == null || !response.success)
            {
                backendAvailable = false;
                break;
            }

            var acknowledgements = new Dictionary<long, BackendCharacterLeaseRenewAckDto>(count);
            BackendCharacterLeaseRenewAckDto[] source =
                response.acknowledgements ?? Array.Empty<BackendCharacterLeaseRenewAckDto>();
            for (int i = 0; i < source.Length; ++i)
            {
                BackendCharacterLeaseRenewAckDto ack = source[i];
                if (ack != null && ack.characterId > 0)
                    acknowledgements[ack.characterId] = ack;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_gate)
            {
                for (int i = 0; i < count; ++i)
                {
                    HeldLease expected = snapshot[offset + i];
                    if (!_held.TryGetValue(expected.CharacterId, out HeldLease current) ||
                        current.SessionId != expected.SessionId ||
                        !string.Equals(current.OwnerToken, expected.OwnerToken, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (acknowledgements.TryGetValue(
                            expected.CharacterId.Value,
                            out BackendCharacterLeaseRenewAckDto ack) &&
                        ack.accepted &&
                        ack.expiresUtcTicks > nowTicks)
                    {
                        current.ExpiresUtcTicks = ack.expiresUtcTicks;
                        continue;
                    }

                    // Backend did not renew this exact ownership proof. Remove it locally
                    // immediately; the transport host will fail the matching session closed.
                    _held.Remove(expected.CharacterId);
                    lostSessions.Add(expected.SessionId);
                }
            }

            if (offset + count < snapshot.Length)
                await Task.Delay(RenewalBatchSpacingMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        long cutoff = DateTime.UtcNow.AddSeconds(RenewalSafetySeconds).Ticks;
        PlayerSessionId[] atRisk = CollectAtRiskSessions(cutoff);
        for (int i = 0; i < atRisk.Length; ++i)
            lostSessions.Add(atRisk[i]);

        var result = new PlayerSessionId[lostSessions.Count];
        lostSessions.CopyTo(result);
        return new RenewalResult(backendAvailable, result);
    }

    private PlayerSessionId[] CollectAtRiskSessions(long cutoffUtcTicks)
    {
        lock (_gate)
        {
            var atRisk = new List<PlayerSessionId>();
            foreach (HeldLease lease in _held.Values)
                if (lease.ExpiresUtcTicks <= cutoffUtcTicks)
                    atRisk.Add(lease.SessionId);
            return atRisk.ToArray();
        }
    }

    private string BuildOwnerToken(PlayerSessionId sessionId) => _processToken + ":" + sessionId.ToString();

    private void TrackRelease(HeldLease lease)
    {
        Task task = ReleaseAsync(lease);
        lock (_gate)
            _pendingReleases.Add(task);

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                    _pendingReleases.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReleaseAsync(HeldLease lease)
    {
        try
        {
            await _backend.ReleaseCharacterLeaseAsync(
                    new BackendCharacterLeaseReleaseRequest
                    {
                        characterId = lease.CharacterId.Value,
                        ownerToken = lease.OwnerToken,
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The lease is already removed from local authority. Backend TTL expiry is
            // intentionally the fail-safe if the release request cannot be delivered.
            Console.Error.WriteLine($"Character lease release failed for {lease.CharacterId}; TTL expiry will clear it: {ex.Message}");
        }
    }
}

internal static class CharacterLeaseTaskExtensions
{
    public static void ForgetLeaseTask(this Task task)
    {
        _ = task.ContinueWith(
            completed => Console.Error.WriteLine(completed.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
