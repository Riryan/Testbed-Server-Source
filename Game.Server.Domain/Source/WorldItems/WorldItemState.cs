using System;
using Game.Shared.Identity;
using Game.Shared.World;

namespace Game.Server.Domain.WorldItems
{
    public sealed class WorldItemState
    {
        public ItemInstanceId ItemInstanceId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public int Durability { get; }
        public long Revision { get; }
        public string MapId { get; }
        public string InstanceId { get; }
        public WorldPosition Position { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public WorldItemState(
            ItemInstanceId itemInstanceId,
            string definitionId,
            int quantity,
            int durability,
            long revision,
            string mapId,
            string instanceId,
            WorldPosition position,
            string loadedAmmoDefinitionId = "",
            int loadedRounds = 0,
            long magazineRevision = 0)
        {
            if (!itemInstanceId.IsValid) throw new ArgumentException("ItemInstanceId is invalid.", nameof(itemInstanceId));
            if (string.IsNullOrWhiteSpace(definitionId)) throw new ArgumentException("DefinitionId is required.", nameof(definitionId));
            if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (durability < 0) throw new ArgumentOutOfRangeException(nameof(durability));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (string.IsNullOrWhiteSpace(mapId)) throw new ArgumentException("MapId is required.", nameof(mapId));
            if (loadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(loadedRounds));
            if (magazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(magazineRevision));

            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Durability = durability;
            Revision = revision;
            MapId = mapId;
            InstanceId = instanceId ?? string.Empty;
            Position = position;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }
    }
}
