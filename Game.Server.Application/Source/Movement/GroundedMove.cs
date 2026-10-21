using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Game.Server.Application.World;
using Game.Shared.World;

namespace Game.Server.Application.Movement
{
    public struct GroundedMoveTiming
    {
        public int RequestedSteps;
        public int CompletedSteps;
        public int FailedStep;
        public string Rejection;

        public int SupportCalls;
        public int SupportMisses;
        public double SupportMilliseconds;
        public double MaxSupportMilliseconds;
        public int AheadCalls;
        public int AheadMisses;
        public double AheadMilliseconds;
        public double MaxAheadMilliseconds;
        public int CapsuleCalls;
        public int CapsuleRejects;
        public double CapsuleMilliseconds;
        public double MaxCapsuleMilliseconds;
        public int RiseCapsuleCalls;
        public int RiseCapsuleRejects;
        public double RiseCapsuleMilliseconds;
        public double MaxRiseCapsuleMilliseconds;
        public int CertifiedFlatSteps;
        public double TimedQueryMilliseconds => SupportMilliseconds + AheadMilliseconds +
                                                CapsuleMilliseconds + RiseCapsuleMilliseconds;
    }

    /// <summary>
    /// Canonical grounded PLAYER movement rules, identical in Unity and standalone .NET.
    /// Flat-clearance summaries are derived once from the SAME immutable collision
    /// triangles already consumed by ServerCollisionWorld; they do not create a new
    /// source of movement authority. Unsupported/uncertain cells use exact collision.
    /// Airborne movement, jumps, loading and Population movement are not modified.
    /// </summary>
    public static class GroundedMove
    {
        private const float FlatCellSize = 4f;
        private const int MaximumFlatCells = 250000;
        private const float FlatTolerance = 0.01f;
        private static readonly ConditionalWeakTable<ServerCollisionWorld, FlatClearance> Worlds =
            new ConditionalWeakTable<ServerCollisionWorld, FlatClearance>();

        // This index is built ONCE at map construction. It contains the real baked
        // physical triangles, not a speculative movement collider or a second map.
        // A cell references only triangles whose XZ AABB touches that cell.
        private sealed class FlatCell
        {
            public List<int> Triangles;
            public bool HasBlocker;
        }

        private sealed class FlatClearance
        {
            public readonly Dictionary<long, FlatCell> Cells;
            public readonly ServerCollisionTriangle[] Triangles;
            // Rare huge floor triangles avoid a gigantic spatial index; each is
            // checked with cheap AABBs, never a per-tick exact capsule predicate.
            public readonly List<int> LargeTriangles;
            public readonly bool HasBlockerDefinitions;
            public FlatClearance(Dictionary<long, FlatCell> cells,
                ServerCollisionTriangle[] triangles, List<int> largeTriangles,
                bool hasBlockerDefinitions)
            {
                Cells = cells;
                Triangles = triangles;
                LargeTriangles = largeTriangles;
                HasBlockerDefinitions = hasBlockerDefinitions;
            }
        }

        /// <summary>
        /// Build an immutable, bounded static-collision candidate index before any
        /// players enter the world. The earlier min-normal/max-vertex summary was
        /// overly conservative: one steep or tall triangle anywhere in a 4m cell
        /// disabled clearance for that entire cell, even if it was metres away
        /// from the actual swept player capsule. Query-time candidates are now
        /// rejected using exact XZ and height AABBs BEFORE any capsule math.
        /// Ambiguous candidates always fall through to the EXISTING exact query.
        /// </summary>
        public static void RegisterWorld(ServerCollisionWorld world,
            ServerCollisionTriangle[] collisionTriangles,
            ServerDynamicBlocker[] blockers)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            collisionTriangles = collisionTriangles ?? Array.Empty<ServerCollisionTriangle>();
            const int maxReferences = 2000000;
            var cells = new Dictionary<long, FlatCell>();
            var largeTriangles = new List<int>();
            bool usable = true;
            int references = 0;

            for (int i = 0; i < collisionTriangles.Length && usable; ++i)
            {
                ServerCollisionTriangle t = collisionTriangles[i];
                float minX = Math.Min(t.ax, Math.Min(t.bx, t.cx));
                float maxX = Math.Max(t.ax, Math.Max(t.bx, t.cx));
                float minZ = Math.Min(t.az, Math.Min(t.bz, t.cz));
                float maxZ = Math.Max(t.az, Math.Max(t.bz, t.cz));
                if (!Finite(minX) || !Finite(maxX) || !Finite(minZ) ||
                    !Finite(maxZ) || !Finite(t.ay) || !Finite(t.by) ||
                    !Finite(t.cy) || !Finite(t.normalY) ||
                    minX / FlatCellSize < int.MinValue + 1d ||
                    maxX / FlatCellSize > int.MaxValue - 1d ||
                    minZ / FlatCellSize < int.MinValue + 1d ||
                    maxZ / FlatCellSize > int.MaxValue - 1d)
                {
                    usable = false;
                    break;
                }

                int ax = CellIndex(minX), bx = CellIndex(maxX);
                int az = CellIndex(minZ), bz = CellIndex(maxZ);
                long width = (long)bx - ax + 1;
                long depth = (long)bz - az + 1;
                if (width <= 0 || depth <= 0)
                {
                    usable = false;
                    break;
                }
                // A few exceptionally large triangles (such as a whole-plane
                // floor) must not disable safe walking throughout an entire map.
                // Keep their ids in a small immutable overflow list, not thousands
                // of per-cell duplicates. The list is bounded for tick safety.
                if (width > 1024 / depth ||
                    references + width * depth > maxReferences)
                {
                    largeTriangles.Add(i);
                    if (largeTriangles.Count > 512) usable = false;
                    continue;
                }
                for (int z = az; z <= bz && usable; ++z)
                for (int x = ax; x <= bx; ++x)
                {
                    long key = CellKey(x, z);
                    if (!cells.TryGetValue(key, out FlatCell cell))
                    {
                        if (cells.Count >= MaximumFlatCells)
                        {
                            usable = false;
                            break;
                        }
                        cell = new FlatCell { Triangles = new List<int>(4) };
                        cells.Add(key, cell);
                    }
                    cell.Triangles.Add(i);
                    ++references;
                }
            }

            // Existing door/blocker footprints are treated as local uncertainty;
            // a blocker on the opposite side of the map never disables open-ground
            // certification. Conservatively use the enclosing rotated rectangle.
            if (usable && blockers != null)
            {
                for (int i = 0; i < blockers.Length && usable; ++i)
                {
                    ServerDynamicBlocker blocker = blockers[i];
                    if (blocker == null || blocker.stableId <= 0)
                        continue;
                    float bx = blocker.pose.x, bz = blocker.pose.z;
                    float yaw = blocker.pose.yaw;
                    float halfX = Math.Abs(blocker.sizeX) * 0.5f;
                    float halfZ = Math.Abs(blocker.sizeZ) * 0.5f;
                    if (!Finite(bx) || !Finite(bz) || !Finite(yaw) ||
                        !Finite(halfX) || !Finite(halfZ))
                    {
                        usable = false;
                        break;
                    }
                    float radians = yaw * (MathF.PI / 180f);
                    float cosine = Math.Abs(MathF.Cos(radians));
                    float sine = Math.Abs(MathF.Sin(radians));
                    float extentX = cosine * halfX + sine * halfZ;
                    float extentZ = sine * halfX + cosine * halfZ;
                    float minX = bx - extentX, maxX = bx + extentX;
                    float minZ = bz - extentZ, maxZ = bz + extentZ;
                    if (!Finite(minX) || !Finite(maxX) || !Finite(minZ) ||
                        !Finite(maxZ) ||
                        minX / FlatCellSize < int.MinValue + 1d ||
                        maxX / FlatCellSize > int.MaxValue - 1d ||
                        minZ / FlatCellSize < int.MinValue + 1d ||
                        maxZ / FlatCellSize > int.MaxValue - 1d)
                    {
                        usable = false;
                        break;
                    }
                    int ax = CellIndex(minX), cx = CellIndex(maxX);
                    int az = CellIndex(minZ), cz = CellIndex(maxZ);
                    long width = (long)cx - ax + 1, depth = (long)cz - az + 1;
                    if (width <= 0 || depth <= 0 ||
                        width > MaximumFlatCells / depth ||
                        cells.Count + width * depth > MaximumFlatCells * 4L)
                    {
                        usable = false;
                        break;
                    }
                    for (int z = az; z <= cz && usable; ++z)
                    for (int x = ax; x <= cx; ++x)
                    {
                        long key = CellKey(x, z);
                        if (!cells.TryGetValue(key, out FlatCell cell))
                        {
                            if (cells.Count >= MaximumFlatCells)
                            {
                                usable = false;
                                break;
                            }
                            cell = new FlatCell();
                            cells.Add(key, cell);
                        }
                        cell.HasBlocker = true;
                    }
                }
            }
            if (!usable) cells = null;
            Worlds.Remove(world);
            Worlds.Add(world, new FlatClearance(
                cells, collisionTriangles, largeTriangles,
                blockers != null && blockers.Length != 0));
        }

        /// <summary>
        /// The motor already establishes a validated grounded pose before taking this
        /// idle early-out. Static collision cannot change at unchanged world/feet.
        /// Only locally authored blocker footprints prevent reuse: their live state
        /// may change, so an idle actor within their footprint must revalidate.
        /// </summary>
        public static bool CanReuseStationaryGround(ServerCollisionWorld world,
            WorldPosition feet, ServerCapsule capsule)
        {
            if (world == null || !Worlds.TryGetValue(world, out FlatClearance map) ||
                !Finite(feet.X) || !Finite(feet.Z))
                return false;
            // Static geometry never changes. Even an oversized map with its
            // optional flat certificate disabled can safely reuse an already
            // validated idle pose if it has NO authored live blockers.
            if (map.Cells == null)
                return !map.HasBlockerDefinitions;
            float radius = capsule.Radius;
            return NoBlockerCells(map.Cells, feet.X - radius, feet.X + radius,
                feet.Z - radius, feet.Z + radius);
        }

        /// <summary>
        /// Blocker footprints are indexed ONCE at map load. This predicate is
        /// conservative, independent of live enabled flags, and tests only the
        /// already-authored cells touched by the full proposed capsule sweep.
        /// </summary>
        private static bool NoBlockerCells(Dictionary<long, FlatCell> cells,
            float minX, float maxX, float minZ, float maxZ)
        {
            if (cells == null || !Finite(minX) || !Finite(maxX) ||
                !Finite(minZ) || !Finite(maxZ) ||
                minX / FlatCellSize < int.MinValue + 1d ||
                maxX / FlatCellSize > int.MaxValue - 1d ||
                minZ / FlatCellSize < int.MinValue + 1d ||
                maxZ / FlatCellSize > int.MaxValue - 1d)
                return false;
            int ax = CellIndex(minX), bx = CellIndex(maxX);
            int az = CellIndex(minZ), bz = CellIndex(maxZ);
            if ((long)bx - ax > 8 || (long)bz - az > 8)
                return false;
            for (int z = az; z <= bz; ++z)
            for (int x = ax; x <= bx; ++x)
                if (cells.TryGetValue(CellKey(x, z), out FlatCell cell) &&
                    cell.HasBlocker)
                    return false;
            return true;
        }

        /// <summary>
        /// Conservative whole-sweep static-clearance proof. Only accepts when
        /// NO triangle whose exact XZ/vertical bounds reach the swept body could
        /// block the existing standing capsule predicate. Flat support triangles
        /// below the original floor-exemption threshold are harmless. An uncertain
        /// candidate means FALLBACK to the exact old collision result, never acceptance.
        /// No allocations, grid construction, or mutable world work occurs here.
        /// </summary>
        private static bool CertifiedStaticFlatSweep(ServerCollisionWorld world,
            WorldPosition start, WorldPosition target, ServerCapsule capsule, float slope)
        {
            if (!Worlds.TryGetValue(world, out FlatClearance map) ||
                map.Cells == null ||
                !Finite(start.X) || !Finite(start.Y) || !Finite(start.Z) ||
                !Finite(target.X) || !Finite(target.Y) || !Finite(target.Z) ||
                !Finite(slope) || Math.Abs(target.Y - start.Y) > FlatTolerance)
                return false;

            float radius = capsule.Radius;
            float feetLow = Math.Min(start.Y, target.Y) - FlatTolerance;
            float sweptTop = Math.Max(start.Y, target.Y) + capsule.Height;
            float allowedFloorTop = feetLow + radius * 1.5f;
            float walkableNormal = MathF.Cos(
                Math.Clamp(slope, 0f, 89.9f) * (MathF.PI / 180f));
            float minX = Math.Min(start.X, target.X) - radius;
            float maxX = Math.Max(start.X, target.X) + radius;
            float minZ = Math.Min(start.Z, target.Z) - radius;
            float maxZ = Math.Max(start.Z, target.Z) + radius;
            if (!Finite(minX) || !Finite(maxX) || !Finite(minZ) || !Finite(maxZ) ||
                minX / FlatCellSize < int.MinValue + 1d ||
                maxX / FlatCellSize > int.MaxValue - 1d ||
                minZ / FlatCellSize < int.MinValue + 1d ||
                maxZ / FlatCellSize > int.MaxValue - 1d)
                return false;

            int ax = CellIndex(minX), bx = CellIndex(maxX);
            int az = CellIndex(minZ), bz = CellIndex(maxZ);
            if ((long)bx - ax > 8 || (long)bz - az > 8)
                return false;
            for (int z = az; z <= bz; ++z)
            for (int x = ax; x <= bx; ++x)
            {
                if (!map.Cells.TryGetValue(CellKey(x, z), out FlatCell cell))
                    continue;
                if (cell.HasBlocker)
                    return false;
                List<int> list = cell.Triangles;
                if (list == null)
                    continue;
                for (int i = 0; i < list.Count; ++i)
                {
                    if (!TriangleProvablyClear(map.Triangles[list[i]],
                            minX, maxX, minZ, maxZ, feetLow, sweptTop,
                            walkableNormal, allowedFloorTop))
                        return false;
                }
            }
            // Handle only the rare oversized triangles without constructing large
            // cell lists at startup or reindexing anything during movement.
            for (int i = 0; i < map.LargeTriangles.Count; ++i)
            {
                if (!TriangleProvablyClear(map.Triangles[map.LargeTriangles[i]],
                        minX, maxX, minZ, maxZ, feetLow, sweptTop,
                        walkableNormal, allowedFloorTop))
                    return false;
            }
            return true;
        }

        private static bool TriangleProvablyClear(ServerCollisionTriangle t,
            float minX, float maxX, float minZ, float maxZ,
            float feetLow, float sweptTop, float walkableNormal,
            float allowedFloorTop)
        {
            if ((t.ax > maxX && t.bx > maxX && t.cx > maxX) ||
                (t.ax < minX && t.bx < minX && t.cx < minX) ||
                (t.az > maxZ && t.bz > maxZ && t.cz > maxZ) ||
                (t.az < minZ && t.bz < minZ && t.cz < minZ) ||
                (t.ay > sweptTop && t.by > sweptTop && t.cy > sweptTop) ||
                (t.ay < feetLow && t.by < feetLow && t.cy < feetLow))
                return true;
            // Exactly the existing standing-capsule floor exemption, with a
            // slightly tighter threshold. No sloped/overhead collision is waived.
            return t.normalY >= walkableNormal &&
                   Math.Max(t.ay, Math.Max(t.by, t.cy)) <= allowedFloorTop;
        }

        private static int CellIndex(float value) => (int)MathF.Floor(value / FlatCellSize);
        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static double Elapsed(long stamp) =>
            (Stopwatch.GetTimestamp() - stamp) * (1000.0 / Stopwatch.Frequency);

        public static WorldPosition Resolve(ServerCollisionWorld world, WorldPosition start,
            float deltaX, float deltaZ, ServerCapsule capsule, float stepHeight,
            float snapDown, float slope, out bool blocked)
        {
            GroundedMoveTiming ignore = default;
            return ResolveCore(world, start, deltaX, deltaZ, capsule, stepHeight,
                snapDown, slope, out blocked, out _, out _, ref ignore, false);
        }

        // Diagnostic-compatible original overload.
        public static WorldPosition Resolve(ServerCollisionWorld world, WorldPosition start,
            float deltaX, float deltaZ, ServerCapsule capsule, float stepHeight,
            float snapDown, float slope, out bool blocked, out GroundedMoveTiming timing)
        {
            timing = default;
            return ResolveCore(world, start, deltaX, deltaZ, capsule, stepHeight,
                snapDown, slope, out blocked, out _, out _, ref timing, true);
        }

        // The motor consumes the final authoritative support hit. No duplicate
        // TryFindGround in the normal accepted, settled grounded-movement path.
        public static WorldPosition Resolve(ServerCollisionWorld world, WorldPosition start,
            float deltaX, float deltaZ, ServerCapsule capsule, float stepHeight,
            float snapDown, float slope, out bool blocked,
            out ServerGroundHit finalSupport, out bool settledSupport,
            out GroundedMoveTiming timing)
        {
            timing = default;
            return ResolveCore(world, start, deltaX, deltaZ, capsule, stepHeight,
                snapDown, slope, out blocked, out finalSupport, out settledSupport,
                ref timing, true);
        }

        private static WorldPosition ResolveCore(ServerCollisionWorld world, WorldPosition start,
            float deltaX, float deltaZ, ServerCapsule capsule, float stepHeight,
            float snapDown, float slope, out bool blocked,
            out ServerGroundHit finalSupport, out bool settledSupport,
            ref GroundedMoveTiming timing, bool measure)
        {
            blocked = false;
            finalSupport = default;
            settledSupport = false;
            if (world == null) return start;
            if (!Finite(start.X) || !Finite(start.Y) || !Finite(start.Z) ||
                !Finite(deltaX) || !Finite(deltaZ))
            {
                blocked = true;
                if (measure) timing.Rejection = "invalid-input";
                return start;
            }
            float length = MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            if (length <= 0.000001f) return start;

            // A single-substep flat move: one support query and zero static capsule
            // scans when the baked triangle summary proves the WHOLE swept body safe.
            // Longer moves retain the original intermediate support checks.
            // Only bypass the substep loop for a move that previously required
            // ONE support sample. A longer sprint move still checks its intermediate
            // support positions, so narrow gaps cannot be skipped.
            if (length <= Math.Max(0.05f, capsule.Radius * 0.5f) &&
                CertifiedStaticFlatSweep(world, start,
                    new WorldPosition(start.X + deltaX, start.Y, start.Z + deltaZ),
                    capsule, slope))
            {
                var destination = new WorldPosition(start.X + deltaX, start.Y, start.Z + deltaZ);
                if (FindGround(world, destination, capsule.Radius,
                        Math.Max(0.05f, stepHeight + 0.05f),
                        Math.Max(0.05f, snapDown), slope,
                        out ServerGroundHit ground, ref timing, measure, false) &&
                    Math.Abs(ground.Position.Y - start.Y) <= FlatTolerance)
                {
                    finalSupport = ground;
                    settledSupport = true;
                    if (measure)
                    {
                        timing.RequestedSteps = 1;
                        timing.CompletedSteps = 1;
                        timing.CertifiedFlatSteps = 1;
                    }
                    return new WorldPosition(destination.X, ground.Position.Y, destination.Z);
                }
                // Elevation changed: use canonical exact stair/edge handling.
            }

            float stepLength = Math.Max(0.05f, capsule.Radius * 0.5f);
            int steps = (int)Math.Ceiling(length / stepLength);
            if (measure) timing.RequestedSteps = steps;
            if (steps > 16)
            {
                blocked = true;
                if (measure) timing.Rejection = "too-many-substeps";
                return start;
            }
            float dx = deltaX / steps;
            float dz = deltaZ / steps;
            WorldPosition current = start;
            for (int i = 0; i < steps; ++i)
            {
                if (!TryStep(world, current, dx, dz, capsule, stepHeight,
                        snapDown, slope, out WorldPosition next, out ServerGroundHit ground,
                        out bool exactHeight, ref timing, measure))
                {
                    blocked = true;
                    if (measure) timing.FailedStep = i + 1;
                    return start;
                }
                current = next;
                finalSupport = ground;
                settledSupport = exactHeight;
                if (measure) timing.CompletedSteps++;
            }
            return current;
        }

        private static bool TryStep(ServerCollisionWorld world, WorldPosition start,
            float dx, float dz, ServerCapsule capsule, float stepHeight,
            float snapDown, float slope, out WorldPosition next,
            out ServerGroundHit finalSupport, out bool settledSupport,
            ref GroundedMoveTiming timing, bool measure)
        {
            next = start;
            finalSupport = default;
            settledSupport = false;
            WorldPosition target = new WorldPosition(start.X + dx, start.Y, start.Z + dz);
            float up = Math.Max(0.05f, stepHeight + 0.05f);
            float down = Math.Max(0.05f, snapDown);
            if (!FindGround(world, target, capsule.Radius, up, down, slope,
                    out ServerGroundHit ground, ref timing, measure, false))
            {
                if (measure) timing.Rejection = "no-support";
                return false;
            }
            float y = ground.Position.Y;
            if (y > start.Y + stepHeight + 0.05f)
            {
                if (measure) timing.Rejection = "step-too-high";
                return false;
            }
            if (y < start.Y - snapDown - 0.05f)
            {
                if (measure) timing.Rejection = "step-too-low";
                return false;
            }
            WorldPosition onGround = new WorldPosition(target.X, y, target.Z);
            bool clear = Math.Abs(y - start.Y) <= FlatTolerance &&
                         CertifiedStaticFlatSweep(world, start, onGround, capsule, slope);
            if (clear && measure) timing.CertifiedFlatSteps++;
            if (clear || CheckStanding(world, onGround, capsule, slope, ref timing, measure, false))
            {
                // Preserve the existing stairs and exposed-tread transition rules.
                if (y < start.Y - 0.01f &&
                    CheckStanding(world, target, capsule, slope, ref timing, measure, false) &&
                    HasTreadAhead(world, target, dx, dz, start.Y, capsule.Radius,
                        stepHeight, slope, y, ref timing, measure))
                {
                    next = target;
                    finalSupport = ground;
                    return true;
                }
                if (y > start.Y + 0.01f &&
                    !CanRaise(world, start, y, capsule, slope, ref timing, measure))
                {
                    if (measure) timing.Rejection = "rise-path-blocked";
                    return false;
                }
                next = onGround;
                finalSupport = ground;
                settledSupport = true;
                return true;
            }
            if (y < start.Y - 0.01f &&
                CheckStanding(world, target, capsule, slope, ref timing, measure, false))
            {
                next = target;
                finalSupport = ground;
                return true;
            }
            float planar = MathF.Sqrt(dx * dx + dz * dz);
            if (planar <= 0.000001f)
            {
                if (measure) timing.Rejection = "tiny-step";
                return false;
            }
            float leadX = target.X + dx / planar * capsule.Radius;
            float leadZ = target.Z + dz / planar * capsule.Radius;
            if (FindGround(world, new WorldPosition(leadX, start.Y, leadZ),
                    0f, up, down, slope, out ServerGroundHit ahead,
                    ref timing, measure, true) &&
                ahead.Position.Y > y + 0.01f &&
                ahead.Position.Y >= start.Y - 0.01f &&
                ahead.Position.Y <= start.Y + stepHeight + 0.05f)
            {
                WorldPosition raised = new WorldPosition(target.X, ahead.Position.Y, target.Z);
                if (CanRaise(world, start, raised.Y, capsule, slope, ref timing, measure) &&
                    CheckStanding(world, raised, capsule, slope, ref timing, measure, false))
                {
                    next = raised;
                    // The leading support sample is not the ground AT the destination.
                    // Preserve the motor's fallback grounding check on this stair case.
                    return true;
                }
            }
            if (measure) timing.Rejection = "no-clear-standing-pose";
            return false;
        }

        private static bool HasTreadAhead(ServerCollisionWorld world, WorldPosition target,
            float dx, float dz, float oldY, float radius, float stepHeight,
            float slope, float centerY, ref GroundedMoveTiming timing, bool measure)
        {
            float length = MathF.Sqrt(dx * dx + dz * dz);
            if (length <= 0.000001f) return false;
            float x = target.X + dx / length * radius;
            float z = target.Z + dz / length * radius;
            float probe = Math.Max(0.05f, stepHeight + 0.05f);
            return FindGround(world, new WorldPosition(x, oldY, z),
                       0f, probe, probe, slope, out ServerGroundHit hit,
                       ref timing, measure, true) &&
                   hit.Position.Y > centerY + 0.01f &&
                   hit.Position.Y >= oldY - 0.01f &&
                   hit.Position.Y <= oldY + stepHeight + 0.05f;
        }

        private static bool CanRaise(ServerCollisionWorld world, WorldPosition start,
            float raisedY, ServerCapsule capsule, float slope,
            ref GroundedMoveTiming timing, bool measure)
        {
            float rise = raisedY - start.Y;
            if (rise < 0f) return false;
            float unit = Math.Max(0.05f, capsule.Radius * 0.5f);
            int count = Math.Clamp((int)Math.Ceiling(rise / unit), 1, 12);
            for (int i = 1; i <= count; ++i)
            {
                float y = start.Y + rise * i / count;
                if (!CheckStanding(world, new WorldPosition(start.X, y, start.Z),
                        capsule, slope, ref timing, measure, true)) return false;
            }
            return true;
        }

        private static bool FindGround(ServerCollisionWorld world, WorldPosition p,
            float radius, float up, float down, float slope,
            out ServerGroundHit hit, ref GroundedMoveTiming timing,
            bool measure, bool ahead)
        {
            long began = measure ? Stopwatch.GetTimestamp() : 0L;
            bool ok = world.TryFindGround(p, radius, up, down, slope, out hit);
            if (measure)
            {
                double ms = Elapsed(began);
                if (ahead)
                {
                    timing.AheadCalls++;
                    if (!ok) timing.AheadMisses++;
                    timing.AheadMilliseconds += ms;
                    timing.MaxAheadMilliseconds = Math.Max(timing.MaxAheadMilliseconds, ms);
                }
                else
                {
                    timing.SupportCalls++;
                    if (!ok) timing.SupportMisses++;
                    timing.SupportMilliseconds += ms;
                    timing.MaxSupportMilliseconds = Math.Max(timing.MaxSupportMilliseconds, ms);
                }
            }
            return ok;
        }

        private static bool CheckStanding(ServerCollisionWorld world, WorldPosition p,
            ServerCapsule capsule, float slope,
            ref GroundedMoveTiming timing, bool measure, bool rise)
        {
            long began = measure ? Stopwatch.GetTimestamp() : 0L;
            bool ok = world.IsStandingCapsuleClear(p, capsule, slope);
            if (measure)
            {
                double ms = Elapsed(began);
                if (rise)
                {
                    timing.RiseCapsuleCalls++;
                    if (!ok) timing.RiseCapsuleRejects++;
                    timing.RiseCapsuleMilliseconds += ms;
                    timing.MaxRiseCapsuleMilliseconds = Math.Max(timing.MaxRiseCapsuleMilliseconds, ms);
                }
                else
                {
                    timing.CapsuleCalls++;
                    if (!ok) timing.CapsuleRejects++;
                    timing.CapsuleMilliseconds += ms;
                    timing.MaxCapsuleMilliseconds = Math.Max(timing.MaxCapsuleMilliseconds, ms);
                }
            }
            return ok;
        }
    }
}
