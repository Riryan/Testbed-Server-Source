using System;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    // Additional implementation of the SAME ServerCollisionWorld, not a second
    // collision model. Uses its existing grid, exact triangle predicates and
    // dynamic-blocker definitions with one swept broadphase per movement tick.
    public sealed partial class ServerCollisionWorld
    {
        private bool TryFindSupportFromCurrentCandidates(
            WorldPosition feet,
            float footprintRadius,
            float probeUp,
            float probeDown,
            float maximumSlopeDegrees,
            bool requireWalkable,
            out ServerGroundHit hit)
        {
            hit = default;
            float radius = Math.Max(0f, footprintRadius);
            float ring = radius * 0.6f;
            float minimumNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));

            // The existing five-footprint algorithm always selects the center
            // surface when the center is supported, regardless of the side
            // samples. The other four checks are therefore unnecessary in the
            // overwhelmingly common case and can be omitted with no change
            // to the chosen ground position, normal or surface ID.
            if (TryFindGroundAtPointFromCurrentCandidates(
                    feet.X, feet.Z, feet.Y,
                    probeUp, probeDown, minimumNormalY, requireWalkable,
                    out float centerY, out ServerCollisionTriangle center))
            {
                hit = new ServerGroundHit(center.surfaceId,
                    new WorldPosition(feet.X, centerY, feet.Z),
                    center.normalX, center.normalY, center.normalZ);
                return true;
            }

            // Preserve the original minimum-two-perimeter-support rule when
            // the center is not supported (ledges and modular seams).
            int supportCount = 0;
            float bestY = float.NegativeInfinity;
            ServerCollisionTriangle best = default;
            for (int sample = 1; sample <= 4; ++sample)
            {
                float offsetX = sample == 1 ? ring : sample == 2 ? -ring : 0f;
                float offsetZ = sample == 3 ? ring : sample == 4 ? -ring : 0f;
                if (!TryFindGroundAtPointFromCurrentCandidates(
                        feet.X + offsetX, feet.Z + offsetZ, feet.Y,
                        probeUp, probeDown, minimumNormalY, requireWalkable,
                        out float y, out ServerCollisionTriangle triangle))
                    continue;
                ++supportCount;
                if (y > bestY)
                {
                    bestY = y;
                    best = triangle;
                }
            }
            if (supportCount < 2)
                return false;
            hit = new ServerGroundHit(best.surfaceId,
                new WorldPosition(feet.X, bestY, feet.Z),
                best.normalX, best.normalY, best.normalZ);
            return true;
        }

        // This is the canonical standing-capsule narrowphase used by both the
        // existing public static query and the shared ambient swept query.
        // The ambient caller already populated _queryScratch for the whole sweep.
        // Reuse the same exact conservative capsule-AABB rejection in the normal
        // character motor and the batched ambient traversal.
        private static bool IsTriangleOutsideStandingCapsuleBounds(
            ServerCollisionTriangle t, WorldPosition feet, ServerCapsule capsule)
        {
            float radius = capsule.Radius;
            return
                (t.ax > feet.X + radius && t.bx > feet.X + radius && t.cx > feet.X + radius) ||
                (t.ax < feet.X - radius && t.bx < feet.X - radius && t.cx < feet.X - radius) ||
                (t.az > feet.Z + radius && t.bz > feet.Z + radius && t.cz > feet.Z + radius) ||
                (t.az < feet.Z - radius && t.bz < feet.Z - radius && t.cz < feet.Z - radius) ||
                (t.ay > feet.Y + capsule.Height && t.by > feet.Y + capsule.Height && t.cy > feet.Y + capsule.Height) ||
                (t.ay < feet.Y && t.by < feet.Y && t.cy < feet.Y);
        }

        // Reusable scratch. Each collision world is already queried serially by the
        // authoritative simulation, so no per-actor allocation or independent index.
        private readonly System.Collections.Generic.List<int> _ambientPruneScratch =
            new System.Collections.Generic.List<int>(64);

        // The movement footprint predicate tolerates barycentric positions slightly
        // outside triangle edges (0.0005). Expand the swept XZ AABB by the SAME
        // epsilon scaled to triangle extent so pruning never removes a triangle
        // that the exact ground predicate could accept.
        private static bool TriangleOutsideAmbientGroundSweep(
            ServerCollisionTriangle t, float minX, float maxX, float minZ, float maxZ)
        {
            const float epsilon = 0.0005f;
            float expandX = epsilon *
                (Math.Abs(t.bx - t.ax) + Math.Abs(t.cx - t.ax)) + 0.00001f;
            float expandZ = epsilon *
                (Math.Abs(t.bz - t.az) + Math.Abs(t.cz - t.az)) + 0.00001f;
            return
                (t.ax > maxX + expandX && t.bx > maxX + expandX && t.cx > maxX + expandX) ||
                (t.ax < minX - expandX && t.bx < minX - expandX && t.cx < minX - expandX) ||
                (t.az > maxZ + expandZ && t.bz > maxZ + expandZ && t.cz > maxZ + expandZ) ||
                (t.az < minZ - expandZ && t.bz < minZ - expandZ && t.cz < minZ - expandZ);
        }

        // The existing grid covers 16m-wide cells; the actual per-tick route move
        // is a fraction of a metre. Keep only triangles intersecting the sweep's
        // tight physical envelope BEFORE iterating the grounded samples.
        private void PruneAmbientGroundCandidates(
            float minX, float minZ, float maxX, float maxZ)
        {
            _ambientPruneScratch.Clear();
            foreach (int index in _movementQueryScratch)
                if (TriangleOutsideAmbientGroundSweep(
                        _movementTriangles[index], minX, maxX, minZ, maxZ))
                    _ambientPruneScratch.Add(index);
            for (int i = 0; i < _ambientPruneScratch.Count; ++i)
                _movementQueryScratch.Remove(_ambientPruneScratch[i]);
        }

        private void PruneAmbientStaticCandidates(
            float minX, float minZ, float maxX, float maxZ,
            float minY, float maxY)
        {
            _ambientPruneScratch.Clear();
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle t = _triangles[index];
                if ((t.ax > maxX && t.bx > maxX && t.cx > maxX) ||
                    (t.ax < minX && t.bx < minX && t.cx < minX) ||
                    (t.az > maxZ && t.bz > maxZ && t.cz > maxZ) ||
                    (t.az < minZ && t.bz < minZ && t.cz < minZ) ||
                    (t.ay > maxY && t.by > maxY && t.cy > maxY) ||
                    (t.ay < minY && t.by < minY && t.cy < minY))
                    _ambientPruneScratch.Add(index);
            }
            for (int i = 0; i < _ambientPruneScratch.Count; ++i)
                _queryScratch.Remove(_ambientPruneScratch[i]);
        }

        private bool IsStandingCapsuleClearStaticFromCurrentCandidates(
            WorldPosition feet, ServerCapsule capsule, float maximumSlopeDegrees)
        {
            float radius = capsule.Radius;
            float bottomY = feet.Y + radius;
            float topY = feet.Y + capsule.Height - radius;
            float floorNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle t = _triangles[index];
                // Conservative local triangle AABB rejection. The grid covers
                // broad 16m cells; most of their triangles are not near a
                // 0.35m humanoid capsule. No intersecting triangle is skipped.
                if (IsTriangleOutsideStandingCapsuleBounds(t, feet, capsule))
                    continue;

                // Preserve the exact existing grounded floor-exemption policy.
                if (t.normalY >= floorNormalY &&
                    (Math.Max(t.ay, Math.Max(t.by, t.cy)) <= feet.Y + radius * 1.5f ||
                     (TryClosestTriangleHeightAtXZ(t, feet.X, feet.Z, out float localFloorY) &&
                      localFloorY <= feet.Y + radius * 1.5f)))
                    continue;
                float distanceSquared = SegmentTriangleDistanceSquared(
                    feet.X, bottomY, feet.Z,
                    feet.X, topY, feet.Z, t);
                if (distanceSquared < radius * radius - 0.000001f)
                    return false;
            }
            return true;
        }

        private bool IsDynamicCapsuleClearFromCurrentCandidates(
            WorldPosition feet, ServerCapsule capsule)
        {
            foreach (long stableId in _dynamicBlockerQueryScratch)
            {
                if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                    state.Enabled &&
                    CapsuleOverlapsOrientedBox(feet, capsule, state.Definition))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Authoritative, contiguous grounded NPC step. Broadphase for walkable
        /// ground, static solids and live blockers is collected ONCE for the
        /// complete short swept segment. Existing exact narrowphase remains
        /// per-sample, with distance <= half capsule radius as in ambient V1.
        /// Any failure leaves the actor unchanged for the existing full motor.
        /// </summary>
        public bool TryTraverseAmbientRouteSegment(
            WorldPosition start,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            int samples,
            out WorldPosition end,
            out long surfaceId,
            out int testedSamples)
        {
            end = start;
            surfaceId = 0;
            testedSamples = 0;
            if (samples < 1 || samples > 24 ||
                !float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(start.Z) ||
                !float.IsFinite(deltaX) || !float.IsFinite(deltaZ) ||
                !float.IsFinite(stepHeight) || !float.IsFinite(groundSnapDistance) ||
                !float.IsFinite(maximumSlopeDegrees))
                return false;

            float targetX = start.X + deltaX;
            float targetZ = start.Z + deltaZ;
            if (!float.IsFinite(targetX) || !float.IsFinite(targetZ))
                return false;

            float minX = Math.Min(start.X, targetX);
            float maxX = Math.Max(start.X, targetX);
            float minZ = Math.Min(start.Z, targetZ);
            float maxZ = Math.Max(start.Z, targetZ);
            float groundExtent = capsule.Radius * 0.6f + 0.01f;
            float bodyExtent = capsule.Radius;

            // Independent grids: none of these queries modifies either of the
            // other scratch sets. Every intermediate sample uses the exact same
            // candidate superset instead of rebuilding three grid queries.
            float groundMinX = minX - groundExtent;
            float groundMinZ = minZ - groundExtent;
            float groundMaxX = maxX + groundExtent;
            float groundMaxZ = maxZ + groundExtent;
            float bodyMinX = minX - bodyExtent;
            float bodyMinZ = minZ - bodyExtent;
            float bodyMaxX = maxX + bodyExtent;
            float bodyMaxZ = maxZ + bodyExtent;

            QueryMovementTriangles(groundMinX, groundMinZ, groundMaxX, groundMaxZ);
            PruneAmbientGroundCandidates(
                groundMinX, groundMinZ, groundMaxX, groundMaxZ);

            QueryTriangles(bodyMinX, bodyMinZ, bodyMaxX, bodyMaxZ);
            // Any accepted sequence is height-bounded by the existing per-sample
            // rise/drop limits. Include the capsule's full vertical extent and
            // ALL possible intermediate accepted heights, not only its endpoints.
            float maxUp = Math.Max(0f, stepHeight + 0.05f);
            float maxDown = Math.Max(0f, groundSnapDistance + 0.05f);
            PruneAmbientStaticCandidates(
                bodyMinX, bodyMinZ, bodyMaxX, bodyMaxZ,
                start.Y - samples * maxDown,
                start.Y + samples * maxUp + capsule.Height);

            // The door/obstacle broadphase remains LIVE on every movement tick.
            QueryDynamicBlockers(bodyMinX, bodyMinZ, bodyMaxX, bodyMaxZ);

            WorldPosition cursor = start;
            long lastSurface = 0;
            for (int i = 1; i <= samples; ++i)
            {
                testedSamples = i;
                float t = i / (float)samples;
                var probe = new WorldPosition(
                    start.X + deltaX * t, cursor.Y, start.Z + deltaZ * t);
                if (!TryFindSupportFromCurrentCandidates(
                        probe, capsule.Radius,
                        stepHeight + 0.05f, groundSnapDistance + 0.05f,
                        maximumSlopeDegrees, requireWalkable: true,
                        out ServerGroundHit ground))
                    return false;
                float rise = ground.Position.Y - cursor.Y;
                if (!float.IsFinite(rise) ||
                    rise > stepHeight + 0.05f ||
                    rise < -groundSnapDistance - 0.05f)
                    return false;
                if (!IsStandingCapsuleClearStaticFromCurrentCandidates(
                        ground.Position, capsule, maximumSlopeDegrees) ||
                    !IsDynamicCapsuleClearFromCurrentCandidates(ground.Position, capsule))
                    return false;
                cursor = ground.Position;
                lastSurface = ground.SurfaceId;
            }
            end = cursor;
            surfaceId = lastSurface;
            return true;
        }
    }
}
