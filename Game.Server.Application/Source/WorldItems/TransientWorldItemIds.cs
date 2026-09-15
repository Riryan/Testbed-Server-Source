using System;
using System.Threading;
using Game.Shared.Backend;
using Game.Shared.Identity;

namespace Game.Server.Application.WorldItems
{
    /// <summary>
    /// Process-local identity source for runtime-only ground items. These ids are never
    /// persisted as durable inventory identity. A GameServer restart intentionally clears
    /// the transient world-item lifecycle, so the allocator may restart with the process.
    /// </summary>
    public static class TransientWorldItemIds
    {
        private static long _next = long.MaxValue;

        public static ItemInstanceId Allocate()
        {
            long value = Interlocked.Decrement(ref _next) + 1;
            if (value < BackendServiceContracts.TransientWorldItemIdFloor)
                throw new InvalidOperationException("Transient world item id space is exhausted.");
            return new ItemInstanceId(value);
        }

        public static bool IsTransient(long value) =>
            value >= BackendServiceContracts.TransientWorldItemIdFloor;
    }
}
