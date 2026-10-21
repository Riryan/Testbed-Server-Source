using Game.Shared.World;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Emergency rollback for the lazy 4m standing-capsule spatial index.
    ///
    /// IndexStandingCoarseCell previously built spatial data on the simulation
    /// movement path, introducing unbounded first-visit work and allocations.
    /// Until the baked map can create a validated immutable acceleration index
    /// before player simulation begins, use the existing authoritative capsule
    /// query. Keep this method as the compatibility entry point for the shared
    /// GroundedMove source and preserve its timing diagnostics.
    /// </summary>
    public sealed partial class ServerCollisionWorld
    {
        public bool IsStandingCapsuleClearIndexed(
            WorldPosition feet, ServerCapsule capsule, float maximumSlopeDegrees)
        {
            return IsStandingCapsuleClear(feet, capsule, maximumSlopeDegrees);
        }
    }
}
