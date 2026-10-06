using System;
using Game.Server.Application.Content;
using Game.Server.Application.Persistence;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Server.Domain.Stats;
using Game.Server.Application.Items;
using Game.Server.Application.Progression;
using Game.Server.Application.Resources;
using Game.Shared.Identity;

namespace Game.Server.Application.Characters
{
    public sealed class CharacterRuntimeFactory
    {
        private readonly GameplayContentCatalog _content;

        public CharacterRuntimeFactory() { }
        public CharacterRuntimeFactory(GameplayContentCatalog content) => _content = content;

        public PlayerRuntime Create(CharacterPersistenceRecord record, PlayerSessionId sessionId)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            var runtime = new PlayerRuntime(
                record.AccountId, record.CharacterId, sessionId,
                new CharacterState(record.Name), record.Location, record.Revision, record.Appearance, record.PresentationPreferences);

            // Progression must exist before resources are initialized because visible
            // maximums are derived from the character's hidden skill-driven attributes.
            runtime.InitializeProgressionState(record.Progression);

            StatsState stats = StatsState.DefaultCharacter();
            if (record.PlayerSystems != null)
            {
                InventoryState inventory = BuildInventory(record.PlayerSystems);
                EquipmentState equipment = BuildEquipment(record.PlayerSystems);
                StatsState equipmentStats = _content == null
                    ? StatsState.DefaultCharacter()
                    : EquipmentStatCalculator.Calculate(_content, equipment);
                stats = _content == null
                    ? equipmentStats
                    : SkillDerivedStatCalculator.Apply(
                        _content,
                        runtime.CaptureProgressionState(),
                        equipmentStats);
                runtime.InitializePlayerItemSystems(inventory, equipment, stats);
            }
            else if (_content != null)
            {
                stats = SkillDerivedStatCalculator.Apply(
                    _content,
                    runtime.CaptureProgressionState(),
                    stats);
            }

            if (_content != null)
            {
                var resources = new CharacterResourceService(_content).CreateInitialState(
                    stats,
                    record.ResourceRevision,
                    record.Resources);
                runtime.InitializeCharacterResources(resources);
            }
            return runtime;
        }

        private static InventoryState BuildInventory(PlayerSystemsPersistenceRecord record)
        {
            var slots = new ItemInstanceState[record.InventoryCapacity];
            for (int i = 0; i < record.InventoryItems.Length; ++i)
            {
                PersistedInventoryItem item = record.InventoryItems[i];
                if (item == null || item.SlotIndex < 0 || item.SlotIndex >= slots.Length || slots[item.SlotIndex] != null)
                    throw new InvalidOperationException("Persisted inventory contains an invalid slot.");
                slots[item.SlotIndex] = new ItemInstanceState(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    item.Quantity,
                    item.Durability,
                    item.Revision,
                    item.LoadedAmmoDefinitionId,
                    item.LoadedRounds,
                    item.MagazineRevision);
            }
            return new InventoryState(record.InventoryCapacity, record.InventoryRevision, slots);
        }

        private static EquipmentState BuildEquipment(PlayerSystemsPersistenceRecord record)
        {
            var items = new EquippedItemState[record.EquipmentItems.Length];
            for (int i = 0; i < items.Length; ++i)
            {
                PersistedEquipmentItem item = record.EquipmentItems[i];
                items[i] = new EquippedItemState(item.SlotId, new ItemInstanceState(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    item.Quantity,
                    item.Durability,
                    item.Revision,
                    item.LoadedAmmoDefinitionId,
                    item.LoadedRounds,
                    item.MagazineRevision));
            }
            return new EquipmentState(record.EquipmentRevision, items);
        }


    }
}
