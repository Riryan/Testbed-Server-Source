using System.Security.Cryptography;
using System.Text;
using Game.Shared.Backend;

namespace Game.BackendServer;

/// <summary>
/// Ephemeral authoritative directory of live GameServer processes.
///
/// Directory membership is intentionally lease-based and is not durable account/gameplay
/// state. A Gateway restart clears the directory; live GameServers re-register on their
/// next heartbeat. This prevents stale process records from becoming persistent routing
/// authority.
///
/// The current testbed internal API remains loopback-only and protected by the shared
/// GameServer key. Per-registration random lease tokens prevent accidental cross-process
/// heartbeat/unregister collisions between authorized local GameServers.
/// </summary>
internal sealed class GameServerDirectory
{
    private sealed class Entry
    {
        public string ServerId = string.Empty;
        public byte[] LeaseTokenHash = Array.Empty<byte>();
        public string AdvertiseHost = string.Empty;
        public int AdvertisePort;
        public int MaxConnections;
        public int ConnectedPlayers;
        public long ExpiresUtcTicks;
        public BackendGameServerMapDto[] Maps = Array.Empty<BackendGameServerMapDto>();
    }

    private readonly object _gate = new object();
    private readonly Dictionary<string, Entry> _entries =
        new Dictionary<string, Entry>(StringComparer.Ordinal);
    // Live authority assignments are intentionally separate from GameServer map capability.
    // Multiple generic workers may load the same baked world package; only the worker selected
    // here is the current authoritative owner of a map/instance partition. Assignments are
    // ephemeral and are fenced by the owning GameServer's existing directory lease.
    private readonly Dictionary<string, string> _assignments =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public GameServerDirectoryDiagnostics GetDiagnostics()
    {
        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            RemoveExpiredLocked(now);
            int connectedPlayers = 0;
            int maxConnections = 0;
            foreach (Entry entry in _entries.Values)
            {
                connectedPlayers = checked(connectedPlayers + Math.Max(0, entry.ConnectedPlayers));
                maxConnections = checked(maxConnections + Math.Max(0, entry.MaxConnections));
            }
            return new GameServerDirectoryDiagnostics(
                _entries.Count,
                connectedPlayers,
                maxConnections,
                _assignments.Count);
        }
    }

    public BackendGameServerRegisterResponse Register(BackendGameServerRegisterRequest request)
    {
        if (!TryValidateRegistration(request, out string error))
            return RegisterFailed(error);

        long now = DateTime.UtcNow.Ticks;
        long expires = checked(now + TimeSpan.FromSeconds(request.leaseSeconds).Ticks);
        string leaseToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var entry = new Entry
        {
            ServerId = request.serverId.Trim(),
            LeaseTokenHash = HashToken(leaseToken),
            AdvertiseHost = request.advertiseHost.Trim(),
            AdvertisePort = request.advertisePort,
            MaxConnections = request.maxConnections,
            ConnectedPlayers = Math.Clamp(request.connectedPlayers, 0, request.maxConnections),
            ExpiresUtcTicks = expires,
            Maps = NormalizeMaps(request.maps),
        };

        lock (_gate)
        {
            RemoveExpiredLocked(now);

            // Registration advertises capability, not ownership. Generic GameServer workers
            // are allowed to load the same baked map data. Resolve() creates one ephemeral
            // authoritative assignment and keeps that assignment stable while its owner lease
            // remains live. This removes the old startup conflict without creating duplicate
            // authoritative ownership.
            _entries[entry.ServerId] = entry;
            PruneInvalidAssignmentsLocked();
        }

        return new BackendGameServerRegisterResponse
        {
            success = true,
            leaseToken = leaseToken,
            expiresUtcTicks = expires,
            error = string.Empty,
        };
    }

    public BackendGameServerHeartbeatResponse Heartbeat(BackendGameServerHeartbeatRequest request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.serverId) ||
            string.IsNullOrWhiteSpace(request.leaseToken) ||
            request.serverId.Length > 96 ||
            request.leaseToken.Length > 256 ||
            request.leaseSeconds < 15 ||
            request.leaseSeconds > 120)
        {
            return HeartbeatRejected("invalid GameServer heartbeat");
        }

        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            RemoveExpiredLocked(now);

            if (!_entries.TryGetValue(request.serverId.Trim(), out Entry entry) ||
                !TokenMatches(entry.LeaseTokenHash, request.leaseToken))
            {
                return HeartbeatRejected("GameServer directory lease is unavailable");
            }

            entry.ConnectedPlayers = Math.Clamp(request.connectedPlayers, 0, entry.MaxConnections);
            entry.ExpiresUtcTicks = checked(now + TimeSpan.FromSeconds(request.leaseSeconds).Ticks);

            return new BackendGameServerHeartbeatResponse
            {
                success = true,
                accepted = true,
                expiresUtcTicks = entry.ExpiresUtcTicks,
                error = string.Empty,
            };
        }
    }

    public BackendGameServerUnregisterResponse Unregister(BackendGameServerUnregisterRequest request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.serverId) ||
            string.IsNullOrWhiteSpace(request.leaseToken))
        {
            return new BackendGameServerUnregisterResponse
            {
                success = false,
                removed = false,
                error = "invalid GameServer unregister request",
            };
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(request.serverId.Trim(), out Entry entry) ||
                !TokenMatches(entry.LeaseTokenHash, request.leaseToken))
            {
                return new BackendGameServerUnregisterResponse
                {
                    success = true,
                    removed = false,
                    error = string.Empty,
                };
            }

            _entries.Remove(entry.ServerId);
            RemoveAssignmentsOwnedByLocked(entry.ServerId);
            return new BackendGameServerUnregisterResponse
            {
                success = true,
                removed = true,
                error = string.Empty,
            };
        }
    }

    public bool HasLiveServer()
    {
        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            RemoveExpiredLocked(now);
            return _entries.Count > 0;
        }
    }

    public BackendGameServerResolveMapResponse Resolve(BackendGameServerResolveMapRequest request)
    {
        string mapId = (request?.mapId ?? string.Empty).Trim();
        string instanceId = (request?.instanceId ?? string.Empty).Trim();
        if (mapId.Length == 0 || mapId.Length > 128 || instanceId.Length > 128)
            return ResolveFailed("invalid map partition");

        long now = DateTime.UtcNow.Ticks;
        string partitionKey = MakePartitionKey(mapId, instanceId);
        lock (_gate)
        {
            RemoveExpiredLocked(now);

            // Existing authority remains sticky for the lifetime of the owner's directory
            // lease. Do not silently move a live partition merely because another worker is
            // less loaded; deliberate live migration will use a later handoff protocol.
            if (_assignments.TryGetValue(partitionKey, out string assignedServerId))
            {
                if (_entries.TryGetValue(assignedServerId, out Entry assigned) &&
                    SupportsPartition(assigned, mapId, instanceId))
                {
                    if (assigned.MaxConnections <= 0 || assigned.ConnectedPlayers >= assigned.MaxConnections)
                        return ResolveFailed($"authoritative GameServer '{assigned.ServerId}' is at capacity");
                    return ToResolveResponse(assigned);
                }

                _assignments.Remove(partitionKey);
            }

            Entry best = null;
            double bestLoad = double.MaxValue;

            foreach (Entry entry in _entries.Values)
            {
                if (entry.MaxConnections <= 0 || entry.ConnectedPlayers >= entry.MaxConnections)
                    continue;
                if (!SupportsPartition(entry, mapId, instanceId))
                    continue;

                double load = (double)entry.ConnectedPlayers / entry.MaxConnections;
                if (best == null ||
                    load < bestLoad ||
                    (Math.Abs(load - bestLoad) < 0.000001 &&
                     string.CompareOrdinal(entry.ServerId, best.ServerId) < 0))
                {
                    best = entry;
                    bestLoad = load;
                }
            }

            if (best == null)
                return ResolveFailed("no live GameServer can host the requested map partition");

            _assignments[partitionKey] = best.ServerId;
            return ToResolveResponse(best);
        }
    }

    private static BackendGameServerResolveMapResponse ToResolveResponse(Entry entry) =>
        new BackendGameServerResolveMapResponse
        {
            success = true,
            serverId = entry.ServerId,
            advertiseHost = entry.AdvertiseHost,
            advertisePort = entry.AdvertisePort,
            connectedPlayers = entry.ConnectedPlayers,
            maxConnections = entry.MaxConnections,
            expiresUtcTicks = entry.ExpiresUtcTicks,
            error = string.Empty,
        };

    private void RemoveExpiredLocked(long now)
    {
        if (_entries.Count == 0)
        {
            _assignments.Clear();
            return;
        }

        var expired = new List<string>();
        foreach (KeyValuePair<string, Entry> pair in _entries)
            if (pair.Value.ExpiresUtcTicks <= now)
                expired.Add(pair.Key);

        for (int i = 0; i < expired.Count; ++i)
        {
            _entries.Remove(expired[i]);
            RemoveAssignmentsOwnedByLocked(expired[i]);
        }

        PruneInvalidAssignmentsLocked();
    }

    private void RemoveAssignmentsOwnedByLocked(string serverId)
    {
        if (_assignments.Count == 0)
            return;

        var remove = new List<string>();
        foreach (KeyValuePair<string, string> pair in _assignments)
            if (string.Equals(pair.Value, serverId, StringComparison.Ordinal))
                remove.Add(pair.Key);

        for (int i = 0; i < remove.Count; ++i)
            _assignments.Remove(remove[i]);
    }

    private void PruneInvalidAssignmentsLocked()
    {
        if (_assignments.Count == 0)
            return;

        var remove = new List<string>();
        foreach (KeyValuePair<string, string> pair in _assignments)
        {
            if (!_entries.TryGetValue(pair.Value, out Entry entry) ||
                !TrySplitPartitionKey(pair.Key, out string mapId, out string instanceId) ||
                !SupportsPartition(entry, mapId, instanceId))
            {
                remove.Add(pair.Key);
            }
        }

        for (int i = 0; i < remove.Count; ++i)
            _assignments.Remove(remove[i]);
    }

    private static bool SupportsPartition(Entry entry, string mapId, string instanceId)
    {
        BackendGameServerMapDto[] maps = entry.Maps ?? Array.Empty<BackendGameServerMapDto>();
        for (int i = 0; i < maps.Length; ++i)
        {
            BackendGameServerMapDto map = maps[i];
            if (map != null &&
                string.Equals(map.mapId, mapId, StringComparison.Ordinal) &&
                string.Equals(map.instanceId ?? string.Empty, instanceId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string MakePartitionKey(string mapId, string instanceId) =>
        mapId + "\n" + (instanceId ?? string.Empty);

    private static bool TrySplitPartitionKey(string key, out string mapId, out string instanceId)
    {
        int separator = (key ?? string.Empty).IndexOf('\n');
        if (separator <= 0)
        {
            mapId = string.Empty;
            instanceId = string.Empty;
            return false;
        }
        mapId = key.Substring(0, separator);
        instanceId = key.Substring(separator + 1);
        return true;
    }

    private static BackendGameServerMapDto[] NormalizeMaps(BackendGameServerMapDto[] source)
    {
        source ??= Array.Empty<BackendGameServerMapDto>();
        var result = new List<BackendGameServerMapDto>(Math.Min(source.Length, 256));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < source.Length && result.Count < 256; ++i)
        {
            BackendGameServerMapDto map = source[i];
            string mapId = (map?.mapId ?? string.Empty).Trim();
            string instanceId = (map?.instanceId ?? string.Empty).Trim();
            if (mapId.Length == 0 || mapId.Length > 128 || instanceId.Length > 128)
                continue;

            string key = mapId + "\n" + instanceId;
            if (!seen.Add(key))
                continue;

            result.Add(new BackendGameServerMapDto
            {
                mapId = mapId,
                instanceId = instanceId,
            });
        }

        return result.ToArray();
    }

    private static bool TryValidateRegistration(
        BackendGameServerRegisterRequest request,
        out string error)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.serverId) ||
            request.serverId.Trim().Length > 96)
        {
            error = "invalid GameServer ID";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.advertiseHost) ||
            request.advertiseHost.Trim().Length > 255 ||
            request.advertisePort < 1 ||
            request.advertisePort > 65535)
        {
            error = "invalid GameServer advertised endpoint";
            return false;
        }

        if (request.maxConnections < 1 ||
            request.maxConnections > 100000 ||
            request.connectedPlayers < 0 ||
            request.leaseSeconds < 15 ||
            request.leaseSeconds > 120)
        {
            error = "invalid GameServer capacity/lease";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));

    private static bool TokenMatches(byte[] expectedHash, string token)
    {
        byte[] actual = HashToken(token);
        return expectedHash != null &&
               expectedHash.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expectedHash, actual);
    }

    private static BackendGameServerRegisterResponse RegisterFailed(string error) =>
        new BackendGameServerRegisterResponse
        {
            success = false,
            leaseToken = string.Empty,
            expiresUtcTicks = 0,
            error = error ?? "GameServer registration rejected",
        };

    private static BackendGameServerHeartbeatResponse HeartbeatRejected(string error) =>
        new BackendGameServerHeartbeatResponse
        {
            success = true,
            accepted = false,
            expiresUtcTicks = 0,
            error = error ?? "GameServer heartbeat rejected",
        };

    private static BackendGameServerResolveMapResponse ResolveFailed(string error) =>
        new BackendGameServerResolveMapResponse
        {
            success = false,
            serverId = string.Empty,
            advertiseHost = string.Empty,
            advertisePort = 0,
            connectedPlayers = 0,
            maxConnections = 0,
            expiresUtcTicks = 0,
            error = error ?? "GameServer route unavailable",
        };
}

internal readonly record struct GameServerDirectoryDiagnostics(
    int LiveServers,
    int ConnectedPlayers,
    int MaxConnections,
    int MapPartitions);
