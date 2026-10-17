using System;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    // Uses the existing ServerCollisionWorld's static map and live door grid.
    // No new spatial index, messaging or per-actor collision subsystem.
    public sealed partial class ServerCollisionWorld
    {
        public bool IsStaticPopulationLaneCertified(
            WorldPosition from, WorldPosition to, float laneOffset,
            ServerCapsule capsule, float maximumSlopeDegrees)
        {
            float dx = to.X - from.X, dz = to.Z - from.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (!float.IsFinite(distance) || distance <= 0.05f ||
                !float.IsFinite(from.Y) || !float.IsFinite(to.Y))
                return false;
            float sideX = -dz / distance, sideZ = dx / distance;
            // This is load-time proof. No static queries are performed for an
            // actor walking/running/fleeing on a successful certified edge.
            float spacing = Math.Max(0.05f, Math.Min(0.15f, capsule.Radius * 0.4f));
            int samples = (int)MathF.Ceiling(distance / spacing);
            // A highly overlong edge should be subdivided with authored markers.
            if (samples <= 0 || samples > 1024) return false;
            float previousGroundY = 0f;
            for (int i = 0; i <= samples; ++i)
            {
                float t = (float)i / samples;
                var authored = new WorldPosition(
                    from.X + dx * t + sideX * laneOffset,
                    from.Y + (to.Y - from.Y) * t,
                    from.Z + dz * t + sideZ * laneOffset);
                if (!TryFindGround(authored, capsule.Radius, 0.18f, 0.18f,
                        maximumSlopeDegrees, out ServerGroundHit ground) ||
                    Math.Abs(ground.Position.Y - authored.Y) > 0.10f ||
                    !IsStandingCapsuleClearStatic(ground.Position, capsule, maximumSlopeDegrees))
                    return false;
                if (i > 0 && Math.Abs(ground.Position.Y - previousGroundY) > 0.22f)
                    return false;
                previousGroundY = ground.Position.Y;
            }
            return true;
        }

        public bool IsLiveRouteStepClear(
            WorldPosition from, WorldPosition to, ServerCapsule capsule,
            out int samples)
        {
            float dx = to.X - from.X, dz = to.Z - from.Z;
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (!float.IsFinite(length)) { samples = 0; return false; }
            // Sample LIVE blockers only, with clearance spacing smaller than the
            // capsule radius so thin doors cannot be skipped on a fast flee step.
            samples = Math.Max(1, (int)MathF.Ceiling(length /
                Math.Max(0.05f, capsule.Radius * 0.5f)));
            if (samples > 64) return false;
            float rad = capsule.Radius;
            QueryDynamicBlockers(
                Math.Min(from.X, to.X) - rad, Math.Min(from.Z, to.Z) - rad,
                Math.Max(from.X, to.X) + rad, Math.Max(from.Z, to.Z) + rad);
            // Keep the existing collision-world live dynamic blocker grid;
            // do not spend time querying it again for every sample.
            for (int i = 1; i <= samples; ++i)
            {
                float t = (float)i / samples;
                var position = new WorldPosition(from.X + dx * t,
                    from.Y + (to.Y - from.Y) * t, from.Z + dz * t);
                foreach (long id in _dynamicBlockerQueryScratch)
                    if (_dynamicBlockers.TryGetValue(id, out DynamicBlockerState state) &&
                        state.Enabled &&
                        CapsuleOverlapsOrientedBox(position, capsule, state.Definition))
                        return false;
            }
            return true;
        }
    }
}
