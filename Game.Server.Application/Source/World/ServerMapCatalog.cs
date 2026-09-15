using System;
using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Read-only catalog of baked authoritative maps. Map data is supplied by the standalone
    /// host; this assembly deliberately owns no filesystem/JSON dependency.
    /// </summary>
    public sealed class ServerMapCatalog
    {
        private readonly struct MapKey : IEquatable<MapKey>
        {
            public readonly string MapId;
            public readonly string InstanceId;

            public MapKey(string mapId, string instanceId)
            {
                MapId = ServerMapId.Normalize(mapId);
                InstanceId = (instanceId ?? string.Empty).Trim();
            }

            public bool Equals(MapKey other) =>
                string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
                string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is MapKey other && Equals(other);
            public override int GetHashCode() => unchecked((StringComparer.Ordinal.GetHashCode(MapId) * 397) ^ StringComparer.Ordinal.GetHashCode(InstanceId));
        }

        private readonly Dictionary<MapKey, ServerMapSnapshot> _snapshots = new Dictionary<MapKey, ServerMapSnapshot>();
        private readonly Dictionary<MapKey, ServerCollisionWorld> _collisionWorlds = new Dictionary<MapKey, ServerCollisionWorld>();

        public int Count => _snapshots.Count;
        public IEnumerable<ServerMapSnapshot> Snapshots => _snapshots.Values;

        public ServerMapCatalog(IEnumerable<ServerMapSnapshot> snapshots, float collisionCellSize = 16f)
        {
            if (snapshots == null)
                return;

            foreach (ServerMapSnapshot snapshot in snapshots)
            {
                if (snapshot == null || snapshot.formatVersion != ServerMapFormat.Version || string.IsNullOrWhiteSpace(snapshot.mapId))
                    continue;

                string mapId = ServerMapId.Normalize(snapshot.mapId);
                string instanceId = (snapshot.instanceId ?? string.Empty).Trim();
                if (mapId.Length == 0)
                    continue;
                var key = new MapKey(mapId, instanceId);
                if (_snapshots.ContainsKey(key))
                    throw new InvalidOperationException($"Duplicate server map data for '{mapId}' instance '{instanceId}'.");

                snapshot.mapId = mapId;
                snapshot.instanceId = instanceId;
                _snapshots.Add(key, snapshot);
                _collisionWorlds.Add(key, new ServerCollisionWorld(snapshot, collisionCellSize));
            }
        }

        public bool TryGet(string mapId, string instanceId, out ServerMapSnapshot snapshot) =>
            _snapshots.TryGetValue(new MapKey(mapId, instanceId), out snapshot);

        public bool TryGetCollisionWorld(string mapId, string instanceId, out ServerCollisionWorld world) =>
            _collisionWorlds.TryGetValue(new MapKey(mapId, instanceId), out world);
    }
}
