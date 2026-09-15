using System;
using Game.Shared.Identity;

namespace Game.Server.Domain.Inventory
{
    /// <summary>
    /// Durable per-item firearm magazine state. This is instance state, not item-definition
    /// state: two pistols of the same definition may contain different ammunition/rounds.
    /// MagazineRevision is independent from the ordinary item Revision so shot decrements
    /// do not force inventory/equipment revision churn.
    /// </summary>
    public readonly struct ItemMagazineState
    {
        public ItemInstanceId ItemInstanceId { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public ItemMagazineState(
            ItemInstanceId itemInstanceId,
            string loadedAmmoDefinitionId,
            int loadedRounds,
            long magazineRevision)
        {
            if (!itemInstanceId.IsValid) throw new ArgumentException("Item instance id is invalid.", nameof(itemInstanceId));
            if (loadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(loadedRounds));
            if (magazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(magazineRevision));
            ItemInstanceId = itemInstanceId;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }
    }

    public sealed class ItemInstanceState
    {
        public ItemInstanceId ItemInstanceId { get; }
        public string DefinitionId { get; }
        public int Quantity { get; }
        public int Durability { get; }
        public long Revision { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public long MagazineRevision { get; }

        public ItemInstanceState(
            ItemInstanceId itemInstanceId,
            string definitionId,
            int quantity,
            int durability,
            long revision,
            string loadedAmmoDefinitionId = "",
            int loadedRounds = 0,
            long magazineRevision = 0)
        {
            if (!itemInstanceId.IsValid) throw new ArgumentException("Item instance id is invalid.", nameof(itemInstanceId));
            if (string.IsNullOrWhiteSpace(definitionId)) throw new ArgumentException("Definition id is required.", nameof(definitionId));
            if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
            if (durability < 0) throw new ArgumentOutOfRangeException(nameof(durability));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (loadedRounds < 0) throw new ArgumentOutOfRangeException(nameof(loadedRounds));
            if (magazineRevision < 0) throw new ArgumentOutOfRangeException(nameof(magazineRevision));

            ItemInstanceId = itemInstanceId;
            DefinitionId = definitionId;
            Quantity = quantity;
            Durability = durability;
            Revision = revision;
            LoadedAmmoDefinitionId = loadedRounds > 0 ? (loadedAmmoDefinitionId ?? string.Empty) : string.Empty;
            LoadedRounds = loadedRounds;
            MagazineRevision = magazineRevision;
        }

        public ItemInstanceState WithQuantity(int quantity) =>
            new ItemInstanceState(
                ItemInstanceId, DefinitionId, quantity, Durability, checked(Revision + 1),
                LoadedAmmoDefinitionId, LoadedRounds, MagazineRevision);

        public ItemInstanceState WithMagazine(
            string loadedAmmoDefinitionId,
            int loadedRounds,
            long magazineRevision) =>
            new ItemInstanceState(
                ItemInstanceId, DefinitionId, Quantity, Durability, Revision,
                loadedAmmoDefinitionId, loadedRounds, magazineRevision);

        public ItemInstanceState Copy() =>
            new ItemInstanceState(
                ItemInstanceId, DefinitionId, Quantity, Durability, Revision,
                LoadedAmmoDefinitionId, LoadedRounds, MagazineRevision);
    }
}
