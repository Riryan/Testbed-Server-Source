using System;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    // Existing collision-world partial class. No new grid, player state, messages, or tick system.
    public sealed partial class ServerCollisionWorld
    {
        // A populated DotRecast-derived movement certificate exists on this map.
        // Movement actors on certified maps never need the old expensive static
        // capsule solver just because a proposed grounded movement is rejected.
        public bool HasCertifiedGroundedMovement => _safeMovementTriangleGrid.Count != 0;

        /// <summary>
        /// Fast, authoritative, all-or-stop grounded movement using the already loaded
        /// DotRecast-derived certified triangles. The caller uses the legacy full resolver
        /// only if the starting position is not covered by a compatible certificate.
        /// A certified boundary rejection is conclusive; never perform X/Z slides or
        /// expensive full capsule fallback for that rejection.
        /// </summary>
        public bool TryResolveCertifiedGroundedNoSlide(
            WorldPosition startFeet,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            out WorldPosition resolved,
            out bool blocked)
        {
            resolved = startFeet;
            blocked = false;

            if (_safeMovementTriangleGrid.Count == 0 ||
                capsule.Radius > _map.safeMovementRadius + 0.0001f ||
                capsule.Height > _map.safeMovementHeight + 0.0001f)
                return false;

            if (!float.IsFinite(deltaX) || !float.IsFinite(deltaZ) ||
                !float.IsFinite(startFeet.X) || !float.IsFinite(startFeet.Y) ||
                !float.IsFinite(startFeet.Z))
            {
                blocked = true;
                return true;
            }

            // Zero motion is not a collision test, and does not need a ground rescan.
            if (Math.Abs(deltaX) + Math.Abs(deltaZ) <= 0.000001f)
                return true;

            // Most wall presses end OUTSIDE the eroded DotRecast surface. Reject
            // those before the more expensive segment coverage and ground lookup.
            // The endpoint is necessary but never sufficient for acceptance: a
            // valid endpoint still requires the full certified segment test below.
            if (!IsCertifiedEndpointAtReachableHeight(
                    startFeet, deltaX, deltaZ, stepHeight,
                    groundSnapDistance, maximumSlopeDegrees))
            {
                // A starting pose covered by the certificate makes this a
                // conclusive baked-boundary rejection. Outside starts are left
                // to the caller's explicit policy (the player motor fails closed).
                if (IsCertifiedStandingPoint(startFeet, maximumSlopeDegrees))
                {
                    blocked = true;
                    return true;
                }
                return false;
            }

            // The existing efficient segment/triangle coverage check is the primary
            // acceptance test. This includes support-height validation.
            if (TryResolveCertifiedSafeMove(
                    startFeet, deltaX, deltaZ, capsule, stepHeight,
                    groundSnapDistance, maximumSlopeDegrees, out resolved))
                return true;

            // A failure inside a known walkable polygon is a blocked movement request.
            // For such a request there is no justification for repeatedly checking
            // authored static wall triangles or trying sideways substeps.
            // Query *only* the start point after failure, so successful movement pays
            // no additional lookup cost.
            QuerySafeMovementTriangles(
                startFeet.X, startFeet.Z, startFeet.X, startFeet.Z);
            float minimumNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < minimumNormalY ||
                    !TryTriangleHeightAtXZ(
                        triangle, startFeet.X, startFeet.Z, out float groundY) ||
                    Math.Abs(groundY - startFeet.Y) > 0.25f)
                    continue;

                resolved = startFeet;
                blocked = true;
                return true;
            }

            // Start outside the trusted baked surface. The grounded motor rejects
            // this on certified maps; legacy maps without a certificate retain their
            // old fallback, and airborne movement is handled separately.
            return false;
        }

        // Query only the tiny existing 4m safe-movement grid at the destination,
        // rather than enumerating the swept capsule or querying static wall meshes.
        private bool IsCertifiedEndpointAtReachableHeight(
            WorldPosition startFeet, float dx, float dz,
            float stepHeight, float groundSnapDistance, float maximumSlopeDegrees)
        {
            float x = startFeet.X + dx;
            float z = startFeet.Z + dz;
            QuerySafeMovementTriangles(x, z, x, z);
            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float minY = startFeet.Y - groundSnapDistance - 0.05f;
            float maxY = startFeet.Y + stepHeight + 0.05f;
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle tri = _safeMovementTriangles[index];
                if (tri.normalY >= minNormalY &&
                    TryTriangleHeightAtXZ(tri, x, z, out float y) &&
                    y >= minY && y <= maxY)
                    return true;
            }
            return false;
        }

        private bool IsCertifiedStandingPoint(
            WorldPosition feet, float maximumSlopeDegrees)
        {
            QuerySafeMovementTriangles(feet.X, feet.Z, feet.X, feet.Z);
            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            foreach (int index in _safeMovementQueryScratch)
            {
                ServerCollisionTriangle tri = _safeMovementTriangles[index];
                if (tri.normalY >= minNormalY &&
                    TryTriangleHeightAtXZ(tri, feet.X, feet.Z, out float y) &&
                    Math.Abs(y - feet.Y) <= 0.25f)
                    return true;
            }
            return false;
        }

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
            samples = Math.Max(1, (int)MathF.Ceiling(length /
                Math.Max(0.05f, capsule.Radius * 0.5f)));
            if (samples > 64) return false;
            float rad = capsule.Radius;
            QueryDynamicBlockers(
                Math.Min(from.X, to.X) - rad, Math.Min(from.Z, to.Z) - rad,
                Math.Max(from.X, to.X) + rad, Math.Max(from.Z, to.Z) + rad);
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
