using System;

namespace Game.Shared.WorldItems
{
    public enum WorldItemChangeKind : byte
    {
        None = 0,
        Added = 1,
        Removed = 2,
    }

    [Serializable]
    public sealed class WorldItemView
    {
        public long itemInstanceId;
        public long itemRevision;
        public ushort itemDataId;
        public string definitionId;
        public string displayName;
        public int quantity;
        public int durability;
        public int maxDurability;
        public float unitWeight;
        public string mapId;
        public string instanceId;
        public float positionX;
        public float positionY;
        public float positionZ;
    }

    [Serializable]
    public sealed class WorldItemsSnapshot
    {
        public string mapId;
        public string instanceId;
        public long revision;
        public WorldItemView[] items;
    }

    public readonly struct WorldItemChange
    {
        public WorldItemChangeKind Kind { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public long WorldRevision { get; }
        public long ItemInstanceId { get; }
        public WorldItemView Item { get; }

        public WorldItemChange(WorldItemChangeKind kind, string mapId, string instanceId, long worldRevision, long itemInstanceId, WorldItemView item)
        {
            Kind = kind;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            WorldRevision = worldRevision;
            ItemInstanceId = itemInstanceId;
            Item = item;
        }
    }
}
