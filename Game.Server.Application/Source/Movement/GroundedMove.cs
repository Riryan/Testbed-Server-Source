using System;
using Game.Server.Application.World;
using Game.Shared.World;

namespace Game.Server.Application.Movement
{
    /// <summary>
    /// Canonical grounded PLAYER horizontal movement, compiled unchanged into Unity
    /// and standalone GameServer. CollisionWorld is the existing baked static world;
    /// this class owns the shared step/clearance/acceptance rules only.
    ///
    /// This is not a jump, fall, teleport, loading or Population route controller.
    /// No X/Z wall slide is introduced. Every attempted move is all-or-stop so the
    /// client can suppress known rejected input without changing the server result.
    /// </summary>
    public static class GroundedMove
    {
        public static WorldPosition Resolve(
            ServerCollisionWorld world,
            WorldPosition start,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            out bool blocked)
        {
            blocked = false;
            if (world == null)
                return start;
            if (!Finite(deltaX) || !Finite(deltaZ) ||
                !Finite(start.X) || !Finite(start.Y) || !Finite(start.Z))
            {
                blocked = true;
                return start;
            }

            float length = MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            if (length <= 0.000001f)
                return start;

            float stepLength = Math.Max(0.05f, capsule.Radius * 0.5f);
            int count = (int)Math.Ceiling(length / stepLength);
            // A legitimate 20 Hz player displacement is well below this ceiling.
            // Do not accidentally validate a long teleport using sparse samples.
            if (count > 16)
            {
                blocked = true;
                return start;
            }

            float sx = deltaX / count;
            float sz = deltaZ / count;
            WorldPosition cursor = start;
            for (int i = 0; i < count; ++i)
            {
                if (!TryStep(world, cursor, sx, sz, capsule, stepHeight,
                        groundSnapDistance, maximumSlopeDegrees, out WorldPosition next))
                {
                    blocked = true;
                    return start;
                }
                cursor = next;
            }
            return cursor;
        }

        private static bool TryStep(
            ServerCollisionWorld world, WorldPosition start,
            float dx, float dz, ServerCapsule capsule,
            float stepHeight, float groundSnapDistance,
            float slope, out WorldPosition next)
        {
            next = start;
            WorldPosition target = new WorldPosition(start.X + dx, start.Y, start.Z + dz);
            float up = Math.Max(0.05f, stepHeight + 0.05f);
            float down = Math.Max(0.05f, groundSnapDistance);
            if (!world.TryFindGround(target, capsule.Radius, up, down,
                    slope, out ServerGroundHit support))
                return false;

            float groundY = support.Position.Y;
            if (groundY > start.Y + stepHeight + 0.05f ||
                groundY < start.Y - groundSnapDistance - 0.05f)
                return false;

            WorldPosition footOnGround = new WorldPosition(target.X, groundY, target.Z);
            if (world.IsStandingCapsuleClear(footOnGround, capsule, slope))
            {
                // Preserve elevation across a descending tread/riser until it is
                // physically safe to lower the standing capsule.
                if (groundY < start.Y - 0.01f &&
                    world.IsStandingCapsuleClear(target, capsule, slope) &&
                    HasTreadAhead(world, target, dx, dz, start.Y,
                        capsule.Radius, stepHeight, slope, groundY))
                {
                    next = target;
                    return true;
                }
                // A rising step must clear the full intervening vertical path.
                if (groundY > start.Y + 0.01f &&
                    !CanRaise(world, start, groundY, capsule, slope))
                    return false;
                next = footOnGround;
                return true;
            }

            if (groundY < start.Y - 0.01f &&
                world.IsStandingCapsuleClear(target, capsule, slope))
            {
                next = target;
                return true;
            }

            // Stair rise before the capsule center reaches the upper tread.
            float planar = MathF.Sqrt(dx * dx + dz * dz);
            if (planar <= 0.000001f)
                return false;
            float x = target.X + dx / planar * capsule.Radius;
            float z = target.Z + dz / planar * capsule.Radius;
            if (world.TryFindGround(new WorldPosition(x, start.Y, z),
                    0f, up, down, slope, out ServerGroundHit ahead) &&
                ahead.Position.Y > groundY + 0.01f &&
                ahead.Position.Y >= start.Y - 0.01f &&
                ahead.Position.Y <= start.Y + stepHeight + 0.05f)
            {
                WorldPosition raised = new WorldPosition(target.X, ahead.Position.Y, target.Z);
                if (CanRaise(world, start, raised.Y, capsule, slope) &&
                    world.IsStandingCapsuleClear(raised, capsule, slope))
                {
                    next = raised;
                    return true;
                }
            }
            return false;
        }

        private static bool HasTreadAhead(
            ServerCollisionWorld world, WorldPosition target,
            float dx, float dz, float oldY,
            float radius, float stepHeight, float slope, float centerY)
        {
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (length <= 0.000001f) return false;
            float x = target.X + dx / length * radius;
            float z = target.Z + dz / length * radius;
            float probe = Math.Max(0.05f, stepHeight + 0.05f);
            return world.TryFindGround(new WorldPosition(x, oldY, z),
                       0f, probe, probe, slope, out ServerGroundHit hit) &&
                   hit.Position.Y > centerY + 0.01f &&
                   hit.Position.Y >= oldY - 0.01f &&
                   hit.Position.Y <= oldY + stepHeight + 0.05f;
        }

        private static bool CanRaise(ServerCollisionWorld world,
            WorldPosition start, float raisedY, ServerCapsule capsule, float slope)
        {
            float rise = raisedY - start.Y;
            if (rise < 0f) return false;
            float unit = Math.Max(0.05f, capsule.Radius * 0.5f);
            int count = Math.Clamp((int)Math.Ceiling(rise / unit), 1, 12);
            for (int i = 1; i <= count; ++i)
            {
                float y = start.Y + rise * i / count;
                if (!world.IsStandingCapsuleClear(
                        new WorldPosition(start.X, y, start.Z), capsule, slope))
                    return false;
            }
            return true;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
