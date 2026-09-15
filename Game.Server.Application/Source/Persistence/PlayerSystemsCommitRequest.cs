using System;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Shared.Identity;

namespace Game.Server.Application.Persistence
{
    public sealed class PlayerSystemsCommitRequest
    {
        public AccountId AccountId { get; }
        public CharacterId CharacterId { get; }
        public long ExpectedInventoryRevision { get; }
        public long ExpectedEquipmentRevision { get; }
        public InventoryState Inventory { get; }
        public EquipmentState Equipment { get; }

        public PlayerSystemsCommitRequest(AccountId accountId, CharacterId characterId, long expectedInventoryRevision, long expectedEquipmentRevision, InventoryState inventory, EquipmentState equipment)
        {
            AccountId = accountId; CharacterId = characterId; ExpectedInventoryRevision = expectedInventoryRevision; ExpectedEquipmentRevision = expectedEquipmentRevision;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            Equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        }
    }

    public readonly struct PlayerSystemsCommitResult
    {
        public bool Accepted { get; }
        public bool Stale { get; }
        public long StoredInventoryRevision { get; }
        public long StoredEquipmentRevision { get; }
        public string Error { get; }

        public PlayerSystemsCommitResult(bool accepted, bool stale, long storedInventoryRevision, long storedEquipmentRevision, string error)
        {
            Accepted = accepted; Stale = stale; StoredInventoryRevision = storedInventoryRevision; StoredEquipmentRevision = storedEquipmentRevision; Error = error ?? string.Empty;
        }
    }
}
