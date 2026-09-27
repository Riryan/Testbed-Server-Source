using System.Diagnostics;

namespace MMODashboard;

internal sealed record GameServerLaunchSpec(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

internal readonly record struct GameServerControlStatus(
    string ServerId,
    bool Running,
    int ProcessId);

/// <summary>
/// Process-control boundary used by the Dashboard. V1 is local-process backed; a later
/// remote node controller can implement the same interface without changing Dashboard UI
/// semantics or GameServer identity handling.
/// </summary>
internal interface IGameServerControlPlane : IDisposable
{
    bool Start(
        GameServerInstanceConfig instance,
        GameServerLaunchSpec launch,
        Action<string, bool>? onLine,
        Action<int>? onExit);

    bool Stop(string serverId);
    void StopAll();
    GameServerControlStatus GetStatus(string serverId);
}

internal sealed class LocalGameServerControlPlane : IGameServerControlPlane
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _processes = new(StringComparer.Ordinal);
    private bool _disposed;

    public bool Start(
        GameServerInstanceConfig instance,
        GameServerLaunchSpec launch,
        Action<string, bool>? onLine,
        Action<int>? onExit)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(launch);

        string serverId = NormalizeServerId(instance.ServerId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryGetLiveProcessLocked(serverId, out _))
                return false;
        }

        Process? process = null;
        process = BatchRunner.StartExecutable(
            launch.ExecutablePath,
            launch.WorkingDirectory,
            launch.Arguments,
            onLine,
            code =>
            {
                lock (_gate)
                {
                    if (_processes.TryGetValue(serverId, out Process? current) &&
                        ReferenceEquals(current, process))
                    {
                        _processes.Remove(serverId);
                    }
                }
                onExit?.Invoke(code);
            });

        if (process == null)
            return false;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (TryGetLiveProcessLocked(serverId, out _))
            {
                BatchRunner.TryKillTree(process);
                return false;
            }
            _processes[serverId] = process;
        }

        return true;
    }

    public bool Stop(string serverId)
    {
        serverId = NormalizeServerId(serverId);
        Process? process;
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (!_processes.Remove(serverId, out process))
                return false;
        }

        BatchRunner.TryKillTree(process);
        return true;
    }

    public void StopAll()
    {
        Process[] processes;
        lock (_gate)
        {
            if (_disposed)
                return;
            processes = _processes.Values.Distinct().ToArray();
            _processes.Clear();
        }

        foreach (Process process in processes)
            BatchRunner.TryKillTree(process);
    }

    public GameServerControlStatus GetStatus(string serverId)
    {
        serverId = NormalizeServerId(serverId);
        lock (_gate)
        {
            if (_disposed)
                return new GameServerControlStatus(serverId, false, 0);
            if (!TryGetLiveProcessLocked(serverId, out Process? process))
                return new GameServerControlStatus(serverId, false, 0);

            int pid;
            try { pid = process.Id; }
            catch { pid = 0; }
            return new GameServerControlStatus(serverId, true, pid);
        }
    }

    public void Dispose()
    {
        StopAll();
        lock (_gate)
            _disposed = true;
    }

    private bool TryGetLiveProcessLocked(string serverId, out Process? process)
    {
        process = null;
        if (!_processes.TryGetValue(serverId, out Process? candidate))
            return false;

        try
        {
            if (!candidate.HasExited)
            {
                process = candidate;
                return true;
            }
        }
        catch
        {
        }

        _processes.Remove(serverId);
        return false;
    }

    private static string NormalizeServerId(string? value)
    {
        string serverId = (value ?? string.Empty).Trim();
        if (serverId.Length == 0)
            throw new InvalidOperationException("GameServer serverId is required.");
        return serverId;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LocalGameServerControlPlane));
    }
}
