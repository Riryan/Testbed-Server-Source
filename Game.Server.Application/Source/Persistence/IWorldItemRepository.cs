using System;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Domain.WorldItems;

namespace Game.Server.Application.Persistence
{
    public interface IWorldItemRepository
    {
        Task<WorldItemPersistenceWorld[]> LoadAllAsync(CancellationToken cancellationToken);
    }

    public sealed class WorldItemPersistenceWorld
    {
        public string MapId { get; }
        public string InstanceId { get; }
        public long Revision { get; }
        public WorldItemState[] Items { get; }

        public WorldItemPersistenceWorld(string mapId, string instanceId, long revision, WorldItemState[] items)
        {
            if (string.IsNullOrWhiteSpace(mapId)) throw new ArgumentException("MapId is required.", nameof(mapId));
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            MapId = mapId;
            InstanceId = instanceId ?? string.Empty;
            Revision = revision;
            Items = items ?? Array.Empty<WorldItemState>();
        }
    }
}
