using System;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Player NavMesh movement helpers, reusing the EXISTING 4m certified-triangle
    /// index and live dynamic-blocker index. No new map grid or wire state.
    /// </summary>
    public sealed partial class ServerCollisionWorld
    {
        public bool HasBakedDynamicBlockerCandidatesForMove(
            WorldPosition start, float dx, float dz, float radius)
        {
            if (!float.IsFinite(start.X) || !float.IsFinite(start.Z) ||
                !float.IsFinite(dx) || !float.IsFinite(dz) ||
                !float.IsFinite(radius) || radius <= 0f)
                return true;

            float endX = start.X + dx;
            float endZ = start.Z + dz;
            QueryDynamicBlockers(
                Math.Min(start.X, endX) - radius,
                Math.Min(start.Z, endZ) - radius,
                Math.Max(start.X, endX) + radius,
                Math.Max(start.Z, endZ) + radius);
            return _dynamicBlockerQueryScratch.Count != 0;
        }

        /// <summary>
        /// Fast Player-only path for consecutive moves within the same trusted,
        /// agent-eroded Recast triangle. Both endpoints inside a convex triangle
        /// imply the entire connecting segment stays inside that certified surface.
        /// The destination is grounded exactly once against the existing physical
        /// support mesh, preserving the old height and step/snap constraints.
        /// On a triangle transition, use the existing whole-segment resolver.
        /// Caller must have excluded nearby dynamic-blocker candidates; the existing
        /// resolver independently checks dynamic blockers on the fallback path.
        /// A triangle index is only valid for the same ServerCollisionWorld instance.
        /// </summary>
        public bool TryResolvePlayerCertifiedGrounded(
            WorldPosition startFeet,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            ref int cachedTriangleIndex,
            out WorldPosition resolved,
            out bool blocked)
        {
            resolved = startFeet;
            blocked = false;

            if (cachedTriangleIndex >= 0 &&
                cachedTriangleIndex < _safeMovementTriangles.Length &&
                _safeMovementTriangleGrid.Count != 0 &&
                capsule.Radius <= _map.safeMovementRadius + 0.0001f &&
                capsule.Height <= _map.safeMovementHeight + 0.0001f &&
                float.IsFinite(startFeet.X) &&
                float.IsFinite(startFeet.Y) &&
                float.IsFinite(startFeet.Z) &&
                float.IsFinite(deltaX) && float.IsFinite(deltaZ) &&
                TryResolveInsideCertifiedTriangle(
                    _safeMovementTriangles[cachedTriangleIndex],
                    startFeet, deltaX, deltaZ, capsule,
                    stepHeight, groundSnapDistance,
                    maximumSlopeDegrees, out resolved))
            {
                return true;
            }

            // The cached triangle did not completely contain the proposed move.
            // No speculative acceptance: invoke the existing certified resolver,
            // with its full path coverage and established rejection semantics.
            cachedTriangleIndex = -1;
            bool handled = TryResolveCertifiedGroundedNoSlide(
                startFeet, deltaX, deltaZ, capsule,
                stepHeight, groundSnapDistance, maximumSlopeDegrees,
                out resolved, out blocked);

            if (!handled || blocked || resolved == startFeet)
                return handled;

            // The existing segment solver already populated this SAME scratch set.
            // Recover one containing triangle without another grid query. If the
            // endpoint falls on a seam, the regular resolver remains the fallback.
            float normalFloor = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < normalFloor ||
                    !TryTriangleHeightAtXZ(triangle, resolved.X, resolved.Z, out float y) ||
                    Math.Abs(y - resolved.Y) > 0.15f)
                    continue;
                cachedTriangleIndex = index;
                break;
            }
            return handled;
        }

        private bool TryResolveInsideCertifiedTriangle(
            ServerCollisionTriangle triangle,
            WorldPosition startFeet,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            out WorldPosition resolved)
        {
            resolved = startFeet;
            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            if (triangle.normalY < minNormalY ||
                !TryTriangleHeightAtXZ(triangle, startFeet.X, startFeet.Z, out float startY) ||
                Math.Abs(startY - startFeet.Y) > 0.15f)
                return false;

            float targetX = startFeet.X + deltaX;
            float targetZ = startFeet.Z + deltaZ;
            // The existing clipper is the exact certified segment contract. The
            // barycentric height routine deliberately tolerates slight edge error,
            // so require full segment coverage before fast-path acceptance.
            if (!float.IsFinite(targetX) || !float.IsFinite(targetZ) ||
                !TryClipSegmentToTriangle(startFeet.X, startFeet.Z,
                    targetX, targetZ, triangle, out float enter, out float exit) ||
                enter > 0.0001f || exit < 0.9999f ||
                !TryTriangleHeightAtXZ(triangle, targetX, targetZ, out float targetY) ||
                targetY > startFeet.Y + stepHeight + 0.05f ||
                targetY < startFeet.Y - groundSnapDistance - 0.05f)
                return false;

            if (!TryFindGround(
                    new WorldPosition(targetX, targetY, targetZ),
                    capsule.Radius,
                    Math.Max(0.05f, stepHeight + 0.05f),
                    Math.Max(0.05f, groundSnapDistance),
                    maximumSlopeDegrees, out ServerGroundHit support) ||
                Math.Abs(support.Position.Y - targetY) > 0.20f ||
                support.Position.Y > startFeet.Y + stepHeight + 0.05f ||
                support.Position.Y < startFeet.Y - groundSnapDistance - 0.05f)
                return false;

            resolved = new WorldPosition(targetX, support.Position.Y, targetZ);
            return true;
        }
    }
}
