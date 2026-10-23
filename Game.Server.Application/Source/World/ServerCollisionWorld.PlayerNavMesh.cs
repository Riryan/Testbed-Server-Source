using System;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    /// <summary>
    /// The existing agent-eroded baked NavMesh is used only to find the feet
    /// height for server-integrated Player intent. This is NOT a physics motor
    /// or a path solver: no static capsule checks, no segment sweeps, no Detour
    /// searches, and no dynamic blocker lookup are performed here.
    /// </summary>
    public sealed partial class ServerCollisionWorld
    {
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
            blocked = true;

            if (_safeMovementTriangleGrid.Count == 0 ||
                capsule.Radius > _map.safeMovementRadius + 0.0001f ||
                capsule.Height > _map.safeMovementHeight + 0.0001f)
                return false;

            if (!float.IsFinite(startFeet.X) || !float.IsFinite(startFeet.Y) ||
                !float.IsFinite(startFeet.Z) || !float.IsFinite(deltaX) ||
                !float.IsFinite(deltaZ))
                return true;

            float targetX = startFeet.X + deltaX;
            float targetZ = startFeet.Z + deltaZ;
            if (!float.IsFinite(targetX) || !float.IsFinite(targetZ))
                return true;

            float minimumNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float maximumRise = stepHeight + 0.05f;
            float maximumDrop = groundSnapDistance + 0.05f;

            // Normal flat-ground movement: one previously identified triangle,
            // two inexpensive barycentric heights, and no collection search.
            // A convex triangle also contains the entire segment when both
            // endpoints lie inside it; an independent segment sweep is redundant.
            bool hasCachedStart = false;
            float cachedStartY = 0f;
            if (cachedTriangleIndex >= 0 &&
                cachedTriangleIndex < _safeMovementTriangles.Length)
            {
                ServerCollisionTriangle cached = _safeMovementTriangles[cachedTriangleIndex];
                if (cached.normalY >= minimumNormalY &&
                    TryTriangleHeightAtXZ(cached, startFeet.X, startFeet.Z,
                        out cachedStartY) &&
                    Math.Abs(cachedStartY - startFeet.Y) <= 0.40f)
                {
                    hasCachedStart = true;
                    if (TryTriangleHeightAtXZ(cached, targetX, targetZ,
                            out float targetNavY))
                    {
                        // Preserve the offset between voxelized NavMesh height
                        // and physical feet height already established at entry.
                        float targetFeetY = startFeet.Y + targetNavY - cachedStartY;
                        float rise = targetFeetY - startFeet.Y;
                        if (rise <= maximumRise && rise >= -maximumDrop)
                        {
                            resolved = new WorldPosition(targetX, targetFeetY, targetZ);
                            blocked = false;
                            return true;
                        }
                    }
                }
            }

            // Triangle transition: inspect only the already-indexed triangles
            // under the proposed endpoint. No whole-path search or physics query.
            QuerySafeMovementTriangles(targetX, targetZ, targetX, targetZ);
            int bestIndex = -1;
            float bestFeetY = startFeet.Y;
            float bestError = float.MaxValue;
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < minimumNormalY ||
                    !TryTriangleHeightAtXZ(triangle, targetX, targetZ,
                        out float targetNavY))
                    continue;

                float feetY = targetNavY;
                if (hasCachedStart)
                {
                    feetY = startFeet.Y + targetNavY - cachedStartY;
                }
                else if (TryTriangleHeightAtXZ(triangle,
                             startFeet.X, startFeet.Z, out float startNavY) &&
                         Math.Abs(startNavY - startFeet.Y) <= 0.40f)
                {
                    feetY = startFeet.Y + targetNavY - startNavY;
                }

                float rise = feetY - startFeet.Y;
                if (rise > maximumRise || rise < -maximumDrop)
                    continue;

                // When multiple floors overlap in X/Z, stay near the current
                // authoritative feet rather than teleporting to a distant floor.
                float error = Math.Abs(targetNavY - startFeet.Y);
                if (error >= bestError)
                    continue;
                bestError = error;
                bestIndex = index;
                bestFeetY = feetY;
            }

            if (bestIndex < 0)
            {
                cachedTriangleIndex = -1;
                return true;
            }

            cachedTriangleIndex = bestIndex;
            resolved = new WorldPosition(targetX, bestFeetY, targetZ);
            blocked = false;
            return true;
        }

        /// <summary>
        /// Sample the already-indexed baked walk surface only when descending
        /// through a possible landing. There is no physics sweep, capsule/triangle
        /// collision or repeated ground probe. The accepted floor is the highest
        /// walkable surface crossed by this vertical step at the new X/Z location.
        /// </summary>
        public bool TryFindPlayerBakedLandingSupport(
            float previousFeetY,
            WorldPosition nextFeet,
            ServerCapsule capsule,
            float maximumSlopeDegrees,
            out float groundY)
        {
            groundY = 0f;
            if (_safeMovementTriangleGrid.Count == 0 ||
                capsule.Radius > _map.safeMovementRadius + 0.0001f ||
                capsule.Height > _map.safeMovementHeight + 0.0001f ||
                !float.IsFinite(previousFeetY) ||
                !float.IsFinite(nextFeet.X) || !float.IsFinite(nextFeet.Y) ||
                !float.IsFinite(nextFeet.Z) || nextFeet.Y > previousFeetY)
                return false;

            // Small tolerance accommodates baked NavMesh vs physical feet offset
            // without accepting floors that the falling trajectory has not reached.
            float upper = previousFeetY + 0.03f;
            float lower = nextFeet.Y - 0.15f;
            float minimumNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            QuerySafeMovementTriangles(nextFeet.X, nextFeet.Z,
                nextFeet.X, nextFeet.Z);

            bool found = false;
            float highest = float.NegativeInfinity;
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < minimumNormalY ||
                    !TryTriangleHeightAtXZ(triangle, nextFeet.X,
                        nextFeet.Z, out float y) ||
                    y > upper || y < lower || y <= highest)
                    continue;
                highest = y;
                found = true;
            }

            if (found)
                groundY = highest;
            return found;
        }

    }
}
