namespace Game.GameServer.Replication;

/// <summary>
/// Distance-based movement replication policy carried forward from the validated Unity
/// server foundation. Critical/discrete state changes bypass this cadence gate; ordinary
/// transform/locomotion snapshots are throttled per observer edge.
/// </summary>
internal sealed class ReplicationLodPolicy
{
    private readonly double _nearInterval;
    private readonly double _midInterval;
    private readonly double _farInterval;
    private readonly double _edgeInterval;
    private readonly double _beyondInterval;

    private const float NearDistanceSquared = 15f * 15f;
    private const float MidDistanceSquared = 40f * 40f;
    private const float FarDistanceSquared = 80f * 80f;
    private const float EdgeDistanceSquared = 160f * 160f;

    public ReplicationLodPolicy(int serverTickRate)
    {
        double maxHz = Math.Max(1d, serverTickRate);
        _nearInterval = 1d / Math.Min(maxHz, 20d);
        _midInterval = 1d / Math.Min(maxHz, 10d);
        _farInterval = 1d / Math.Min(maxHz, 5d);
        _edgeInterval = 1d / Math.Min(maxHz, 2d);
        _beyondInterval = 1d / Math.Min(maxHz, 1d);
    }

    public double GetIntervalSeconds(float distanceSquared)
    {
        if (distanceSquared <= NearDistanceSquared)
            return _nearInterval;
        if (distanceSquared <= MidDistanceSquared)
            return _midInterval;
        if (distanceSquared <= FarDistanceSquared)
            return _farInterval;
        if (distanceSquared <= EdgeDistanceSquared)
            return _edgeInterval;
        return _beyondInterval;
    }
}
