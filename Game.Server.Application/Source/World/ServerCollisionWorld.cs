using System;
using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    public readonly struct ServerGroundHit
    {
        public long SurfaceId { get; }
        public WorldPosition Position { get; }
        public float NormalX { get; }
        public float NormalY { get; }
        public float NormalZ { get; }

        public ServerGroundHit(long surfaceId, WorldPosition position, float normalX, float normalY, float normalZ)
        {
            SurfaceId = surfaceId;
            Position = position;
            NormalX = normalX;
            NormalY = normalY;
            NormalZ = normalZ;
        }
    }

    public readonly struct ServerCapsule
    {
        public float Radius { get; }
        public float Height { get; }

        public ServerCapsule(float radius, float height)
        {
            Radius = Math.Max(0.05f, radius);
            Height = Math.Max(Radius * 2f, height);
        }
    }

    /// <summary>
    /// Lightweight static collision/query world over baked modular-map triangles. It is a
    /// query structure, not a rigidbody simulation. Active characters remain kinematic.
    /// </summary>
    public sealed class ServerCollisionWorld
    {
        private readonly struct CellKey : IEquatable<CellKey>
        {
            public readonly int X;
            public readonly int Z;
            public CellKey(int x, int z) { X = x; Z = z; }
            public bool Equals(CellKey other) => X == other.X && Z == other.Z;
            public override bool Equals(object obj) => obj is CellKey other && Equals(other);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        private sealed class DynamicBlockerState
        {
            public ServerDynamicBlocker Definition;
            public bool Enabled;
        }

        private readonly ServerMapSnapshot _map;
        private readonly ServerCollisionTriangle[] _triangles;
        private readonly float _cellSize;
        private readonly Dictionary<CellKey, List<int>> _triangleGrid = new Dictionary<CellKey, List<int>>();
        private readonly Dictionary<long, DynamicBlockerState> _dynamicBlockers = new Dictionary<long, DynamicBlockerState>();
        private readonly Dictionary<CellKey, List<long>> _dynamicBlockerGrid = new Dictionary<CellKey, List<long>>();
        private readonly HashSet<int> _queryScratch = new HashSet<int>();
        private readonly HashSet<long> _dynamicBlockerQueryScratch = new HashSet<long>();

        private bool _hasWorldBounds;
        private float _minX;
        private float _minY;
        private float _minZ;
        private float _maxX;
        private float _maxY;
        private float _maxZ;

        public string MapId => _map.mapId;
        public string InstanceId => _map.instanceId;
        public int TriangleCount => _triangles.Length;
        public bool HasWorldBounds => _hasWorldBounds;
        public float MinX => _minX;
        public float MinY => _minY;
        public float MinZ => _minZ;
        public float MaxX => _maxX;
        public float MaxY => _maxY;
        public float MaxZ => _maxZ;

        public ServerCollisionWorld(ServerMapSnapshot map, float cellSize = 16f)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _cellSize = IsFinite(cellSize) && cellSize >= 1f ? cellSize : 16f;
            _triangles = map.collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
            ComputeWorldBounds();
            BuildTriangleGrid();

            ServerDynamicBlocker[] blockers = map.dynamicBlockers ?? Array.Empty<ServerDynamicBlocker>();
            for (int i = 0; i < blockers.Length; ++i)
            {
                ServerDynamicBlocker blocker = blockers[i];
                if (blocker == null || blocker.stableId <= 0)
                    continue;
                _dynamicBlockers[blocker.stableId] = new DynamicBlockerState
                {
                    Definition = blocker,
                    Enabled = blocker.enabledByDefault,
                };
                IndexDynamicBlocker(blocker);
            }
        }

        /// <summary>
        /// True only when an actor is clearly outside the baked world envelope. This is a
        /// recovery guard, not ordinary movement validation.
        /// </summary>
        public bool IsOutsideRecoveryBounds(
            WorldPosition position,
            float horizontalMargin = 3f,
            float belowMargin = 6f,
            float aboveMargin = 30f)
        {
            if (!IsFinite(position.X) || !IsFinite(position.Y) || !IsFinite(position.Z))
                return true;
            if (!_hasWorldBounds)
                return false;

            float h = Math.Max(0f, horizontalMargin);
            float below = Math.Max(0f, belowMargin);
            float above = Math.Max(0f, aboveMargin);
            return position.X < _minX - h || position.X > _maxX + h ||
                   position.Z < _minZ - h || position.Z > _maxZ + h ||
                   position.Y < _minY - below || position.Y > _maxY + above;
        }

        /// <summary>
        /// Rare last-resort recovery search when a map has no valid authored player spawn.
        /// Normal spawning should use ServerSpawn anchors.
        /// </summary>
        public bool TryFindNearestSafeRecoveryPose(
            WorldPosition from,
            ServerCapsule capsule,
            float maximumSlopeDegrees,
            out ServerPose pose)
        {
            pose = default;
            float minNormalY = MathF.Cos(Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));
            float bestScore = float.PositiveInfinity;
            bool found = false;

            for (int i = 0; i < _triangles.Length; ++i)
            {
                ServerCollisionTriangle t = _triangles[i];
                if ((t.flags & ServerSurfaceFlags.Walkable) == 0 ||
                    (t.flags & ServerSurfaceFlags.NoSpawn) != 0 ||
                    t.normalY < minNormalY)
                    continue;

                float x = (t.ax + t.bx + t.cx) / 3f;
                float y = (t.ay + t.by + t.cy) / 3f;
                float z = (t.az + t.bz + t.cz) / 3f;
                if (!TryValidateSpawn(
                        new ServerPose(x, y, z, 0f),
                        capsule,
                        1.5f,
                        maximumSlopeDegrees,
                        out ServerPose candidate,
                        out _))
                    continue;

                float dx = candidate.x - from.X;
                float dz = candidate.z - from.Z;
                float score = dx * dx + dz * dz;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                pose = candidate;
                found = true;
            }

            return found;
        }

        public bool TrySetDynamicBlockerEnabled(long stableId, bool enabled)
        {
            if (!_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state))
                return false;
            state.Enabled = enabled;
            return true;
        }

        public bool IsDynamicBlockerEnabled(long stableId) =>
            _dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) && state.Enabled;

        /// <summary>
        /// Cheap authoritative visibility/occlusion query over baked static geometry and
        /// enabled dynamic blockers. Intended for spawn concealment/perception, not rendering.
        /// </summary>
        public bool IsLineObstructed(WorldPosition from, WorldPosition to)
        {
            float minX = Math.Min(from.X, to.X);
            float maxX = Math.Max(from.X, to.X);
            float minZ = Math.Min(from.Z, to.Z);
            float maxZ = Math.Max(from.Z, to.Z);
            QueryTriangles(minX, minZ, maxX, maxZ);
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle t = _triangles[index];
                if (SegmentIntersectsTriangle(from.X, from.Y, from.Z, to.X, to.Y, to.Z, t))
                    return true;
            }

            QueryDynamicBlockers(minX, minZ, maxX, maxZ);
            foreach (long stableId in _dynamicBlockerQueryScratch)
            {
                if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                    state.Enabled &&
                    SegmentIntersectsOrientedBox(from, to, state.Definition))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Finds a walkable support surface under the capsule's foot position. A small
        /// five-point footprint prevents a single center ray from failing at modular ledges.
        /// </summary>
        public bool TryFindGround(
            WorldPosition feet,
            float footprintRadius,
            float probeUp,
            float probeDown,
            float maximumSlopeDegrees,
            out ServerGroundHit hit)
        {
            return TryFindSupport(
                feet,
                footprintRadius,
                probeUp,
                probeDown,
                maximumSlopeDegrees,
                requireWalkable: true,
                out hit);
        }

        /// <summary>
        /// Physical-support fallback used only to stop a fall through solid geometry when
        /// that geometry was intentionally omitted from the walk surface. This does not
        /// promote the surface to normal walkable ground.
        /// </summary>
        public bool TryFindSolidGround(
            WorldPosition feet,
            float footprintRadius,
            float probeUp,
            float probeDown,
            float maximumSlopeDegrees,
            out ServerGroundHit hit)
        {
            return TryFindSupport(
                feet,
                footprintRadius,
                probeUp,
                probeDown,
                maximumSlopeDegrees,
                requireWalkable: false,
                out hit);
        }

        private bool TryFindSupport(
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
            float minNormalY = MathF.Cos(Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f));

            bool centerSupported = false;
            float centerY = float.NegativeInfinity;
            ServerCollisionTriangle center = default;

            int supportCount = 0;
            float bestY = float.NegativeInfinity;
            ServerCollisionTriangle best = default;

            // Gather the complete five-point footprint candidate set once. The previous
            // implementation rebuilt the same spatial-grid candidate set independently for
            // center/+X/-X/+Z/-Z, multiplying dictionary/hash work for every ground probe.
            float queryExtent = ring + 0.01f;
            QueryTriangles(
                feet.X - queryExtent,
                feet.Z - queryExtent,
                feet.X + queryExtent,
                feet.Z + queryExtent);

            for (int sample = 0; sample < 5; ++sample)
            {
                float offsetX = sample == 1 ? ring : sample == 2 ? -ring : 0f;
                float offsetZ = sample == 3 ? ring : sample == 4 ? -ring : 0f;
                float x = feet.X + offsetX;
                float z = feet.Z + offsetZ;

                if (!TryFindGroundAtPointFromCurrentCandidates(
                        x,
                        z,
                        feet.Y,
                        probeUp,
                        probeDown,
                        minNormalY,
                        requireWalkable,
                        out float y,
                        out ServerCollisionTriangle triangle))
                {
                    continue;
                }

                supportCount++;
                if (sample == 0)
                {
                    centerSupported = true;
                    centerY = y;
                    center = triangle;
                }

                if (y > bestY)
                {
                    bestY = y;
                    best = triangle;
                }
            }

            if (!centerSupported && supportCount < 2)
                return false;

            // Center height is the stable root height on slopes. The old highest-footprint
            // behavior made the capsule ride its uphill sample and visibly stair-step.
            ServerCollisionTriangle chosen = centerSupported ? center : best;
            float chosenY = centerSupported ? centerY : bestY;

            hit = new ServerGroundHit(
                chosen.surfaceId,
                new WorldPosition(feet.X, chosenY, feet.Z),
                chosen.normalX,
                chosen.normalY,
                chosen.normalZ);
            return true;
        }

        public bool IsCapsuleClear(WorldPosition feet, ServerCapsule capsule)
        {
            return IsCapsuleClearInternal(
                feet,
                capsule,
                ignoreGroundContact: false,
                maximumSlopeDegrees: 0f);
        }

        /// <summary>
        /// Capsule clearance for an actor that already has authoritative walkable support
        /// under its feet. Upward/downward wound floor triangles near the feet are treated
        /// as support, not as a wall penetrating the capsule.
        ///
        /// A vertical capsule resting on an inclined plane geometrically overlaps that plane
        /// unless the capsule is offset along the surface normal. The server motor stores a
        /// feet/root point on the walk surface instead, so normal capsule-vs-triangle distance
        /// must ignore the local supporting floor band while still testing walls, ceilings,
        /// obstacles, and dynamic blockers.
        /// </summary>
        public bool IsStandingCapsuleClear(
            WorldPosition feet,
            ServerCapsule capsule,
            float maximumSlopeDegrees)
        {
            return IsCapsuleClearInternal(
                feet,
                capsule,
                ignoreGroundContact: true,
                maximumSlopeDegrees: maximumSlopeDegrees);
        }

        private bool IsCapsuleClearInternal(
            WorldPosition feet,
            ServerCapsule capsule,
            bool ignoreGroundContact,
            float maximumSlopeDegrees)
        {
            float radius = capsule.Radius;
            float bottomY = feet.Y + radius;
            float topY = feet.Y + capsule.Height - radius;
            float minSupportNormalY = ignoreGroundContact
                ? MathF.Cos(Math.Clamp(maximumSlopeDegrees, 0f, 89.9f) * (MathF.PI / 180f))
                : 1.1f;

            QueryTriangles(feet.X - radius, feet.Z - radius, feet.X + radius, feet.Z + radius);
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle t = _triangles[index];

                if (ignoreGroundContact &&
                    IsGroundContactTriangle(
                        t,
                        feet,
                        radius,
                        minSupportNormalY,
                        maximumSlopeDegrees))
                {
                    continue;
                }

                float distSq = SegmentTriangleDistanceSquared(
                    feet.X, bottomY, feet.Z,
                    feet.X, topY, feet.Z,
                    t);

                if (distSq < radius * radius - 0.000001f)
                    return false;
            }

            QueryDynamicBlockers(feet.X - radius, feet.Z - radius, feet.X + radius, feet.Z + radius);
            foreach (long stableId in _dynamicBlockerQueryScratch)
            {
                if (_dynamicBlockers.TryGetValue(stableId, out DynamicBlockerState state) &&
                    state.Enabled &&
                    CapsuleOverlapsOrientedBox(feet, capsule, state.Definition))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsGroundContactTriangle(
            ServerCollisionTriangle triangle,
            WorldPosition feet,
            float radius,
            float minSupportNormalY,
            float maximumSlopeDegrees)
        {
            // Static collider winding is not guaranteed to point upward, so use the
            // absolute Y normal only for this local floor-contact classification.
            if (Math.Abs(triangle.normalY) < minSupportNormalY)
                return false;

            float slopeRadians =
                Math.Clamp(maximumSlopeDegrees, 0f, 89f) * (MathF.PI / 180f);

            // Across one capsule radius, a legal slope can rise by radius*tan(slope).
            // Add a small skin so triangle seams / quantization do not become blockers.
            float maxRise = radius * MathF.Tan(slopeRadians) + 0.08f;
            float maxDrop = radius * 1.5f + 0.10f;
            float ring = radius * 0.80f;

            if (IsGroundSampleOnTriangle(triangle, feet.X, feet.Z, feet.Y, maxRise, maxDrop))
                return true;
            if (IsGroundSampleOnTriangle(triangle, feet.X + ring, feet.Z, feet.Y, maxRise, maxDrop))
                return true;
            if (IsGroundSampleOnTriangle(triangle, feet.X - ring, feet.Z, feet.Y, maxRise, maxDrop))
                return true;
            if (IsGroundSampleOnTriangle(triangle, feet.X, feet.Z + ring, feet.Y, maxRise, maxDrop))
                return true;
            if (IsGroundSampleOnTriangle(triangle, feet.X, feet.Z - ring, feet.Y, maxRise, maxDrop))
                return true;

            return false;
        }

        private static bool IsGroundSampleOnTriangle(
            ServerCollisionTriangle triangle,
            float x,
            float z,
            float feetY,
            float maxRise,
            float maxDrop)
        {
            if (!TryTriangleHeightAtXZ(triangle, x, z, out float y))
                return false;

            return y <= feetY + maxRise &&
                   y >= feetY - maxDrop;
        }

        /// <summary>
        /// Resolves a short kinematic horizontal displacement with cheap substeps and
        /// axis-sliding. It is intentionally deterministic and bounded rather than a
        /// general-purpose physics solver.
        /// </summary>
        public WorldPosition ResolveHorizontalMove(
            WorldPosition startFeet,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            bool grounded,
            out bool blocked)
        {
            blocked = false;
            float length = MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            if (length <= 0.000001f)
                return startFeet;

            float maxSubstep = Math.Max(0.05f, capsule.Radius * 0.5f);
            int steps = Math.Clamp((int)Math.Ceiling(length / maxSubstep), 1, 16);
            float sx = deltaX / steps;
            float sz = deltaZ / steps;
            WorldPosition current = startFeet;

            for (int i = 0; i < steps; ++i)
            {
                if (TryResolveHorizontalCandidate(
                        current,
                        sx,
                        sz,
                        capsule,
                        stepHeight,
                        groundSnapDistance,
                        maximumSlopeDegrees,
                        grounded,
                        out WorldPosition resolved))
                {
                    current = resolved;
                    continue;
                }

                // Soft slide. Grounded candidates still require authored walkable support,
                // so ordinary movement stops at a walk-surface edge rather than falling.
                bool canX = TryResolveHorizontalCandidate(
                    current, sx, 0f, capsule, stepHeight, groundSnapDistance,
                    maximumSlopeDegrees, grounded, out WorldPosition xOnly);

                bool canZ = TryResolveHorizontalCandidate(
                    current, 0f, sz, capsule, stepHeight, groundSnapDistance,
                    maximumSlopeDegrees, grounded, out WorldPosition zOnly);

                if (canX && (!canZ || Math.Abs(sx) >= Math.Abs(sz)))
                    current = xOnly;
                else if (canZ)
                    current = zOnly;
                else
                    blocked = true;
            }

            return current;
        }

        private bool TryResolveHorizontalCandidate(
            WorldPosition current,
            float deltaX,
            float deltaZ,
            ServerCapsule capsule,
            float stepHeight,
            float groundSnapDistance,
            float maximumSlopeDegrees,
            bool grounded,
            out WorldPosition resolved)
        {
            resolved = current;
            if (Math.Abs(deltaX) <= 0.000001f && Math.Abs(deltaZ) <= 0.000001f)
                return false;

            WorldPosition desired = new WorldPosition(
                current.X + deltaX,
                current.Y,
                current.Z + deltaZ);

            if (!grounded)
            {
                if (!IsCapsuleClear(desired, capsule))
                    return false;

                resolved = desired;
                return true;
            }

            // Grounded movement is projected onto the walk surface at every bounded
            // horizontal substep. Ramps therefore change Y continuously instead of moving
            // flat first and snapping upward after the whole tick.
            if (!TryFindGround(
                    desired,
                    capsule.Radius,
                    Math.Max(0.05f, stepHeight + 0.05f),
                    Math.Max(0.05f, groundSnapDistance),
                    maximumSlopeDegrees,
                    out ServerGroundHit support))
            {
                return false;
            }

            float verticalDelta = support.Position.Y - current.Y;
            if (verticalDelta > stepHeight + 0.05f ||
                verticalDelta < -groundSnapDistance - 0.05f)
            {
                return false;
            }

            WorldPosition candidate = new WorldPosition(
                desired.X,
                support.Position.Y,
                desired.Z);

            if (!IsStandingCapsuleClear(
                    candidate,
                    capsule,
                    maximumSlopeDegrees))
            {
                return false;
            }

            resolved = candidate;
            return true;
        }

        public bool TryValidateSpawn(
            ServerPose authoredPose,
            ServerCapsule capsule,
            float maximumSnapDistance,
            float maximumSlopeDegrees,
            out ServerPose resolved,
            out string detail)
        {
            resolved = authoredPose;
            detail = string.Empty;
            float maxSnap = Math.Max(0.05f, maximumSnapDistance);
            WorldPosition authored = authoredPose.ToWorldPosition();

            if (!TryFindGround(authored, capsule.Radius, maxSnap, maxSnap, maximumSlopeDegrees, out ServerGroundHit ground))
            {
                detail = "no valid supporting ground within spawn tolerance";
                return false;
            }

            float verticalError = ground.Position.Y - authored.Y;
            if (Math.Abs(verticalError) > maxSnap)
            {
                detail = $"spawn vertical error {verticalError:0.###}m exceeds tolerance {maxSnap:0.###}m";
                return false;
            }

            var feet = new WorldPosition(authored.X, ground.Position.Y, authored.Z);
            if (!IsStandingCapsuleClear(
                    feet,
                    capsule,
                    maximumSlopeDegrees))
            {
                detail = "spawn capsule overlaps authoritative blocking collision";
                return false;
            }

            resolved = new ServerPose(feet.X, feet.Y, feet.Z, authoredPose.yaw);
            return true;
        }

        private bool TryFindGroundAtPoint(
            float x,
            float z,
            float feetY,
            float probeUp,
            float probeDown,
            float minNormalY,
            bool requireWalkable,
            out float bestY,
            out ServerCollisionTriangle bestTriangle)
        {
            QueryTriangles(x - 0.01f, z - 0.01f, x + 0.01f, z + 0.01f);
            return TryFindGroundAtPointFromCurrentCandidates(
                x, z, feetY, probeUp, probeDown, minNormalY, requireWalkable, out bestY, out bestTriangle);
        }

        private bool TryFindGroundAtPointFromCurrentCandidates(
            float x,
            float z,
            float feetY,
            float probeUp,
            float probeDown,
            float minNormalY,
            bool requireWalkable,
            out float bestY,
            out ServerCollisionTriangle bestTriangle)
        {
            bestY = float.NegativeInfinity;
            bestTriangle = default;
            bool found = false;
            foreach (int index in _queryScratch)
            {
                ServerCollisionTriangle t = _triangles[index];
                if ((requireWalkable && (t.flags & ServerSurfaceFlags.Walkable) == 0) ||
                    t.normalY < minNormalY)
                {
                    continue;
                }
                if (!TryTriangleHeightAtXZ(t, x, z, out float y))
                    continue;
                if (y > feetY + Math.Max(0f, probeUp) || y < feetY - Math.Max(0f, probeDown))
                    continue;
                if (!found || y > bestY)
                {
                    found = true;
                    bestY = y;
                    bestTriangle = t;
                }
            }
            return found;
        }

        private void ComputeWorldBounds()
        {
            _hasWorldBounds = false;
            _minX = _minY = _minZ = float.PositiveInfinity;
            _maxX = _maxY = _maxZ = float.NegativeInfinity;

            for (int i = 0; i < _triangles.Length; ++i)
            {
                ServerCollisionTriangle t = _triangles[i];
                ExpandBounds(t.ax, t.ay, t.az);
                ExpandBounds(t.bx, t.by, t.bz);
                ExpandBounds(t.cx, t.cy, t.cz);
            }
        }

        private void ExpandBounds(float x, float y, float z)
        {
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                return;

            if (!_hasWorldBounds)
            {
                _minX = _maxX = x;
                _minY = _maxY = y;
                _minZ = _maxZ = z;
                _hasWorldBounds = true;
                return;
            }

            _minX = Math.Min(_minX, x);
            _minY = Math.Min(_minY, y);
            _minZ = Math.Min(_minZ, z);
            _maxX = Math.Max(_maxX, x);
            _maxY = Math.Max(_maxY, y);
            _maxZ = Math.Max(_maxZ, z);
        }

        private void BuildTriangleGrid()
        {
            for (int i = 0; i < _triangles.Length; ++i)
            {
                ServerCollisionTriangle t = _triangles[i];
                float minX = Math.Min(t.ax, Math.Min(t.bx, t.cx));
                float maxX = Math.Max(t.ax, Math.Max(t.bx, t.cx));
                float minZ = Math.Min(t.az, Math.Min(t.bz, t.cz));
                float maxZ = Math.Max(t.az, Math.Max(t.bz, t.cz));
                int minCellX = ToCell(minX);
                int maxCellX = ToCell(maxX);
                int minCellZ = ToCell(minZ);
                int maxCellZ = ToCell(maxZ);
                for (int z = minCellZ; z <= maxCellZ; ++z)
                for (int x = minCellX; x <= maxCellX; ++x)
                {
                    var key = new CellKey(x, z);
                    if (!_triangleGrid.TryGetValue(key, out List<int> list))
                    {
                        list = new List<int>(16);
                        _triangleGrid.Add(key, list);
                    }
                    list.Add(i);
                }
            }
        }

        private void IndexDynamicBlocker(ServerDynamicBlocker blocker)
        {
            if (blocker == null || blocker.stableId <= 0)
                return;

            float halfX = Math.Max(0.01f, blocker.sizeX * 0.5f);
            float halfZ = Math.Max(0.01f, blocker.sizeZ * 0.5f);
            float yaw = blocker.pose.yaw * (MathF.PI / 180f);
            float cos = MathF.Abs(MathF.Cos(yaw));
            float sin = MathF.Abs(MathF.Sin(yaw));
            float extentX = cos * halfX + sin * halfZ;
            float extentZ = sin * halfX + cos * halfZ;

            int minCellX = ToCell(blocker.pose.x - extentX);
            int maxCellX = ToCell(blocker.pose.x + extentX);
            int minCellZ = ToCell(blocker.pose.z - extentZ);
            int maxCellZ = ToCell(blocker.pose.z + extentZ);
            for (int z = minCellZ; z <= maxCellZ; ++z)
            for (int x = minCellX; x <= maxCellX; ++x)
            {
                var key = new CellKey(x, z);
                if (!_dynamicBlockerGrid.TryGetValue(key, out List<long> list))
                {
                    list = new List<long>(4);
                    _dynamicBlockerGrid.Add(key, list);
                }
                list.Add(blocker.stableId);
            }
        }

        private void QueryDynamicBlockers(float minX, float minZ, float maxX, float maxZ)
        {
            _dynamicBlockerQueryScratch.Clear();
            int minCellX = ToCell(minX);
            int maxCellX = ToCell(maxX);
            int minCellZ = ToCell(minZ);
            int maxCellZ = ToCell(maxZ);
            for (int z = minCellZ; z <= maxCellZ; ++z)
            for (int x = minCellX; x <= maxCellX; ++x)
            {
                if (!_dynamicBlockerGrid.TryGetValue(new CellKey(x, z), out List<long> list))
                    continue;
                for (int i = 0; i < list.Count; ++i)
                    _dynamicBlockerQueryScratch.Add(list[i]);
            }
        }

        private void QueryTriangles(float minX, float minZ, float maxX, float maxZ)
        {
            _queryScratch.Clear();
            int minCellX = ToCell(minX);
            int maxCellX = ToCell(maxX);
            int minCellZ = ToCell(minZ);
            int maxCellZ = ToCell(maxZ);
            for (int z = minCellZ; z <= maxCellZ; ++z)
            for (int x = minCellX; x <= maxCellX; ++x)
            {
                if (!_triangleGrid.TryGetValue(new CellKey(x, z), out List<int> list))
                    continue;
                for (int i = 0; i < list.Count; ++i)
                    _queryScratch.Add(list[i]);
            }
        }

        private int ToCell(float value) => (int)MathF.Floor(value / _cellSize);

        private static bool TryTriangleHeightAtXZ(ServerCollisionTriangle t, float x, float z, out float y)
        {
            float v0x = t.bx - t.ax;
            float v0z = t.bz - t.az;
            float v1x = t.cx - t.ax;
            float v1z = t.cz - t.az;
            float v2x = x - t.ax;
            float v2z = z - t.az;
            float den = v0x * v1z - v1x * v0z;
            if (Math.Abs(den) <= 0.0000001f)
            {
                y = 0f;
                return false;
            }
            float inv = 1f / den;
            float u = (v2x * v1z - v1x * v2z) * inv;
            float v = (v0x * v2z - v2x * v0z) * inv;
            const float epsilon = 0.0005f;
            if (u < -epsilon || v < -epsilon || u + v > 1f + epsilon)
            {
                y = 0f;
                return false;
            }
            y = t.ay + u * (t.by - t.ay) + v * (t.cy - t.ay);
            return IsFinite(y);
        }

        private static bool SegmentIntersectsOrientedBox(WorldPosition from, WorldPosition to, ServerDynamicBlocker box)
        {
            float yaw = -box.pose.yaw * (MathF.PI / 180f);
            float cos = MathF.Cos(yaw);
            float sin = MathF.Sin(yaw);
            static void ToLocal(WorldPosition p, ServerDynamicBlocker b, float c, float si, out float x, out float y, out float z)
            {
                float dx = p.X - b.pose.x;
                float dz = p.Z - b.pose.z;
                x = dx * c - dz * si;
                z = dx * si + dz * c;
                y = p.Y - b.pose.y;
            }

            ToLocal(from, box, cos, sin, out float fx, out float fy, out float fz);
            ToLocal(to, box, cos, sin, out float tx, out float ty, out float tz);
            float dx = tx - fx, dy = ty - fy, dz = tz - fz;
            float hx = Math.Max(0.01f, box.sizeX * 0.5f);
            float hy = Math.Max(0.01f, box.sizeY * 0.5f);
            float hz = Math.Max(0.01f, box.sizeZ * 0.5f);
            float tMin = 0f, tMax = 1f;

            static bool Slab(float origin, float direction, float min, float max, ref float enter, ref float exit)
            {
                if (Math.Abs(direction) < 0.0000001f)
                    return origin >= min && origin <= max;
                float inv = 1f / direction;
                float a = (min - origin) * inv;
                float b = (max - origin) * inv;
                if (a > b) { float temp = a; a = b; b = temp; }
                enter = Math.Max(enter, a);
                exit = Math.Min(exit, b);
                return enter <= exit;
            }

            return Slab(fx, dx, -hx, hx, ref tMin, ref tMax) &&
                   Slab(fy, dy, -hy, hy, ref tMin, ref tMax) &&
                   Slab(fz, dz, -hz, hz, ref tMin, ref tMax);
        }

        private static bool CapsuleOverlapsOrientedBox(WorldPosition feet, ServerCapsule capsule, ServerDynamicBlocker box)
        {
            float yaw = -box.pose.yaw * (MathF.PI / 180f);
            float cos = MathF.Cos(yaw);
            float sin = MathF.Sin(yaw);
            float dx = feet.X - box.pose.x;
            float dz = feet.Z - box.pose.z;
            float localX = dx * cos - dz * sin;
            float localZ = dx * sin + dz * cos;
            float halfX = Math.Max(0.01f, box.sizeX * 0.5f);
            float halfY = Math.Max(0.01f, box.sizeY * 0.5f);
            float halfZ = Math.Max(0.01f, box.sizeZ * 0.5f);
            float nearestX = Math.Clamp(localX, -halfX, halfX);
            float nearestZ = Math.Clamp(localZ, -halfZ, halfZ);
            float horizontalDx = localX - nearestX;
            float horizontalDz = localZ - nearestZ;
            if (horizontalDx * horizontalDx + horizontalDz * horizontalDz >= capsule.Radius * capsule.Radius)
                return false;

            float capsuleMinY = feet.Y;
            float capsuleMaxY = feet.Y + capsule.Height;
            float boxMinY = box.pose.y - halfY;
            float boxMaxY = box.pose.y + halfY;
            return capsuleMaxY > boxMinY && capsuleMinY < boxMaxY;
        }

        private static float SegmentTriangleDistanceSquared(
            float p0x, float p0y, float p0z,
            float p1x, float p1y, float p1z,
            ServerCollisionTriangle t)
        {
            if (SegmentIntersectsTriangle(p0x, p0y, p0z, p1x, p1y, p1z, t))
                return 0f;

            float best = PointTriangleDistanceSquared(p0x, p0y, p0z, t);
            best = Math.Min(best, PointTriangleDistanceSquared(p1x, p1y, p1z, t));
            best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.ax, t.ay, t.az, t.bx, t.by, t.bz));
            best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.bx, t.by, t.bz, t.cx, t.cy, t.cz));
            best = Math.Min(best, SegmentSegmentDistanceSquared(p0x, p0y, p0z, p1x, p1y, p1z, t.cx, t.cy, t.cz, t.ax, t.ay, t.az));
            return best;
        }

        private static bool SegmentIntersectsTriangle(float p0x, float p0y, float p0z, float p1x, float p1y, float p1z, ServerCollisionTriangle t)
        {
            float dx = p1x - p0x, dy = p1y - p0y, dz = p1z - p0z;
            float e1x = t.bx - t.ax, e1y = t.by - t.ay, e1z = t.bz - t.az;
            float e2x = t.cx - t.ax, e2y = t.cy - t.ay, e2z = t.cz - t.az;
            float hx = dy * e2z - dz * e2y;
            float hy = dz * e2x - dx * e2z;
            float hz = dx * e2y - dy * e2x;
            float a = e1x * hx + e1y * hy + e1z * hz;
            if (Math.Abs(a) < 0.0000001f) return false;
            float f = 1f / a;
            float sx = p0x - t.ax, sy = p0y - t.ay, sz = p0z - t.az;
            float u = f * (sx * hx + sy * hy + sz * hz);
            if (u < 0f || u > 1f) return false;
            float qx = sy * e1z - sz * e1y;
            float qy = sz * e1x - sx * e1z;
            float qz = sx * e1y - sy * e1x;
            float v = f * (dx * qx + dy * qy + dz * qz);
            if (v < 0f || u + v > 1f) return false;
            float along = f * (e2x * qx + e2y * qy + e2z * qz);
            return along >= 0f && along <= 1f;
        }

        // Ericson, Real-Time Collision Detection: closest point on triangle.
        private static float PointTriangleDistanceSquared(float px, float py, float pz, ServerCollisionTriangle t)
        {
            float ax=t.ax, ay=t.ay, az=t.az, bx=t.bx, by=t.by, bz=t.bz, cx=t.cx, cy=t.cy, cz=t.cz;
            float abx=bx-ax, aby=by-ay, abz=bz-az, acx=cx-ax, acy=cy-ay, acz=cz-az, apx=px-ax, apy=py-ay, apz=pz-az;
            float d1=abx*apx+aby*apy+abz*apz, d2=acx*apx+acy*apy+acz*apz;
            if (d1<=0f && d2<=0f) return DistSq(px,py,pz,ax,ay,az);
            float bpx=px-bx,bpy=py-by,bpz=pz-bz, d3=abx*bpx+aby*bpy+abz*bpz, d4=acx*bpx+acy*bpy+acz*bpz;
            if (d3>=0f && d4<=d3) return DistSq(px,py,pz,bx,by,bz);
            float vc=d1*d4-d3*d2;
            if (vc<=0f && d1>=0f && d3<=0f) { float v=d1/(d1-d3); return DistSq(px,py,pz,ax+v*abx,ay+v*aby,az+v*abz); }
            float cpx=px-cx,cpy=py-cy,cpz=pz-cz, d5=abx*cpx+aby*cpy+abz*cpz, d6=acx*cpx+acy*cpy+acz*cpz;
            if (d6>=0f && d5<=d6) return DistSq(px,py,pz,cx,cy,cz);
            float vb=d5*d2-d1*d6;
            if (vb<=0f && d2>=0f && d6<=0f) { float w=d2/(d2-d6); return DistSq(px,py,pz,ax+w*acx,ay+w*acy,az+w*acz); }
            float va=d3*d6-d5*d4;
            if (va<=0f && (d4-d3)>=0f && (d5-d6)>=0f) { float w=(d4-d3)/((d4-d3)+(d5-d6)); return DistSq(px,py,pz,bx+w*(cx-bx),by+w*(cy-by),bz+w*(cz-bz)); }
            float denom=1f/(va+vb+vc); float v2=vb*denom,w2=vc*denom;
            return DistSq(px,py,pz,ax+abx*v2+acx*w2,ay+aby*v2+acy*w2,az+abz*v2+acz*w2);
        }

        private static float SegmentSegmentDistanceSquared(
            float p1x,float p1y,float p1z,float q1x,float q1y,float q1z,
            float p2x,float p2y,float p2z,float q2x,float q2y,float q2z)
        {
            float d1x=q1x-p1x,d1y=q1y-p1y,d1z=q1z-p1z,d2x=q2x-p2x,d2y=q2y-p2y,d2z=q2z-p2z,rx=p1x-p2x,ry=p1y-p2y,rz=p1z-p2z;
            float a=d1x*d1x+d1y*d1y+d1z*d1z,e=d2x*d2x+d2y*d2y+d2z*d2z,f=d2x*rx+d2y*ry+d2z*rz;
            float s,t;
            if (a<=0.0000001f && e<=0.0000001f) return DistSq(p1x,p1y,p1z,p2x,p2y,p2z);
            if (a<=0.0000001f) { s=0f; t=Math.Clamp(f/e,0f,1f); }
            else
            {
                float c=d1x*rx+d1y*ry+d1z*rz;
                if (e<=0.0000001f) { t=0f; s=Math.Clamp(-c/a,0f,1f); }
                else
                {
                    float b=d1x*d2x+d1y*d2y+d1z*d2z,den=a*e-b*b;
                    s=den!=0f?Math.Clamp((b*f-c*e)/den,0f,1f):0f;
                    t=(b*s+f)/e;
                    if (t<0f) { t=0f; s=Math.Clamp(-c/a,0f,1f); }
                    else if (t>1f) { t=1f; s=Math.Clamp((b-c)/a,0f,1f); }
                }
            }
            float c1x=p1x+d1x*s,c1y=p1y+d1y*s,c1z=p1z+d1z*s,c2x=p2x+d2x*t,c2y=p2y+d2y*t,c2z=p2z+d2z*t;
            return DistSq(c1x,c1y,c1z,c2x,c2y,c2z);
        }

        private static float DistSq(float ax,float ay,float az,float bx,float by,float bz)
        { float x=ax-bx,y=ay-by,z=az-bz; return x*x+y*y+z*z; }
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
