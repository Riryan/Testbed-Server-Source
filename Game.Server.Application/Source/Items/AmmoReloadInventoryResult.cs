using Game.Shared.Protocol;

namespace Game.Server.Application.Items
{
    public readonly struct AmmoReloadInventoryResult
    {
        public bool Success { get; }
        public string AmmoDefinitionId { get; }
        public int ConsumedRounds { get; }
        public PlayerItemOperationResult Items { get; }

        public AmmoReloadInventoryResult(
            bool success,
            string ammoDefinitionId,
            int consumedRounds,
            PlayerItemOperationResult items)
        {
            Success = success;
            AmmoDefinitionId = ammoDefinitionId ?? string.Empty;
            ConsumedRounds = consumedRounds;
            Items = items;
        }

        public static AmmoReloadInventoryResult Failed(PlayerItemOperationResult items) =>
            new AmmoReloadInventoryResult(false, string.Empty, 0, items);
    }
}
