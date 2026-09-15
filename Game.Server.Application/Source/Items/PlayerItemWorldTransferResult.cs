using Game.Server.Domain.WorldItems;
using Game.Shared.Protocol;

namespace Game.Server.Application.Items
{
    public readonly struct PlayerItemWorldTransferResult
    {
        public PlayerItemOperationResult ItemResult { get; }
        public long WorldRevision { get; }
        public WorldItemState WorldItem { get; }
        public string MapId { get; }
        public string InstanceId { get; }

        public PlayerItemWorldTransferResult(PlayerItemOperationResult itemResult, long worldRevision, WorldItemState worldItem, string mapId, string instanceId)
        {
            ItemResult = itemResult;
            WorldRevision = worldRevision;
            WorldItem = worldItem;
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
        }
    }
}
