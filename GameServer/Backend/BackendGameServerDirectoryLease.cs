using Game.GameServer;
using Game.Server.Application.World;
using Game.Shared.Backend;
using Game.Shared.World;
using Game.UnityIntegration.Backend;

namespace Game.GameServer.Backend;

/// <summary>
/// Maintains this process's ephemeral Gateway-owned GameServer directory lease.
///
/// The Gateway directory is the authority for map/instance ownership. A GameServer may
/// continue simulating sessions that are already InWorld when the directory becomes
/// unavailable, but it must fail closed for new connections/world admission once its
/// directory lease is no longer valid.
/// </summary>
internal sealed class BackendGameServerDirectoryLease : IDisposable
{
    private readonly object _gate = new object();
    private readonly BackendInternalClient _backend;
    private readonly GameServerOptions _options;
    private readonly BackendGameServerMapDto[] _maps;

    private CancellationTokenSource _loopCancellation;
    private Task _heartbeatLoop;
    private string _leaseToken = string.Empty;
    private long _expiresUtcTicks;
    private int _connectedPlayers;
    private bool _started;
    private bool _disposed;

    public string ServerId => _options.ServerId;

    public bool CanAdmitNewSessions
    {
        get
        {
            lock (_gate)
            {
                return _started &&
                       !_disposed &&
                       !string.IsNullOrWhiteSpace(_leaseToken) &&
                       _expiresUtcTicks > DateTime.UtcNow.Ticks;
            }
        }
    }

    public long ExpiresUtcTicks
    {
        get { lock (_gate) return _expiresUtcTicks; }
    }

    public int OwnedPartitionCount => _maps.Length;

    public BackendGameServerDirectoryLease(
        BackendInternalClient backend,
        GameServerOptions options,
        ServerMapCatalog maps)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (maps == null)
            throw new ArgumentNullException(nameof(maps));

        _maps = BuildMapList(maps);
    }

    /// <summary>
    /// Performs the initial registration synchronously with startup. The UDP listener is not
    /// opened unless Gateway has accepted this process as the live owner of its partitions.
    /// </summary>
    public void Start(CancellationToken shutdownToken)
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BackendGameServerDirectoryLease));
            if (_started)
                throw new InvalidOperationException("GameServer directory lease is already started.");
        }

        BackendGameServerRegisterResponse registration = RegisterAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (registration == null ||
            !registration.success ||
            string.IsNullOrWhiteSpace(registration.leaseToken) ||
            registration.expiresUtcTicks <= DateTime.UtcNow.Ticks)
        {
            string error = registration?.error;
            if (string.IsNullOrWhiteSpace(error))
                error = "Gateway rejected GameServer directory registration";
            throw new InvalidOperationException(error);
        }

        lock (_gate)
        {
            _leaseToken = registration.leaseToken;
            _expiresUtcTicks = registration.expiresUtcTicks;
            _started = true;
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(_loopCancellation.Token));
        }

        Console.WriteLine(
            $"GameServer directory lease active: serverId={_options.ServerId}, " +
            $"partitions={_maps.Length}, expiresUtc={new DateTime(registration.expiresUtcTicks, DateTimeKind.Utc):O}");
    }

    public void UpdateConnectedPlayers(int connectedPlayers) =>
        Interlocked.Exchange(ref _connectedPlayers, Math.Clamp(connectedPlayers, 0, _options.MaxConnections));

    /// <summary>
    /// Resolves a partition against Gateway authority. Callers must still compare the returned
    /// serverId to this process and re-check CanAdmitNewSessions immediately before world entry.
    /// </summary>
    public Task<BackendGameServerResolveMapResponse> ResolveMapAsync(
        string mapId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (!CanAdmitNewSessions)
        {
            return Task.FromResult(new BackendGameServerResolveMapResponse
            {
                success = false,
                serverId = string.Empty,
                advertiseHost = string.Empty,
                advertisePort = 0,
                connectedPlayers = 0,
                maxConnections = 0,
                expiresUtcTicks = 0,
                error = "local GameServer directory lease is unavailable or expired",
            });
        }

        return _backend.ResolveGameServerForMapAsync(
            new BackendGameServerResolveMapRequest
            {
                mapId = ServerMapId.Normalize(mapId),
                instanceId = (instanceId ?? string.Empty).Trim(),
            },
            cancellationToken);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.DirectoryHeartbeatSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                string leaseToken;
                lock (_gate)
                    leaseToken = _leaseToken;

                if (string.IsNullOrWhiteSpace(leaseToken))
                {
                    await TryRecoverRegistrationAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                BackendGameServerHeartbeatResponse heartbeat = await _backend
                    .HeartbeatGameServerAsync(
                        new BackendGameServerHeartbeatRequest
                        {
                            serverId = _options.ServerId,
                            leaseToken = leaseToken,
                            connectedPlayers = Volatile.Read(ref _connectedPlayers),
                            leaseSeconds = _options.DirectoryLeaseSeconds,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (heartbeat != null &&
                    heartbeat.success &&
                    heartbeat.accepted &&
                    heartbeat.expiresUtcTicks > DateTime.UtcNow.Ticks)
                {
                    lock (_gate)
                    {
                        // A concurrent shutdown may already have cleared state.
                        if (!_disposed && string.Equals(_leaseToken, leaseToken, StringComparison.Ordinal))
                            _expiresUtcTicks = heartbeat.expiresUtcTicks;
                    }
                    continue;
                }

                InvalidateLease();
                Console.Error.WriteLine(
                    $"GameServer directory heartbeat rejected for '{_options.ServerId}': " +
                    $"{heartbeat?.error ?? "directory lease unavailable"}. Re-registering.");
                await TryRecoverRegistrationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Do not invent a new lease state from transport failure. The previously
                // acknowledged lease remains locally valid only until its authoritative expiry.
                Console.Error.WriteLine(
                    $"GameServer directory heartbeat failed for '{_options.ServerId}': {ex.Message}");
            }
        }
    }

    private async Task TryRecoverRegistrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            BackendGameServerRegisterResponse registration = await RegisterAsync(cancellationToken)
                .ConfigureAwait(false);

            if (registration == null ||
                !registration.success ||
                string.IsNullOrWhiteSpace(registration.leaseToken) ||
                registration.expiresUtcTicks <= DateTime.UtcNow.Ticks)
            {
                InvalidateLease();
                Console.Error.WriteLine(
                    $"GameServer directory re-registration rejected for '{_options.ServerId}': " +
                    $"{registration?.error ?? "directory registration unavailable"}");
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                    return;
                _leaseToken = registration.leaseToken;
                _expiresUtcTicks = registration.expiresUtcTicks;
            }

            Console.WriteLine(
                $"GameServer directory lease recovered: serverId={_options.ServerId}, " +
                $"expiresUtc={new DateTime(registration.expiresUtcTicks, DateTimeKind.Utc):O}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            InvalidateLeaseIfExpired();
            Console.Error.WriteLine(
                $"GameServer directory re-registration failed for '{_options.ServerId}': {ex.Message}");
        }
    }

    private Task<BackendGameServerRegisterResponse> RegisterAsync(CancellationToken cancellationToken) =>
        _backend.RegisterGameServerAsync(
            new BackendGameServerRegisterRequest
            {
                serverId = _options.ServerId,
                advertiseHost = _options.AdvertiseHost,
                advertisePort = _options.AdvertisePort,
                maxConnections = _options.MaxConnections,
                connectedPlayers = Volatile.Read(ref _connectedPlayers),
                leaseSeconds = _options.DirectoryLeaseSeconds,
                maps = _maps,
            },
            cancellationToken);

    private void InvalidateLease()
    {
        lock (_gate)
        {
            _leaseToken = string.Empty;
            _expiresUtcTicks = 0;
        }
    }

    private void InvalidateLeaseIfExpired()
    {
        lock (_gate)
        {
            if (_expiresUtcTicks <= DateTime.UtcNow.Ticks)
            {
                _leaseToken = string.Empty;
                _expiresUtcTicks = 0;
            }
        }
    }

    private static BackendGameServerMapDto[] BuildMapList(ServerMapCatalog maps)
    {
        return maps.Snapshots
            .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.mapId))
            .Select(snapshot => new BackendGameServerMapDto
            {
                mapId = ServerMapId.Normalize(snapshot.mapId),
                instanceId = (snapshot.instanceId ?? string.Empty).Trim(),
            })
            .Where(map => map.mapId.Length > 0)
            .GroupBy(map => map.mapId + "\n" + map.instanceId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(map => map.mapId, StringComparer.Ordinal)
            .ThenBy(map => map.instanceId, StringComparer.Ordinal)
            .ToArray();
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        Task heartbeat;
        string leaseToken;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            cancellation = _loopCancellation;
            heartbeat = _heartbeatLoop;
            leaseToken = _leaseToken;
        }

        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (heartbeat != null)
        {
            try { heartbeat.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GameServer directory heartbeat shutdown failed: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(leaseToken))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                BackendGameServerUnregisterResponse response = _backend
                    .UnregisterGameServerAsync(
                        new BackendGameServerUnregisterRequest
                        {
                            serverId = _options.ServerId,
                            leaseToken = leaseToken,
                        },
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();

                if (response != null && response.success)
                {
                    Console.WriteLine(
                        $"GameServer directory lease released: serverId={_options.ServerId}, removed={response.removed}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"GameServer directory unregister rejected for '{_options.ServerId}': " +
                        $"{response?.error ?? "directory unregister unavailable"}");
                }
            }
            catch (Exception ex)
            {
                // Lease expiry is the crash/shutdown safety net. Do not turn graceful process
                // termination into a failure solely because Gateway disappeared first.
                Console.Error.WriteLine(
                    $"GameServer directory unregister failed for '{_options.ServerId}': {ex.Message}");
            }
        }

        lock (_gate)
        {
            _leaseToken = string.Empty;
            _expiresUtcTicks = 0;
            _started = false;
            _loopCancellation = null;
            _heartbeatLoop = null;
        }

        cancellation?.Dispose();
    }
}
