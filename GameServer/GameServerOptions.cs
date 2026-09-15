using System.Globalization;
using Game.Shared.Backend;

namespace Game.GameServer;

internal sealed class GameServerOptions
{
    public int Port { get; private set; } = 7777;
    public int MaxConnections { get; private set; } = 4096;
    public string ConnectKey { get; private set; } = "SampleConnectKey";
    public string BackendInternalBaseUrl { get; private set; } = "http://127.0.0.1:8444";
    public string BackendKeyFile { get; private set; } = "../Data/game-server-auth.key";
    public string ServerId { get; private set; } = string.Empty;
    public string AdvertiseHost { get; private set; } = "127.0.0.1";
    public int AdvertisePort { get; private set; }
    public int DirectoryLeaseSeconds { get; private set; } = 45;
    public int DirectoryHeartbeatSeconds { get; private set; } = 10;
    public int AuthenticationTimeoutSeconds { get; private set; } = 60;
    public int MaxAdmissionAttemptsPerSession { get; private set; } = 5;
    public ushort TickRate { get; private set; } = 20;
    public double CombatTickRate { get; private set; } = 10.0;
    public double PassiveResourceHz { get; private set; } = 1.0;
    public bool CombatWireDiagnostics { get; private set; }
    public bool CombatTestDummy { get; private set; }
    public float AoiRange { get; private set; } = 48f;
    public float CombatPresentationRange { get; private set; } = 36f;
    public float AoiExitPadding { get; private set; } = 8f;
    public float AoiCellSize { get; private set; } = 32f;
    public int AoiCandidateChecksPerGameplayTick { get; private set; } = 2048;
    public int AoiEdgeAdditionsPerGameplayTick { get; private set; } = 256;
    public bool ReplicationLodEnabled { get; private set; } = true;
    public int ReplicationPacketsPerObserverFrame { get; private set; } = 4;
    public int ReplicationPacketsPerFrame { get; private set; } = 512;
    public int ReplicationBytesPerFrame { get; private set; } = 393216;
    public double ReplicationMillisecondsPerFrame { get; private set; } = 2.0;
    // Unified outbound budget. 8 KiB/s is the target steady-state gameplay budget per CCU;
    // the burst bucket absorbs admission, AOI and short combat spikes while queues stage
    // noncritical reliable work instead of allowing one observer to monopolize the socket.
    public int OutboundBytesPerConnectionSecond { get; private set; } = 8192;
    public int OutboundBurstBytesPerConnection { get; private set; } = 131072;
    public int OutboundCriticalReserveBytes { get; private set; } = 16384;
    // Hard application-level queued-byte bound per connection. Normal/background
    // unreliable traffic is shed at the limit; non-droppable traffic disconnects a
    // slow consumer rather than allowing pooled queue memory to grow without bound.
    public int OutboundMaxQueuedBytesPerConnection { get; private set; } = 1048576;
    public int OutboundPacketsPerConnectionFrame { get; private set; } = 8;
    public int OutboundPacketsPerFrame { get; private set; } = 1024;
    public int OutboundBytesPerFrame { get; private set; } = 131072;
    public int PopulationActorsPerAiTick { get; private set; } = 4096;
    public double PopulationDeadDecaySeconds { get; private set; } = 60.0;
    public double DroppedItemLifetimeSeconds { get; private set; } = 300.0;
    public int MaxTransientDroppedItems { get; private set; } = 5000;
    public int DroppedItemCleanupTarget { get; private set; } = 4500;
    // Persistence sweeps are cheap eligibility checks. Actual soft-state saves are
    // age-gated and staggered across the preferred..maximum dirty window.
    public double CharacterCheckpointSweepIntervalSeconds { get; private set; } = 30.0;
    public double CharacterCheckpointPreferredAgeSeconds { get; private set; } = 900.0;
    public double CharacterCheckpointMaximumAgeSeconds { get; private set; } = 1800.0;
    public int CharacterCheckpointBatchSize { get; private set; } = BackendServiceContracts.MaxCharacterBatchSize;
    public int CharacterCheckpointMaxBatchesPerCycle { get; private set; } = 8;
    public string MapDataDirectory { get; private set; } = "../Content/Maps";
    public bool RequireMapData { get; private set; }
    public string StaffAuthorizationFile { get; private set; } = "../Content/StaffAuthorizations.json";
    public string StaffAuditFile { get; private set; } = "../Logs/StaffAudit.jsonl";

    public static GameServerOptions Parse(string[] args)
    {
        var result = new GameServerOptions();
        for (int i = 0; i < args.Length; ++i)
        {
            string arg = args[i] ?? string.Empty;
            string value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            switch (arg)
            {
                case "--port":
                    result.Port = ParseInt(value, 1, 65535, "port");
                    ++i;
                    break;
                case "--max-connections":
                    result.MaxConnections = ParseInt(value, 1, 100000, "max-connections");
                    ++i;
                    break;
                case "--connect-key":
                    result.ConnectKey = Require(value, "connect-key");
                    ++i;
                    break;
                case "--backend-internal":
                    result.BackendInternalBaseUrl = Require(value, "backend-internal").TrimEnd('/');
                    ++i;
                    break;
                case "--backend-key-file":
                    result.BackendKeyFile = Require(value, "backend-key-file");
                    ++i;
                    break;
                case "--server-id":
                    result.ServerId = Require(value, "server-id");
                    ++i;
                    break;
                case "--advertise-host":
                    result.AdvertiseHost = Require(value, "advertise-host");
                    ++i;
                    break;
                case "--advertise-port":
                    result.AdvertisePort = ParseInt(value, 1, 65535, "advertise-port");
                    ++i;
                    break;
                case "--directory-lease-seconds":
                    result.DirectoryLeaseSeconds = ParseInt(value, 15, 120, "directory-lease-seconds");
                    ++i;
                    break;
                case "--directory-heartbeat-seconds":
                    result.DirectoryHeartbeatSeconds = ParseInt(value, 1, 60, "directory-heartbeat-seconds");
                    ++i;
                    break;
                case "--auth-timeout-seconds":
                    result.AuthenticationTimeoutSeconds = ParseInt(value, 10, 300, "auth-timeout-seconds");
                    ++i;
                    break;
                case "--max-admission-attempts":
                    result.MaxAdmissionAttemptsPerSession = ParseInt(value, 1, 20, "max-admission-attempts");
                    ++i;
                    break;
                case "--tick-rate":
                    result.TickRate = checked((ushort)ParseInt(value, 1, ushort.MaxValue, "tick-rate"));
                    ++i;
                    break;
                case "--combat-tick-rate":
                    result.CombatTickRate = ParseDouble(value, 1.0, 60.0, "combat-tick-rate");
                    ++i;
                    break;
                case "--passive-resource-hz":
                    result.PassiveResourceHz = ParseDouble(value, 0.1, 10.0, "passive-resource-hz");
                    ++i;
                    break;
                case "--combat-wire-diagnostics":
                    result.CombatWireDiagnostics = true;
                    break;
                case "--combat-test-dummy":
                    result.CombatTestDummy = true;
                    break;
                case "--aoi-range":
                    result.AoiRange = ParseFloat(value, 1f, 2000f, "aoi-range");
                    ++i;
                    break;
                case "--combat-presentation-range":
                    result.CombatPresentationRange = ParseFloat(value, 1f, 2000f, "combat-presentation-range");
                    ++i;
                    break;
                case "--aoi-exit-padding":
                    result.AoiExitPadding = ParseFloat(value, 0f, 500f, "aoi-exit-padding");
                    ++i;
                    break;
                case "--aoi-cell-size":
                    result.AoiCellSize = ParseFloat(value, 1f, 2000f, "aoi-cell-size");
                    ++i;
                    break;
                case "--aoi-candidate-checks-per-gameplay-tick":
                    result.AoiCandidateChecksPerGameplayTick = ParseInt(
                        value, 1, 1_000_000, "aoi-candidate-checks-per-gameplay-tick");
                    ++i;
                    break;
                case "--aoi-edge-additions-per-gameplay-tick":
                    result.AoiEdgeAdditionsPerGameplayTick = ParseInt(
                        value, 1, 1_000_000, "aoi-edge-additions-per-gameplay-tick");
                    ++i;
                    break;
                case "--disable-replication-lod":
                    result.ReplicationLodEnabled = false;
                    break;
                case "--replication-packets-per-observer-frame":
                    result.ReplicationPacketsPerObserverFrame = ParseInt(value, 1, 64, "replication-packets-per-observer-frame");
                    ++i;
                    break;
                case "--replication-packets-per-frame":
                    result.ReplicationPacketsPerFrame = ParseInt(value, 1, 65536, "replication-packets-per-frame");
                    ++i;
                    break;
                case "--replication-bytes-per-frame":
                    result.ReplicationBytesPerFrame = ParseInt(value, 1024, 64 * 1024 * 1024, "replication-bytes-per-frame");
                    ++i;
                    break;
                case "--replication-milliseconds-per-frame":
                    result.ReplicationMillisecondsPerFrame = ParseDouble(value, 0.1, 50.0, "replication-milliseconds-per-frame");
                    ++i;
                    break;
                case "--outbound-bytes-per-connection-second":
                    result.OutboundBytesPerConnectionSecond = ParseInt(value, 1024, 1024 * 1024, "outbound-bytes-per-connection-second");
                    ++i;
                    break;
                case "--outbound-burst-bytes-per-connection":
                    result.OutboundBurstBytesPerConnection = ParseInt(value, 4096, 4 * 1024 * 1024, "outbound-burst-bytes-per-connection");
                    ++i;
                    break;
                case "--outbound-critical-reserve-bytes":
                    result.OutboundCriticalReserveBytes = ParseInt(value, 0, 1024 * 1024, "outbound-critical-reserve-bytes");
                    ++i;
                    break;
                case "--outbound-max-queued-bytes-per-connection":
                    result.OutboundMaxQueuedBytesPerConnection = ParseInt(value, 16384, 16 * 1024 * 1024, "outbound-max-queued-bytes-per-connection");
                    ++i;
                    break;
                case "--outbound-packets-per-connection-frame":
                    result.OutboundPacketsPerConnectionFrame = ParseInt(value, 1, 128, "outbound-packets-per-connection-frame");
                    ++i;
                    break;
                case "--outbound-packets-per-frame":
                    result.OutboundPacketsPerFrame = ParseInt(value, 1, 65536, "outbound-packets-per-frame");
                    ++i;
                    break;
                case "--outbound-bytes-per-frame":
                    result.OutboundBytesPerFrame = ParseInt(value, 4096, 64 * 1024 * 1024, "outbound-bytes-per-frame");
                    ++i;
                    break;
                case "--population-actors-per-ai-tick":
                    result.PopulationActorsPerAiTick = ParseInt(value, 1, 100000, "population-actors-per-ai-tick");
                    ++i;
                    break;
                case "--population-dead-decay-seconds":
                    result.PopulationDeadDecaySeconds = ParseDouble(value, 1.0, 86400.0, "population-dead-decay-seconds");
                    ++i;
                    break;
                case "--dropped-item-lifetime-seconds":
                    result.DroppedItemLifetimeSeconds = ParseDouble(value, 1.0, 86400.0, "dropped-item-lifetime-seconds");
                    ++i;
                    break;
                case "--max-transient-dropped-items":
                    result.MaxTransientDroppedItems = ParseInt(value, 1, 1_000_000, "max-transient-dropped-items");
                    ++i;
                    break;
                case "--dropped-item-cleanup-target":
                    result.DroppedItemCleanupTarget = ParseInt(value, 1, 1_000_000, "dropped-item-cleanup-target");
                    ++i;
                    break;
                case "--checkpoint-interval-seconds":
                    // Backward-compatible alias for the cheap eligibility sweep. Old
                    // launch scripts that specify 15 seconds therefore do not silently
                    // restore 15-second database checkpoints.
                    result.CharacterCheckpointSweepIntervalSeconds = ParseDouble(
                        value, 1.0, 300.0, "checkpoint-interval-seconds");
                    ++i;
                    break;
                case "--checkpoint-sweep-seconds":
                    result.CharacterCheckpointSweepIntervalSeconds = ParseDouble(
                        value, 5.0, 300.0, "checkpoint-sweep-seconds");
                    ++i;
                    break;
                case "--checkpoint-preferred-age-seconds":
                    result.CharacterCheckpointPreferredAgeSeconds = ParseDouble(
                        value, 30.0, 7200.0, "checkpoint-preferred-age-seconds");
                    ++i;
                    break;
                case "--checkpoint-max-age-seconds":
                    result.CharacterCheckpointMaximumAgeSeconds = ParseDouble(
                        value, 60.0, 14400.0, "checkpoint-max-age-seconds");
                    ++i;
                    break;
                case "--checkpoint-batch-size":
                    result.CharacterCheckpointBatchSize = ParseInt(value, 1, BackendServiceContracts.MaxCharacterBatchSize, "checkpoint-batch-size");
                    ++i;
                    break;
                case "--checkpoint-max-batches-per-cycle":
                    result.CharacterCheckpointMaxBatchesPerCycle = ParseInt(value, 1, 64, "checkpoint-max-batches-per-cycle");
                    ++i;
                    break;
                case "--map-data-dir":
                    result.MapDataDirectory = Require(value, "map-data-dir");
                    ++i;
                    break;
                case "--require-map-data":
                    result.RequireMapData = true;
                    break;
                case "--staff-auth-file":
                    result.StaffAuthorizationFile = Require(value, "staff-auth-file");
                    ++i;
                    break;
                case "--staff-audit-file":
                    result.StaffAuditFile = Require(value, "staff-audit-file");
                    ++i;
                    break;
            }
        }

        if (result.AdvertisePort == 0)
            result.AdvertisePort = result.Port;

        if (result.CombatTickRate > result.TickRate)
            throw new InvalidOperationException("--combat-tick-rate cannot exceed --tick-rate.");
        if (result.CombatPresentationRange > result.AoiRange)
            throw new InvalidOperationException("--combat-presentation-range cannot exceed --aoi-range.");

        if (result.CharacterCheckpointMaximumAgeSeconds < result.CharacterCheckpointPreferredAgeSeconds)
            throw new InvalidOperationException("--checkpoint-max-age-seconds cannot be less than the preferred checkpoint age.");

        if (result.DroppedItemCleanupTarget > result.MaxTransientDroppedItems)
            throw new InvalidOperationException("--dropped-item-cleanup-target cannot exceed --max-transient-dropped-items.");

        if (result.DirectoryHeartbeatSeconds * 2 > result.DirectoryLeaseSeconds)
        {
            throw new InvalidOperationException(
                "--directory-heartbeat-seconds must be no more than half of --directory-lease-seconds.");
        }

        if (string.IsNullOrWhiteSpace(result.ServerId))
            result.ServerId = $"local-{Environment.MachineName}-{result.Port}";

        if (result.ServerId.Length > 96)
            throw new InvalidOperationException("--server-id must be 96 characters or fewer.");
        if (string.IsNullOrWhiteSpace(result.AdvertiseHost) || result.AdvertiseHost.Length > 255)
            throw new InvalidOperationException("--advertise-host must be 1-255 characters.");

        // AOI queries probe a square of spatial-hash cells around each observer. Reject
        // pathological range/cell-size combinations before startup so a configuration
        // typo cannot turn one movement reconciliation into millions of dictionary probes.
        double exitRange = result.AoiRange + result.AoiExitPadding;
        int queryCellRadius = Math.Max(1, (int)Math.Ceiling(exitRange / result.AoiCellSize));
        long queryDiameter = (2L * queryCellRadius) + 1L;
        long queryCellProbes = checked(queryDiameter * queryDiameter);
        const long MaxAoiQueryCellProbes = 1024L;
        if (queryCellProbes > MaxAoiQueryCellProbes)
        {
            throw new InvalidOperationException(
                $"AOI configuration would probe {queryCellProbes} spatial cells per query " +
                $"(range={result.AoiRange}, exitPadding={result.AoiExitPadding}, cellSize={result.AoiCellSize}). " +
                $"Increase --aoi-cell-size or reduce --aoi-range/--aoi-exit-padding; maximum is {MaxAoiQueryCellProbes} cells.");
        }

        if (!Uri.TryCreate(result.BackendInternalBaseUrl, UriKind.Absolute, out Uri uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--backend-internal must be an absolute HTTP URL.");
        }

        return result;
    }

    private static string Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"--{name} requires a value.");
        return value.Trim();
    }

    private static float ParseFloat(string value, float min, float max, string name)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed) || parsed < min || parsed > max)
        {
            throw new InvalidOperationException($"--{name} must be in range {min}..{max}.");
        }
        return parsed;
    }


    private static double ParseDouble(string value, double min, double max, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed < min || parsed > max)
        {
            throw new InvalidOperationException($"--{name} must be in range {min}..{max}.");
        }
        return parsed;
    }

    private static int ParseInt(string value, int min, int max, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < min || parsed > max)
        {
            throw new InvalidOperationException($"--{name} must be in range {min}..{max}.");
        }
        return parsed;
    }
}
