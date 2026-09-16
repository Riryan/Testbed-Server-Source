using System;
using System.Collections.Generic;
using Game.Shared.Actors;
using Game.Shared.World;

namespace Game.Server.Application.Actors
{
    public sealed class AuthoritativeActorRuntime
    {
        public AuthoritativeActorHandle Handle { get; }
        public string ArchetypeId { get; }
        public string DisplayName { get; set; }
        public ActorFaction Faction { get; set; }
        public ActorWeightClass WeightClass { get; set; }
        public string MapId { get; set; }
        public string InstanceId { get; set; }
        public WorldPosition Position { get; set; }
        public WorldPosition LastSafePosition { get; set; }
        public float YawDegrees { get; set; }
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float VelocityZ { get; set; }
        public ActorMovementMode MovementMode { get; set; }
        public int HealthCurrent { get; set; }
        public int HealthMaximum { get; set; }
        public bool Alive { get; set; }
        public long Revision { get; private set; }

        public AuthoritativeActorRuntime(
            AuthoritativeActorHandle handle,
            string archetypeId,
            string displayName,
            ActorFaction faction,
            string mapId,
            string instanceId,
            WorldPosition position,
            float yawDegrees,
            int healthMaximum,
            ActorWeightClass weightClass = ActorWeightClass.Normal)
        {
            if (!handle.IsValid) throw new ArgumentException("Actor handle is invalid.", nameof(handle));
            Handle = handle;
            ArchetypeId = archetypeId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Faction = faction;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            Position = position;
            LastSafePosition = position;
            YawDegrees = NormalizeYaw(yawDegrees);
            HealthMaximum = Math.Max(1, healthMaximum);
            HealthCurrent = HealthMaximum;
            Alive = true;
            MovementMode = ActorMovementMode.Grounded;
            WeightClass = weightClass;
            Revision = 1;
        }

        public void Touch() => Revision = Revision == long.MaxValue ? 1 : Revision + 1;

        public AuthoritativeActorSnapshot Snapshot() => new AuthoritativeActorSnapshot
        {
            handle = Handle,
            archetypeId = ArchetypeId,
            displayName = DisplayName,
            faction = Faction,
            mapId = MapId,
            instanceId = InstanceId,
            positionX = Position.X,
            positionY = Position.Y,
            positionZ = Position.Z,
            yawDegrees = YawDegrees,
            velocityX = VelocityX,
            velocityY = VelocityY,
            velocityZ = VelocityZ,
            movementMode = MovementMode,
            weightClass = WeightClass,
            healthCurrent = HealthCurrent,
            healthMaximum = HealthMaximum,
            alive = Alive,
            revision = Revision,
        };

        private static float NormalizeYaw(float value)
        {
            float result = value % 360f;
            return result < 0f ? result + 360f : result;
        }
    }

    /// <summary>
    /// Plain-C# non-player actor registry. PlayerRuntime remains canonical for Players today;
    /// this registry establishes the common identity/spatial model for NPC/Monster/Population.
    /// </summary>
    public sealed class AuthoritativeActorRegistry
    {
        private const float SpatialCellSize = 32f;

        private readonly struct SpatialCellKey : IEquatable<SpatialCellKey>
        {
            public readonly string MapId;
            public readonly string InstanceId;
            public readonly int X;
            public readonly int Z;

            public SpatialCellKey(string mapId, string instanceId, int x, int z)
            {
                MapId = mapId ?? string.Empty;
                InstanceId = instanceId ?? string.Empty;
                X = x;
                Z = z;
            }

            public bool Equals(SpatialCellKey other) =>
                X == other.X && Z == other.Z &&
                string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
                string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is SpatialCellKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = StringComparer.Ordinal.GetHashCode(MapId);
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(InstanceId);
                    hash = (hash * 397) ^ X;
                    hash = (hash * 397) ^ Z;
                    return hash;
                }
            }
        }

        private readonly Dictionary<long, AuthoritativeActorRuntime> _actors = new Dictionary<long, AuthoritativeActorRuntime>();
        private readonly Dictionary<SpatialCellKey, List<AuthoritativeActorRuntime>> _spatial =
            new Dictionary<SpatialCellKey, List<AuthoritativeActorRuntime>>();
        private readonly Dictionary<long, SpatialCellKey> _actorCells = new Dictionary<long, SpatialCellKey>();
        private readonly HashSet<long> _spatiallySuspended = new HashSet<long>();
        private readonly Stack<List<AuthoritativeActorRuntime>> _spatialListPool = new Stack<List<AuthoritativeActorRuntime>>();
        private long _nextActorId = 1000000;
        private ushort _nextGeneration = 1;

        public event Action<AuthoritativeActorRuntime> Added;
        public event Action<AuthoritativeActorRuntime> Changed;
        public event Action<AuthoritativeActorHandle> Removed;
        public int Count => _actors.Count;

        public AuthoritativeActorRuntime Create(
            AuthoritativeActorKind kind,
            string archetypeId,
            string displayName,
            ActorFaction faction,
            string mapId,
            string instanceId,
            WorldPosition position,
            float yawDegrees,
            int healthMaximum = 100,
            ActorWeightClass weightClass = ActorWeightClass.Normal)
        {
            if (kind == AuthoritativeActorKind.None || kind == AuthoritativeActorKind.Player)
                throw new ArgumentOutOfRangeException(nameof(kind));
            long actorId = ++_nextActorId;
            ushort generation = NextGeneration();
            var runtime = new AuthoritativeActorRuntime(
                new AuthoritativeActorHandle(actorId, generation, kind),
                archetypeId, displayName, faction, mapId, instanceId, position, yawDegrees,
                healthMaximum, weightClass);
            _actors.Add(actorId, runtime);
            AddToSpatial(runtime);
            Added?.Invoke(runtime);
            return runtime;
        }

        public bool TryGet(long actorId, out AuthoritativeActorRuntime actor) => _actors.TryGetValue(actorId, out actor);

        public bool TryGet(AuthoritativeActorHandle handle, out AuthoritativeActorRuntime actor)
        {
            if (_actors.TryGetValue(handle.actorId, out actor) && actor.Handle.Equals(handle))
                return true;
            actor = null;
            return false;
        }

        public void PublishChanged(AuthoritativeActorRuntime actor)
        {
            if (actor == null || !_actors.TryGetValue(actor.Handle.actorId, out AuthoritativeActorRuntime current) || !ReferenceEquals(current, actor))
                return;
            if (!_spatiallySuspended.Contains(actor.Handle.actorId))
                MoveSpatialIfNeeded(actor);
            actor.Touch();
            Changed?.Invoke(actor);
        }

        /// <summary>
        /// Removes an actor from spatial queries without destroying its authoritative identity.
        /// Population uses this while an off-AOI actor is logically hibernating.
        /// </summary>
        public bool SuspendSpatial(AuthoritativeActorRuntime actor)
        {
            if (actor == null || !_actors.TryGetValue(actor.Handle.actorId, out AuthoritativeActorRuntime current) || !ReferenceEquals(current, actor))
                return false;
            if (!_spatiallySuspended.Add(actor.Handle.actorId))
                return true;
            RemoveFromSpatial(actor);
            return true;
        }

        /// <summary>Restores a previously suspended actor to authoritative spatial queries.</summary>
        public bool ResumeSpatial(AuthoritativeActorRuntime actor)
        {
            if (actor == null || !_actors.TryGetValue(actor.Handle.actorId, out AuthoritativeActorRuntime current) || !ReferenceEquals(current, actor))
                return false;
            if (!_spatiallySuspended.Remove(actor.Handle.actorId))
            {
                MoveSpatialIfNeeded(actor);
                return true;
            }
            AddToSpatial(actor);
            return true;
        }

        public bool IsSpatiallySuspended(AuthoritativeActorRuntime actor) =>
            actor != null && _spatiallySuspended.Contains(actor.Handle.actorId);

        public bool Remove(AuthoritativeActorHandle handle)
        {
            if (!TryGet(handle, out AuthoritativeActorRuntime actor))
                return false;
            RemoveFromSpatial(actor);
            _spatiallySuspended.Remove(handle.actorId);
            _actors.Remove(handle.actorId);
            Removed?.Invoke(handle);
            return true;
        }

        public List<AuthoritativeActorRuntime> QueryRadius(string mapId, string instanceId, WorldPosition center, float radius, List<AuthoritativeActorRuntime> destination = null)
        {
            destination ??= new List<AuthoritativeActorRuntime>();
            destination.Clear();
            float range = Math.Max(0f, radius);
            float rangeSq = range * range;
            int cellRadius = Math.Max(0, (int)Math.Ceiling(range / SpatialCellSize));
            int centerX = ToSpatialCell(center.X);
            int centerZ = ToSpatialCell(center.Z);
            string normalizedMap = mapId ?? string.Empty;
            string normalizedInstance = instanceId ?? string.Empty;

            for (int dz = -cellRadius; dz <= cellRadius; ++dz)
            {
                for (int dx = -cellRadius; dx <= cellRadius; ++dx)
                {
                    var key = new SpatialCellKey(normalizedMap, normalizedInstance, centerX + dx, centerZ + dz);
                    if (!_spatial.TryGetValue(key, out List<AuthoritativeActorRuntime> cell))
                        continue;

                    for (int i = 0; i < cell.Count; ++i)
                    {
                        AuthoritativeActorRuntime actor = cell[i];
                        float deltaX = actor.Position.X - center.X;
                        float deltaZ = actor.Position.Z - center.Z;
                        if ((deltaX * deltaX) + (deltaZ * deltaZ) <= rangeSq)
                            destination.Add(actor);
                    }
                }
            }
            return destination;
        }

        private void AddToSpatial(AuthoritativeActorRuntime actor)
        {
            SpatialCellKey key = MakeSpatialKey(actor);
            if (!_spatial.TryGetValue(key, out List<AuthoritativeActorRuntime> cell))
            {
                cell = _spatialListPool.Count > 0
                    ? _spatialListPool.Pop()
                    : new List<AuthoritativeActorRuntime>(8);
                _spatial.Add(key, cell);
            }
            cell.Add(actor);
            _actorCells[actor.Handle.actorId] = key;
        }

        private void MoveSpatialIfNeeded(AuthoritativeActorRuntime actor)
        {
            SpatialCellKey desired = MakeSpatialKey(actor);
            if (_actorCells.TryGetValue(actor.Handle.actorId, out SpatialCellKey current) && current.Equals(desired))
                return;
            RemoveFromSpatial(actor);
            AddToSpatial(actor);
        }

        private void RemoveFromSpatial(AuthoritativeActorRuntime actor)
        {
            if (actor == null || !_actorCells.Remove(actor.Handle.actorId, out SpatialCellKey key))
                return;
            if (!_spatial.TryGetValue(key, out List<AuthoritativeActorRuntime> cell))
                return;
            cell.Remove(actor);
            if (cell.Count == 0)
            {
                _spatial.Remove(key);
                if (_spatialListPool.Count < 2048)
                    _spatialListPool.Push(cell);
            }
        }

        private static SpatialCellKey MakeSpatialKey(AuthoritativeActorRuntime actor) =>
            new SpatialCellKey(
                actor.MapId,
                actor.InstanceId,
                ToSpatialCell(actor.Position.X),
                ToSpatialCell(actor.Position.Z));

        private static int ToSpatialCell(float value) => (int)Math.Floor(value / SpatialCellSize);

        public IEnumerable<AuthoritativeActorRuntime> All => _actors.Values;

        private ushort NextGeneration()
        {
            ushort value = _nextGeneration++;
            if (value == 0) value = _nextGeneration++;
            return value == 0 ? (ushort)1 : value;
        }
    }

    public static class ActorRelationshipRules
    {
        public static ActorRelationshipDisposition Evaluate(ActorFaction source, ActorFaction target)
        {
            if (source == target && source != ActorFaction.Neutral)
                return ActorRelationshipDisposition.Allied;
            if (source == ActorFaction.Police && (target == ActorFaction.Criminal || target == ActorFaction.Gang))
                return ActorRelationshipDisposition.Hostile;
            if ((source == ActorFaction.Hunter && target == ActorFaction.Vampire) ||
                (source == ActorFaction.Vampire && target == ActorFaction.Hunter))
                return ActorRelationshipDisposition.Hostile;
            if (source == ActorFaction.Monster || target == ActorFaction.Monster)
                return source == target ? ActorRelationshipDisposition.Allied : ActorRelationshipDisposition.Hostile;
            return ActorRelationshipDisposition.Neutral;
        }
    }
}
