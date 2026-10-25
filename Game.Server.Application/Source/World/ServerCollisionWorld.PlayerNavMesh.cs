using System;
using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    // Diagnostic reason for a grounded Player step that would previously have
    // entered the endpoint spatial-grid fallback. No protocol or shared actor state.
    public enum PlayerNavFallbackReason : byte
    {
        None = 0,
        StartNotBound = 1,
        NoReachableDirectNeighbor = 2,
        NoReachableBakedTransition = 3,
        DestinationNotOnBakedSurface = 4,
        DestinationHeightRejected = 5,
        IntermediateSupportRejected = 6,
        ExceedsShortStepBudget = 7,
    }

    /// <summary>
    /// Low-cost server-authoritative Player locomotion over the already-eroded,
    /// immutable DotRecast walk surface. The map catalog prepares height equations
    /// and short-step local surface candidates ONCE at map startup. Ordinary movement
    /// is direct kinematic integration plus one cached-triangle containment test.
    ///
    /// No runtime Detour query, pathfinding, capsule/mesh sweep, ground raycast,
    /// dynamic-blocker scan, additional scheduler, or network protocol is involved.
    /// Ordinary movement uses an authoritative baked destination, not per-tick
    /// 10cm mesh-continuity probing. Unusually large steps retain probing.
    /// </summary>
    public sealed partial class ServerCollisionWorld
    {
        // Tight enough to preserve real NavMesh boundaries; permissive enough for
        // separate authored mesh seams, differently subdivided tile borders, and
        // tiny floating-point differences. This is a WORLD-SPACE distance, not
        // an unbounded tolerance per Player tick.
        // Authoring meshes may not share exact triangle edge coordinates. The
        // certified DotRecast surface is the authority, not reconstructed edge IDs.
        // These values are small, world-space seam allowances, never accumulated
        // across ticks and never permission to move without a certified endpoint.
        private const float ShortStepMaximum = 0.75f;
        // A usual 20 Hz sprint step is ~0.30 m. If both ends are already
        // supported by certified MapData, do not require intermediate samples
        // every 10 cm. Baked agent erosion already protects static obstacles.
        // Longer steps retain segment validation to avoid tunneling.
        private const float OrdinaryStepMaximum = 0.35f;
        private const float LocalCandidateReach = 0.85f;
        private const float SegmentProbeSpacing = 0.10f;
        private const float SegmentSeamAllowance = 0.075f;
        // Allow small discontinuities from independently authored mesh pieces.
        // This is an absolute NavMesh-edge margin, NOT a per-tick accumulated
        // displacement. The current cached start must remain within it.
        private const float EndpointBoundaryTolerance = 0.10f;

        // Built once from the existing 4m spatial index when ServerMapCatalog
        // starts. Grounded Player transitions never query a spatial grid or
        // reconstruct graph connectivity in the simulation loop.
        private PlayerNavFace[] _playerNavFaces;
        private int[] _playerNavLocalOffsets;
        private int[] _playerNavLocalIndices;

        private readonly struct PlayerNavFace
        {
            private readonly float _ax, _az, _ux, _uz, _vx, _vz;
            private readonly float _invDen, _baseY, _byDelta, _cyDelta;
            private readonly float _uSlack, _vSlack, _wSlack;

            public PlayerNavFace(ServerCollisionTriangle t)
            {
                _ax = t.ax; _az = t.az;
                _ux = t.bx - t.ax; _uz = t.bz - t.az;
                _vx = t.cx - t.ax; _vz = t.cz - t.az;
                float den = _ux * _vz - _vx * _uz;
                _invDen = Math.Abs(den) > 0.0000001f ? 1f / den : 0f;
                _baseY = t.ay;
                _byDelta = t.by - t.ay;
                _cyDelta = t.cy - t.ay;
                float absInv = Math.Abs(_invDen);
                _uSlack = MathF.Sqrt(_vx * _vx + _vz * _vz) * absInv;
                _vSlack = MathF.Sqrt(_ux * _ux + _uz * _uz) * absInv;
                float wx = _ux - _vx, wz = _uz - _vz;
                _wSlack = MathF.Sqrt(wx * wx + wz * wz) * absInv;
            }

            private void Barycentric(float x, float z, out float u, out float v)
            {
                float dx = x - _ax, dz = z - _az;
                u = (dx * _vz - _vx * dz) * _invDen;
                v = (_ux * dz - dx * _uz) * _invDen;
            }

            public bool TryHeight(float x, float z, float slackMeters, out float y)
            {
                if (_invDen == 0f)
                {
                    y = 0f;
                    return false;
                }
                Barycentric(x, z, out float u, out float v);
                const float epsilon = 0.0005f;
                if (u < -epsilon - slackMeters * _uSlack ||
                    v < -epsilon - slackMeters * _vSlack ||
                    u + v > 1f + epsilon + slackMeters * _wSlack)
                {
                    y = 0f;
                    return false;
                }
                y = _baseY + u * _byDelta + v * _cyDelta;
                return float.IsFinite(y);
            }

            // Clip the entire requested horizontal STEP to this face. This makes
            // multi-face traversal follow the actual short Player motion segment,
            // rather than permitting a shortcut across an obstacle merely because
            // two end faces happen to belong to the same connected graph.
            public bool ClipStep(float x0, float z0, float x1, float z1,
                float slackMeters, out float enter, out float exit)
            {
                enter = 0f; exit = 1f;
                if (_invDen == 0f)
                    return false;
                Barycentric(x0, z0, out float u0, out float v0);
                Barycentric(x1, z1, out float u1, out float v1);
                const float epsilon = 0.0005f;
                return ClipPositive(u0 + epsilon + slackMeters * _uSlack,
                           u1 + epsilon + slackMeters * _uSlack, ref enter, ref exit) &&
                       ClipPositive(v0 + epsilon + slackMeters * _vSlack,
                           v1 + epsilon + slackMeters * _vSlack, ref enter, ref exit) &&
                       ClipPositive(1f + epsilon + slackMeters * _wSlack - u0 - v0,
                           1f + epsilon + slackMeters * _wSlack - u1 - v1,
                           ref enter, ref exit);
            }

            private static bool ClipPositive(float a, float b,
                ref float enter, ref float exit)
            {
                float delta = b - a;
                if (Math.Abs(delta) <= 0.0000001f)
                    return a >= 0f;
                float crossing = -a / delta;
                if (delta > 0f)
                    enter = Math.Max(enter, crossing);
                else
                    exit = Math.Min(exit, crossing);
                return enter <= exit + 0.00001f;
            }
        }

        /// <summary>
        /// Startup only: precompute the small set of possible destination
        /// surfaces for a short grounded step from each certified triangle.
        /// Unlike V4 this is NOT a reconstructed triangle-edge graph. Synty
        /// parts and NavMesh tile borders need not have matching edge vertices.
        /// The runtime still verifies the destination and intermediate support.
        /// </summary>
        public void PreparePlayerNavigationFastPath()
        {
            if (_playerNavFaces != null || _safeMovementTriangleGrid.Count == 0)
                return;

            int count = _safeMovementTriangles.Length;
            var faces = new PlayerNavFace[count];
            var candidateLists = new List<int>[count];
            var seen = new int[count];
            for (int i = 0; i < count; ++i)
                faces[i] = new PlayerNavFace(_safeMovementTriangles[i]);

            for (int i = 0; i < count; ++i)
            {
                ServerCollisionTriangle source = _safeMovementTriangles[i];
                float sourceMinX = Math.Min(source.ax, Math.Min(source.bx, source.cx));
                float sourceMaxX = Math.Max(source.ax, Math.Max(source.bx, source.cx));
                float sourceMinZ = Math.Min(source.az, Math.Min(source.bz, source.cz));
                float sourceMaxZ = Math.Max(source.az, Math.Max(source.bz, source.cz));
                float sourceMinY = Math.Min(source.ay, Math.Min(source.by, source.cy));
                float sourceMaxY = Math.Max(source.ay, Math.Max(source.by, source.cy));
                float minX = sourceMinX - LocalCandidateReach;
                float maxX = sourceMaxX + LocalCandidateReach;
                float minZ = sourceMinZ - LocalCandidateReach;
                float maxZ = sourceMaxZ + LocalCandidateReach;
                int minCellX = ToCell(minX, SafeMovementCellSize);
                int maxCellX = ToCell(maxX, SafeMovementCellSize);
                int minCellZ = ToCell(minZ, SafeMovementCellSize);
                int maxCellZ = ToCell(maxZ, SafeMovementCellSize);

                var local = new List<int>(12);
                local.Add(i); // A valid start face is always part of its local neighborhood.
                seen[i] = i + 1;
                for (int cz = minCellZ; cz <= maxCellZ; ++cz)
                for (int cx = minCellX; cx <= maxCellX; ++cx)
                {
                    if (!_safeMovementTriangleGrid.TryGetValue(new CellKey(cx, cz),
                            out List<int> cell))
                        continue;
                    for (int j = 0; j < cell.Count; ++j)
                    {
                        int otherIndex = cell[j];
                        if (seen[otherIndex] == i + 1)
                            continue;
                        seen[otherIndex] = i + 1;
                        ServerCollisionTriangle other = _safeMovementTriangles[otherIndex];
                        if (Math.Max(other.ax, Math.Max(other.bx, other.cx)) < minX ||
                            Math.Min(other.ax, Math.Min(other.bx, other.cx)) > maxX ||
                            Math.Max(other.az, Math.Max(other.bz, other.cz)) < minZ ||
                            Math.Min(other.az, Math.Min(other.bz, other.cz)) > maxZ ||
                            Math.Max(other.ay, Math.Max(other.by, other.cy)) < sourceMinY - 0.65f ||
                            Math.Min(other.ay, Math.Min(other.by, other.cy)) > sourceMaxY + 0.65f)
                            continue;
                        local.Add(otherIndex);
                    }
                }
                candidateLists[i] = local;
            }

            var offsets = new int[count + 1];
            for (int i = 0; i < count; ++i)
                offsets[i + 1] = checked(offsets[i] + candidateLists[i].Count);
            var flattened = new int[offsets[count]];
            for (int i = 0; i < count; ++i)
                candidateLists[i].CopyTo(flattened, offsets[i]);

            _playerNavLocalIndices = flattened;
            _playerNavLocalOffsets = offsets;
            _playerNavFaces = faces;
        }

        private bool TryPlayerNavHeight(int index, float x, float z, out float y)
        {
            // Directly constructed worlds in existing tests still work without
            // startup preparation; the production catalog always prepares faces.
            if (_playerNavFaces != null)
                return _playerNavFaces[index].TryHeight(x, z, EndpointBoundaryTolerance, out y);
            return TryTriangleHeightAtXZ(_safeMovementTriangles[index], x, z, out y);
        }

        /// <summary>
        /// Bind a newly spawned, teleported, or directly reconstructed Player to
        /// its baked supporting triangle exactly once per location change.
        /// This is NOT an in-movement fallback. It never accepts displacement.
        /// </summary>
        public bool TryBindPlayerNavStart(
            WorldPosition startFeet, ServerCapsule capsule,
            float maximumSlopeDegrees, out int triangleIndex)
        {
            triangleIndex = -1;
            if (_safeMovementTriangleGrid.Count == 0 ||
                capsule.Radius > _map.safeMovementRadius + 0.0001f ||
                capsule.Height > _map.safeMovementHeight + 0.0001f ||
                !float.IsFinite(startFeet.X) || !float.IsFinite(startFeet.Y) ||
                !float.IsFinite(startFeet.Z) ||
                !TryGetPlayerSafeMovementCell(startFeet.X, startFeet.Z,
                    out List<int> candidates))
                return false;

            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float bestError = 0.40f;
            for (int i = 0; i < candidates.Count; ++i)
            {
                int index = candidates[i];
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < minNormalY ||
                    !TryPlayerNavHeight(index, startFeet.X, startFeet.Z, out float y))
                    continue;

                float error = Math.Abs(y - startFeet.Y);
                if (error <= bestError)
                {
                    triangleIndex = index;
                    bestError = error;
                }
            }
            return triangleIndex >= 0;
        }

        /// <summary>
        /// Diagnostic-only constant-time candidate count for a throttled warning.
        /// The grounded-movement solver never scans this cell as a fallback.
        /// </summary>
        public int CountPlayerNavCellCandidates(float x, float z) =>
            float.IsFinite(x) && float.IsFinite(z) &&
            TryGetPlayerSafeMovementCell(x, z, out List<int> candidates)
                ? candidates.Count : 0;

        // Backward-compatible signature for other callers and tests.
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
            out bool blocked) =>
            TryResolvePlayerCertifiedGrounded(
                startFeet, deltaX, deltaZ, capsule, stepHeight,
                groundSnapDistance, maximumSlopeDegrees,
                ref cachedTriangleIndex, out resolved, out blocked, out _);

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
            out bool blocked,
            out PlayerNavFallbackReason fallbackReason)
        {
            fallbackReason = PlayerNavFallbackReason.None;
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

            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float maximumRise = stepHeight + 0.05f;
            float maximumDrop = groundSnapDistance + 0.05f;
            bool hasCachedStart = false;
            float cachedStartY = 0f;
            if ((uint)cachedTriangleIndex < (uint)_safeMovementTriangles.Length)
            {
                ServerCollisionTriangle cached = _safeMovementTriangles[cachedTriangleIndex];
                if (cached.normalY >= minNormalY &&
                    TryPlayerNavHeight(cachedTriangleIndex, startFeet.X,
                        startFeet.Z, out cachedStartY) &&
                    Math.Abs(cachedStartY - startFeet.Y) <= 0.40f)
                {
                    hasCachedStart = true;

                    // The overwhelmingly common case: one cached triangle and
                    // two precomputed barycentric equations. No grid, heap or
                    // physics lookup on the simulation thread.
                    if (_playerNavFaces != null &&
                        _playerNavFaces[cachedTriangleIndex].TryHeight(
                            targetX, targetZ, 0f, out float navY) &&
                        AcceptHeight(startFeet.Y, cachedStartY, navY,
                            maximumRise, maximumDrop, out float feetY))
                    {
                        resolved = new WorldPosition(targetX, feetY, targetZ);
                        blocked = false;
                        return true;
                    }

                    // Avoid reconstructed edge-topology restrictions: inspect
                    // only surfaces prepared near this source triangle at
                    // map startup and certify the short movement segment.
                    if (TryResolvePlayerLocalStep(cachedTriangleIndex, startFeet,
                            targetX, targetZ, cachedStartY, minNormalY, maximumRise,
                            maximumDrop, out int destinationFace,
                            out float destinationFeetY,
                            out PlayerNavFallbackReason localReason))
                    {
                        cachedTriangleIndex = destinationFace;
                        resolved = new WorldPosition(targetX, destinationFeetY, targetZ);
                        blocked = false;
                        return true;
                    }
                    // Keep the actual rejected stage. The previous generic
                    // NoReachableBakedTransition label hid whether the problem
                    // was no destination surface, a vertical step, or an actual
                    // unsupported segment. No extra query/log is added here.
                    fallbackReason = localReason;
                }
            }

            // There is no runtime endpoint grid fallback. The source triangle
            // remains cached on a rejected step so repeated wall presses stay
            // cheap and do not trigger another start lookup.
            if (!hasCachedStart)
                fallbackReason = PlayerNavFallbackReason.StartNotBound;
            if (!hasCachedStart)
                cachedTriangleIndex = -1;
            return true;
        }

        /// <summary>
        /// The common path already completed the one-face test. Transitions
        /// use only a small precomputed LOCAL surface set and sample the short
        /// movement segment against certified walkable geometry. This tolerates
        /// tiny non-matching mesh seams without trusting an arbitrary endpoint
        /// or depending on exact triangle connectivity. No runtime spatial
        /// grid, heap allocation, Detour query, or polygon path search.
        /// </summary>
        private bool TryResolvePlayerLocalStep(int startIndex,
            WorldPosition startFeet, float targetX, float targetZ,
            float startNavY, float minimumNormalY, float maximumRise, float maximumDrop,
            out int destinationFace, out float destinationY,
            out PlayerNavFallbackReason rejectedReason)
        {
            destinationFace = -1;
            destinationY = startFeet.Y;
            rejectedReason = PlayerNavFallbackReason.NoReachableBakedTransition;
            if (_playerNavFaces == null || _playerNavLocalIndices == null ||
                _playerNavLocalOffsets == null)
                return false;

            float dx = targetX - startFeet.X;
            float dz = targetZ - startFeet.Z;
            float squared = dx * dx + dz * dz;
            if (squared <= 0.0000000001f ||
                squared > ShortStepMaximum * ShortStepMaximum)
            {
                rejectedReason = PlayerNavFallbackReason.ExceedsShortStepBudget;
                return false;
            }
            float distance = MathF.Sqrt(squared);
            bool ordinaryStep = squared <= OrdinaryStepMaximum * OrdinaryStepMaximum;
            int first = _playerNavLocalOffsets[startIndex];
            int end = _playerNavLocalOffsets[startIndex + 1];

            // Prefer the closest certified height when several overlapping
            // source meshes share the same XZ position. This avoids jumping
            // between stacked surfaces simply because an index sorts first.
            float bestError = float.MaxValue;
            bool foundEndpoint = false;
            bool foundHeightCompatibleEndpoint = false;
            for (int k = first; k < end; ++k)
            {
                int faceIndex = _playerNavLocalIndices[k];
                if (_safeMovementTriangles[faceIndex].normalY < minimumNormalY ||
                    !_playerNavFaces[faceIndex].TryHeight(targetX, targetZ,
                        EndpointBoundaryTolerance, out float targetNavY))
                    continue;

                foundEndpoint = true;
                if (!AcceptHeight(startFeet.Y, startNavY, targetNavY,
                        maximumRise, maximumDrop, out float targetFeetY))
                    continue;
                foundHeightCompatibleEndpoint = true;

                float error = Math.Abs(targetNavY - startNavY);
                if (error >= bestError)
                    continue;

                // Do not re-sample tiny triangle seams on normal movement.
                // Both endpoints are already verified against baked agent-sized
                // surfaces. A short step cannot cross a properly eroded wall
                // gap (2 * agent radius) in one tick. Long steps are still
                // segment-checked rather than blindly endpoint-accepted.
                if (!ordinaryStep &&
                    !PlayerLocalSegmentSupported(first, end,
                        startFeet.X, startFeet.Z, targetX, targetZ,
                        startNavY, targetNavY, minimumNormalY, distance,
                        maximumRise, maximumDrop))
                    continue;

                bestError = error;
                destinationFace = faceIndex;
                destinationY = targetFeetY;
            }
            if (destinationFace >= 0)
                return true;
            rejectedReason = !foundEndpoint
                ? PlayerNavFallbackReason.DestinationNotOnBakedSurface
                : !foundHeightCompatibleEndpoint
                    ? PlayerNavFallbackReason.DestinationHeightRejected
                    : PlayerNavFallbackReason.IntermediateSupportRejected;
            return false;
        }

        private bool PlayerLocalSegmentSupported(int first, int end,
            float startX, float startZ, float targetX, float targetZ,
            float startNavY, float targetNavY, float minimumNormalY,
            float distance, float maximumRise, float maximumDrop)
        {
            // A two- or three-sample check catches small holes and disconnected
            // areas that endpoint-only acceptance would otherwise tunnel over.
            // Sampling is constant-bounded by the already-limited step length.
            int subdivisions = Math.Max(2,
                (int)Math.Ceiling(distance / SegmentProbeSpacing));
            float dx = targetX - startX;
            float dz = targetZ - startZ;
            float priorY = startNavY;
            for (int sample = 1; sample < subdivisions; ++sample)
            {
                float fraction = (float)sample / subdivisions;
                float x = startX + dx * fraction;
                float z = startZ + dz * fraction;
                float expected = startNavY + (targetNavY - startNavY) * fraction;
                float bestError = float.MaxValue;
                float bestY = 0f;
                bool supported = false;
                for (int k = first; k < end; ++k)
                {
                    int index = _playerNavLocalIndices[k];
                    if (_safeMovementTriangles[index].normalY < minimumNormalY ||
                        !_playerNavFaces[index].TryHeight(x, z,
                            SegmentSeamAllowance, out float y))
                        continue;
                    float error = Math.Abs(y - expected);
                    if (error >= bestError || error > 0.30f ||
                        y > priorY + maximumRise ||
                        y < priorY - maximumDrop)
                        continue;
                    supported = true;
                    bestError = error;
                    bestY = y;
                }
                if (!supported)
                    return false;
                priorY = bestY;
            }
            return targetNavY <= priorY + maximumRise &&
                   targetNavY >= priorY - maximumDrop;
        }

        private static bool AcceptHeight(float feetY, float oldNavY, float newNavY,
            float maximumRise, float maximumDrop, out float targetFeetY)
        {
            targetFeetY = feetY + newNavY - oldNavY;
            float rise = targetFeetY - feetY;
            return rise <= maximumRise && rise >= -maximumDrop;
        }

        /// <summary>
        /// Only when a Player descends through a possible landing: one baked
        /// destination cell, no physics sweep or allocated scratch collection.
        /// </summary>
        public bool TryFindPlayerBakedLandingSupport(
            float previousFeetY,
            WorldPosition nextFeet,
            ServerCapsule capsule,
            float maximumSlopeDegrees,
            out float groundY) =>
            TryFindPlayerBakedLandingSupport(previousFeetY, nextFeet,
                capsule, maximumSlopeDegrees, out groundY, out _);

        public bool TryFindPlayerBakedLandingSupport(
            float previousFeetY,
            WorldPosition nextFeet,
            ServerCapsule capsule,
            float maximumSlopeDegrees,
            out float groundY,
            out int supportTriangleIndex)
        {
            groundY = 0f;
            supportTriangleIndex = -1;
            if (_safeMovementTriangleGrid.Count == 0 ||
                capsule.Radius > _map.safeMovementRadius + 0.0001f ||
                capsule.Height > _map.safeMovementHeight + 0.0001f ||
                !float.IsFinite(previousFeetY) ||
                !float.IsFinite(nextFeet.X) || !float.IsFinite(nextFeet.Y) ||
                !float.IsFinite(nextFeet.Z) || nextFeet.Y > previousFeetY)
                return false;

            float upper = previousFeetY + 0.03f;
            float lower = nextFeet.Y - 0.15f;
            float minNormalY = MathF.Cos(
                Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            if (!TryGetPlayerSafeMovementCell(nextFeet.X, nextFeet.Z,
                    out List<int> candidates))
                return false;

            bool found = false;
            float highest = float.NegativeInfinity;
            for (int i = 0; i < candidates.Count; ++i)
            {
                int index = candidates[i];
                ServerCollisionTriangle triangle = _safeMovementTriangles[index];
                if (triangle.normalY < minNormalY ||
                    !TryPlayerNavHeight(index, nextFeet.X, nextFeet.Z, out float y) ||
                    y > upper || y < lower || y <= highest)
                    continue;
                highest = y;
                supportTriangleIndex = index;
                found = true;
            }

            if (found)
                groundY = highest;
            return found;
        }

        private bool TryGetPlayerSafeMovementCell(float x, float z,
            out List<int> candidates)
        {
            return _safeMovementTriangleGrid.TryGetValue(new CellKey(
                ToCell(x, SafeMovementCellSize),
                ToCell(z, SafeMovementCellSize)), out candidates);
        }
    }
}
