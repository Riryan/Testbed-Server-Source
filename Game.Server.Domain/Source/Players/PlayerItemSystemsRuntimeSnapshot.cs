using Game.Server.Domain.Equipment;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Stats;

namespace Game.Server.Domain.Players
{
    public sealed class PlayerItemSystemsRuntimeSnapshot
    {
        public InventoryState Inventory { get; }
        public EquipmentState Equipment { get; }
        public StatsState Stats { get; }

        public PlayerItemSystemsRuntimeSnapshot(InventoryState inventory, EquipmentState equipment, StatsState stats)
        {
            Inventory = inventory;
            Equipment = equipment;
            Stats = stats;
        }
    }
}
