using System;
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

        /// <summary>
        /// True only for a short grounded step that reduces an existing, shallow
        /// STATIC standing-capsule overlap. This is not a collision bypass:
        /// the step must remain on the same side of the obstacle, must introduce
        /// no new contact, and must not intersect an enabled dynamic blocker.
        ///
        /// The ordinary standing predicate remains the only acceptance path
        /// when the player starts in a valid, non-overlapping position.
        /// </summary>
        public bool CanRetreatFromShallowStandingOverlap(
            WorldPosition from, WorldPosition to,
            ServerCapsule capsule, float maximumSlopeDegrees)
        {
            if (!IsFinite(from.X) || !IsFinite(from.Y) || !IsFinite(from.Z) ||
                !IsFinite(to.X) || !IsFinite(to.Y) || !IsFinite(to.Z))
                return false;

            float dx = to.X - from.X;
            float dz = to.Z - from.Z;
            float lengthSq = dx * dx + dz * dz;
            float maximumStep = Math.Max(0.05f, capsule.Radius * 0.5f) + 0.001f;
            if (lengthSq < 0.00000001f || lengthSq > maximumStep * maximumStep ||
                Math.Abs(to.Y - from.Y) > 0.05f)
                return false;

            // A blocked step onto a physically continuous level tile seam is
            // distinct from escaping penetration into a real blocking mesh.
            if (CanTraverseLevelTileSeam(from, to, capsule, maximumSlopeDegrees))
                return true;

            float radius = capsule.Radius;
            float floorNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float minX = Math.Min(from.X, to.X) - radius;
            float maxX = Math.Max(from.X, to.X) + radius;
            float minZ = Math.Min(from.Z, to.Z) - radius;
            float maxZ = Math.Max(from.Z, to.Z) + radius;
            QueryTriangles(minX, minZ, maxX, maxZ);

            bool hadShallowOverlap = false;
            bool decreasedOverlap = false;
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle triangle = _triangles[index];
                float oldDepth = StandingTrianglePenetrationDepth(
                    triangle, from, capsule, floorNormalY);
                float newDepth = StandingTrianglePenetrationDepth(
                    triangle, to, capsule, floorNormalY);

                if (oldDepth > 0f)
                {
                    hadShallowOverlap = true;
                    // Deep or ambiguous penetrations require normal authoritative
                    // recovery, not ordinary movement through the obstruction.
                    if (oldDepth > 0.12f || newDepth > oldDepth + 0.0001f)
                        return false;
                    if (newDepth < oldDepth - 0.0001f)
                        decreasedOverlap = true;
                }
                else if (newDepth > 0.0001f)
                {
                    // Retreat must never start penetrating a different triangle.
                    return false;
                }
            }

            if (!hadShallowOverlap || !decreasedOverlap)
                return false;

            // Even an improving capsule overlap cannot cross a wall's actual
            // center line. The same existing world query also tests live doors.
            float testHeight = Math.Min(capsule.Height - radius, 0.9f);
            if (IsLineObstructed(
                    new WorldPosition(from.X, from.Y + testHeight, from.Z),
                    new WorldPosition(to.X, to.Y + testHeight, to.Z)))
                return false;

            QueryDynamicBlockers(minX, minZ, maxX, maxZ);
            foreach (long stableId in _dynamicBlockerQueryScratch)
            {
                if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                    state.Enabled &&
                    CapsuleOverlapsOrientedBox(to, capsule, state.Definition))
                    return false;
            }
            return true;
        }


        /// <summary>
        /// A vertical face below the walking plane is not a wall when the baked
        /// walkable geometry continues at the same height on BOTH sides of it.
        /// Adjacent BoxCollider floor tiles export those buried side faces; near
        /// their shared edge a standing capsule can otherwise appear to overlap
        /// the side despite having valid, continuous ground under its feet.
        ///
        /// This is evaluated only after the normal grounded-capsule test fails.
        /// A true wall, single-sided ledge, gap, step, overhead face, or enabled
        /// dynamic blocker cannot qualify. Nothing is cached or networked.
        /// </summary>
        private bool CanTraverseLevelTileSeam(
            WorldPosition from, WorldPosition to,
            ServerCapsule capsule, float maximumSlopeDegrees)
        {
            float radius = capsule.Radius;
            float floorNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));

            QueryTriangles(to.X - radius, to.Z - radius,
                to.X + radius, to.Z + radius);
            bool encounteredSeam = false;
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle triangle = _triangles[index];
                if (StandingTrianglePenetrationDepth(
                        triangle, to, capsule, floorNormalY) <= 0f)
                    continue;

                // Every blocking contact at the destination must be an actual
                // supported floor seam. One ordinary wall still blocks the move.
                if (!IsInternalSupportedTileSide(triangle, to, radius, floorNormalY))
                    return false;
                encounteredSeam = true;
            }
            if (!encounteredSeam)
                return false;

            // Preserve static center-line obstruction and LIVE door checks.
            float bodyHeight = Math.Min(capsule.Height - radius, 0.9f);
            if (IsLineObstructed(
                    new WorldPosition(from.X, from.Y + bodyHeight, from.Z),
                    new WorldPosition(to.X, to.Y + bodyHeight, to.Z)))
                return false;

            QueryDynamicBlockers(to.X - radius, to.Z - radius,
                to.X + radius, to.Z + radius);
            foreach (long stableId in _dynamicBlockerQueryScratch)
            {
                if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                    state.Enabled && CapsuleOverlapsOrientedBox(to, capsule, state.Definition))
                    return false;
            }
            return true;
        }

        private bool IsInternalSupportedTileSide(
            ServerCollisionTriangle t, WorldPosition feet,
            float radius, float floorNormalY)
        {
            // This is an actual upright *side* of a floor tile, with its top
            // at foot level. It must not classify a rising wall or stair riser.
            if (MathF.Abs(t.normalY) > 0.10f)
                return false;
            float highest = Math.Max(t.ay, Math.Max(t.by, t.cy));
            float lowest = Math.Min(t.ay, Math.Min(t.by, t.cy));
            if (highest > feet.Y + 0.025f ||
                highest < feet.Y - 0.035f ||
                highest - lowest < 0.005f)
                return false;

            float normalLength = MathF.Sqrt(
                t.normalX * t.normalX + t.normalZ * t.normalZ);
            if (normalLength < 0.95f)
                return false;

            // Project onto the longest horizontal triangle edge, rather than
            // using an arbitrary vertex on a potentially large tiled wall face.
            float x0 = t.ax, z0 = t.az, x1 = t.bx, z1 = t.bz;
            float longest = (x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0);
            float bc = (t.cx - t.bx) * (t.cx - t.bx) +
                       (t.cz - t.bz) * (t.cz - t.bz);
            if (bc > longest)
            {
                x0 = t.bx; z0 = t.bz;
                x1 = t.cx; z1 = t.cz;
                longest = bc;
            }
            float ca = (t.ax - t.cx) * (t.ax - t.cx) +
                       (t.az - t.cz) * (t.az - t.cz);
            if (ca > longest)
            {
                x0 = t.cx; z0 = t.cz;
                x1 = t.ax; z1 = t.az;
                longest = ca;
            }
            if (longest < 0.000001f)
                return false;

            float fraction = Math.Clamp(
                ((feet.X - x0) * (x1 - x0) +
                 (feet.Z - z0) * (z1 - z0)) / longest, 0f, 1f);
            float faceX = x0 + (x1 - x0) * fraction;
            float faceZ = z0 + (z1 - z0) * fraction;
            float offset = Math.Min(0.12f, Math.Max(0.06f, radius * 0.25f));
            float nx = t.normalX / normalLength;
            float nz = t.normalZ / normalLength;

            // Authoritative walkable support must exist across BOTH sides of
            // the face at the same elevation. This is the defining difference
            // between a buried interior tile seam and a real external ledge.
            const float levelTolerance = 0.035f;
            if (!TryFindGroundAtPoint(
                    faceX + nx * offset, faceZ + nz * offset, feet.Y,
                    levelTolerance, levelTolerance, floorNormalY, true,
                    out float firstY, out _))
                return false;
            if (!TryFindGroundAtPoint(
                    faceX - nx * offset, faceZ - nz * offset, feet.Y,
                    levelTolerance, levelTolerance, floorNormalY, true,
                    out float secondY, out _))
                return false;
            return Math.Abs(firstY - feet.Y) <= 0.025f &&
                   Math.Abs(secondY - feet.Y) <= 0.025f &&
                   Math.Abs(firstY - secondY) <= 0.025f;
        }

        // Uses the same exact standing-capsule triangle distance and walkable-
        // support exclusion as IsStandingCapsuleClear. Only the overlap depth
        // is different: normal standing continues to use the existing bool API.
        private static float StandingTrianglePenetrationDepth(
            ServerCollisionTriangle triangle, WorldPosition feet,
            ServerCapsule capsule, float floorNormalY)
        {
            float radius = capsule.Radius;
            if (triangle.normalY >= floorNormalY &&
                (Math.Max(triangle.ay, Math.Max(triangle.by, triangle.cy)) <=
                     feet.Y + radius * 1.5f ||
                 (TryClosestTriangleHeightAtXZ(triangle, feet.X, feet.Z,
                      out float localFloorY) &&
                  localFloorY <= feet.Y + radius * 1.5f)))
                return 0f;

            float d2 = SegmentTriangleDistanceSquared(
                feet.X, feet.Y + radius, feet.Z,
                feet.X, feet.Y + capsule.Height - radius, feet.Z,
                triangle);
            float r2 = radius * radius - 0.000001f;
            if (!IsFinite(d2))
                return radius;
            if (d2 >= r2)
                return 0f;
            return radius - MathF.Sqrt(Math.Max(0f, d2));
        }

    }
}
