using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Game.Server.Application.Scheduling;
using Game.Server.Application.Content;
using Game.Server.Application.Interactions;
using Game.Server.Application.Items;
using Game.Server.Domain.Players;
using Game.Server.Domain.WorldItems;
using Game.Shared.Content;
using Game.Shared.Interactions;
using Game.Shared.Protocol;
using Game.Shared.World;
using Game.Shared.WorldItems;

namespace Game.Server.Application.WorldItems
{
    /// <summary>
    /// Live authoritative world-item registry for transient ground drops. Ordinary player
    /// drops are runtime-only: they are never restored from persistence, decay after a
    /// configurable lifetime, and participate in oldest-first capacity cleanup. Once picked
    /// up, the item leaves this lifecycle entirely and inventory persistence owns it again.
    /// Housing placement is intentionally outside this transient service.
    /// </summary>
    public sealed class WorldItemService : IAsyncWorldInteractionActionHandler
    {
        private sealed class WorldState
        {
            public long Revision;
            public readonly Dictionary<long, WorldItemState> Items = new Dictionary<long, WorldItemState>();
            public readonly SemaphoreSlim OperationGate = new SemaphoreSlim(1, 1);
        }

        private sealed class TransientDrop
        {
            public long ItemId;
            public string WorldKey;
            public long Sequence;
            public double ExpiresAt;
            public bool GrantOnPickup;
        }

        private readonly object _gate = new object();
        private readonly GameplayContentCatalog _content;
        private readonly PlayerItemService _playerItems;
        private readonly Dictionary<string, WorldState> _worlds = new Dictionary<string, WorldState>(StringComparer.Ordinal);
        private readonly Dictionary<long, long> _reservations = new Dictionary<long, long>();
        private readonly Dictionary<long, TransientDrop> _transientDrops = new Dictionary<long, TransientDrop>();
        private readonly MinPriorityQueue<TransientDrop, double> _expiryQueue = new MinPriorityQueue<TransientDrop, double>();
        private readonly MinPriorityQueue<TransientDrop, long> _oldestQueue = new MinPriorityQueue<TransientDrop, long>();
        private long _nextReservation;
        private long _nextDropSequence;
        private double _droppedItemLifetimeSeconds = 300d;
        private int _maxTransientDroppedItems = 5000;
        private int _droppedItemCleanupTarget = 4500;

        public InteractionTargetKind TargetKind => InteractionTargetKind.NetworkWorldObject;
        public InteractionActionId ActionId => InteractionActionId.Loot;
        public int TransientDropCount { get { lock (_gate) return _transientDrops.Count; } }
        public event Action<WorldItemChange> Changed;

        public WorldItemService(GameplayContentCatalog content, PlayerItemService playerItems)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _playerItems = playerItems ?? throw new ArgumentNullException(nameof(playerItems));
        }

        public void ConfigureTransientPolicy(double lifetimeSeconds, int maximumDrops, int cleanupTarget)
        {
            if (double.IsNaN(lifetimeSeconds) || double.IsInfinity(lifetimeSeconds) || lifetimeSeconds < 1d)
                throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
            if (maximumDrops < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumDrops));
            if (cleanupTarget < 1 || cleanupTarget > maximumDrops)
                throw new ArgumentOutOfRangeException(nameof(cleanupTarget));

            lock (_gate)
            {
                _droppedItemLifetimeSeconds = lifetimeSeconds;
                _maxTransientDroppedItems = maximumDrops;
                _droppedItemCleanupTarget = cleanupTarget;
            }
        }

        public WorldItemsSnapshot GetSnapshot(string mapId, string instanceId)
        {
            lock (_gate)
            {
                if (!_worlds.TryGetValue(Key(mapId, instanceId), out WorldState world))
                    return new WorldItemsSnapshot { mapId = mapId ?? string.Empty, instanceId = instanceId ?? string.Empty, revision = 0, items = Array.Empty<WorldItemView>() };
                var items = new WorldItemView[world.Items.Count];
                int index = 0;
                foreach (WorldItemState item in world.Items.Values) items[index++] = BuildView(item);
                Array.Sort(items, (a, b) => a.itemInstanceId.CompareTo(b.itemInstanceId));
                return new WorldItemsSnapshot { mapId = mapId, instanceId = instanceId ?? string.Empty, revision = world.Revision, items = items };
            }
        }

        public bool TryGet(string mapId, string instanceId, long itemInstanceId, out WorldItemState item)
        {
            lock (_gate)
            {
                if (_worlds.TryGetValue(Key(mapId, instanceId), out WorldState world))
                    return world.Items.TryGetValue(itemInstanceId, out item);

                item = null;
                return false;
            }
        }

        /// <summary>
        /// Creates runtime-only overflow reward drops. These use the same AOI/decay/pickup
        /// lifecycle as ordinary player drops, but pickup grants a fresh persisted inventory
        /// item instead of attempting to persist the temporary ground identity.
        /// </summary>
        public int SpawnGeneratedRewardDrops(PlayerRuntime runtime, RewardItemDefinition[] rewards)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            rewards ??= Array.Empty<RewardItemDefinition>();
            if (rewards.Length == 0) return 0;

            var location = runtime.Location;
            if (string.IsNullOrWhiteSpace(location.MapId))
                throw new InvalidOperationException("player is not in a valid world");

            var prepared = new List<WorldItemState>();
            for (int i = 0; i < rewards.Length; ++i)
            {
                RewardItemDefinition reward = rewards[i];
                if (reward == null || reward.quantity < 1)
                    throw new InvalidOperationException("overflow reward is invalid");

                ushort dataId = reward.itemDataId;
                if (dataId == 0 &&
                    !string.IsNullOrWhiteSpace(reward.itemDefinitionId) &&
                    _content.TryGetItem(reward.itemDefinitionId, out ItemDefinition semantic))
                    dataId = semantic.dataId;
                if (dataId == 0 || !_content.TryGetItem(dataId, out ItemDefinition definition) || definition == null)
                    throw new InvalidOperationException("overflow reward item is not loaded");

                int remaining = reward.quantity;
                int maxStack = Math.Max(1, definition.maxStack);
                while (remaining > 0)
                {
                    int quantity = Math.Min(maxStack, remaining);
                    prepared.Add(new WorldItemState(
                        new Game.Shared.Identity.ItemInstanceId(1), // replaced under registry lock
                        definition.definitionId,
                        quantity,
                        Math.Max(0, definition.maxDurability),
                        0,
                        location.MapId,
                        location.InstanceId,
                        location.Position));
                    remaining -= quantity;
                }
            }

            var added = new List<WorldItemChange>(prepared.Count);
            List<WorldItemChange> capacityRemovals;
            lock (_gate)
            {
                WorldState world = GetOrCreate(location.MapId, location.InstanceId);
                double now = MonotonicSeconds();
                for (int i = 0; i < prepared.Count; ++i)
                {
                    WorldItemState prototype = prepared[i];
                    long id = TransientWorldItemIds.Allocate().Value;
                    var item = new WorldItemState(
                        new Game.Shared.Identity.ItemInstanceId(id),
                        prototype.DefinitionId,
                        prototype.Quantity,
                        prototype.Durability,
                        0,
                        prototype.MapId,
                        prototype.InstanceId,
                        prototype.Position);

                    world.Revision = NextRevision(world.Revision);
                    world.Items[id] = item;
                    RegisterTransientDropLocked(item, now, grantOnPickup: true);
                    added.Add(new WorldItemChange(
                        WorldItemChangeKind.Added,
                        item.MapId,
                        item.InstanceId,
                        world.Revision,
                        id,
                        BuildView(item)));
                }
                capacityRemovals = TrimToCapacityLocked();
            }

            for (int i = 0; i < added.Count; ++i)
                Changed?.Invoke(added[i]);
            PublishChanges(capacityRemovals);
            return added.Count;
        }

        public async Task<PlayerItemOperationResult> DropAsync(PlayerRuntime runtime, int inventoryIndex, int quantity, WorldPosition position, CancellationToken cancellationToken)
        {
            if (runtime == null) return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player runtime is unavailable");
            var location = runtime.Location;
            WorldState operationWorld;
            lock (_gate) operationWorld = GetOrCreate(location.MapId, location.InstanceId);

            await operationWorld.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentLocation = runtime.Location;
                if (!string.Equals(currentLocation.MapId, location.MapId, StringComparison.Ordinal) ||
                    !string.Equals(currentLocation.InstanceId, location.InstanceId, StringComparison.Ordinal))
                    return PlayerItemOperationResult.Failed(PlayerItemOperationStatus.CharacterUnavailable, "player changed worlds before the drop could commit");

                PlayerItemWorldTransferResult result = await _playerItems.DropAsync(runtime, inventoryIndex, quantity, location.MapId, location.InstanceId, position, cancellationToken).ConfigureAwait(false);
                if (!result.ItemResult.Success || result.WorldItem == null) return result.ItemResult;

                WorldItemChange added;
                List<WorldItemChange> capacityRemovals;
                lock (_gate)
                {
                    WorldState world = GetOrCreate(result.WorldItem.MapId, result.WorldItem.InstanceId);
                    world.Revision = NextRevision(world.Revision);
                    world.Items[result.WorldItem.ItemInstanceId.Value] = result.WorldItem;
                    RegisterTransientDropLocked(result.WorldItem, MonotonicSeconds());
                    added = new WorldItemChange(WorldItemChangeKind.Added, result.WorldItem.MapId, result.WorldItem.InstanceId, world.Revision, result.WorldItem.ItemInstanceId.Value, BuildView(result.WorldItem));
                    capacityRemovals = TrimToCapacityLocked();
                }

                Changed?.Invoke(added);
                PublishChanges(capacityRemovals);
                return result.ItemResult;
            }
            finally
            {
                operationWorld.OperationGate.Release();
            }
        }

        public async Task<InteractionResult> ExecuteAsync(WorldInteractionExecutionContext context, CancellationToken cancellationToken)
        {
            long itemId = context.Target.PrimaryId;
            WorldItemState item;
            WorldState operationWorld;
            long reservation;
            lock (_gate)
            {
                if (!_worlds.TryGetValue(Key(context.MapId, context.InstanceId), out operationWorld) || !operationWorld.Items.TryGetValue(itemId, out item))
                    return new InteractionResult(context.Sequence, context.ActionId, InteractionResultCode.TargetUnavailable, context.Target, "world item is unavailable");
                if (_reservations.ContainsKey(itemId))
                    return new InteractionResult(context.Sequence, context.ActionId, InteractionResultCode.SessionBusy, context.Target, "world item is already being picked up");
                reservation = ++_nextReservation;
                if (reservation <= 0) { _nextReservation = 1; reservation = 1; }
                _reservations[itemId] = reservation;
            }

            bool operationGateHeld = false;
            try
            {
                await operationWorld.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                operationGateHeld = true;
                lock (_gate)
                {
                    if (!operationWorld.Items.TryGetValue(itemId, out WorldItemState currentItem) || currentItem.Revision != item.Revision ||
                        !_reservations.TryGetValue(itemId, out long currentReservation) || currentReservation != reservation)
                        return new InteractionResult(context.Sequence, context.ActionId, InteractionResultCode.TargetUnavailable, context.Target, "world item changed before pickup could commit");
                    item = currentItem;
                }

                bool grantOnPickup;
                lock (_gate)
                    grantOnPickup = _transientDrops.TryGetValue(itemId, out TransientDrop transient) && transient.GrantOnPickup;

                PlayerItemOperationResult pickupResult;
                if (grantOnPickup)
                {
                    if (!_content.TryGetItem(item.DefinitionId, out ItemDefinition definition) || definition == null)
                    {
                        return new InteractionResult(context.Sequence, context.ActionId, InteractionResultCode.Rejected, context.Target, "world item content is unavailable");
                    }

                    pickupResult = await _playerItems.GrantBundleAsync(
                        context.Source,
                        new[]
                        {
                            new RewardItemDefinition
                            {
                                itemDataId = definition.dataId,
                                itemDefinitionId = definition.definitionId,
                                quantity = item.Quantity,
                            },
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    PlayerItemWorldTransferResult transfer = await _playerItems.PickupAsync(context.Source, item, cancellationToken).ConfigureAwait(false);
                    pickupResult = transfer.ItemResult;
                }

                if (!pickupResult.Success)
                {
                    InteractionResultCode code = pickupResult.Status == PlayerItemOperationStatus.InventoryFull ? InteractionResultCode.Rejected :
                        pickupResult.Status == PlayerItemOperationStatus.StaleState ? InteractionResultCode.TargetUnavailable : InteractionResultCode.Rejected;
                    return new InteractionResult(context.Sequence, context.ActionId, code, context.Target, pickupResult.Error);
                }

                WorldItemChange change;
                lock (_gate)
                {
                    WorldState world = GetOrCreate(item.MapId, item.InstanceId);
                    world.Revision = NextRevision(world.Revision);
                    world.Items.Remove(itemId);
                    _transientDrops.Remove(itemId); // pickup ends ground-item decay immediately
                    _reservations.Remove(itemId);
                    change = new WorldItemChange(WorldItemChangeKind.Removed, item.MapId, item.InstanceId, world.Revision, itemId, null);
                }
                Changed?.Invoke(change);
                return new InteractionResult(context.Sequence, context.ActionId, InteractionResultCode.Success, context.Target, $"Picked up {item.Quantity} {DisplayName(item.DefinitionId)}.");
            }
            finally
            {
                if (operationGateHeld) operationWorld.OperationGate.Release();
                lock (_gate)
                    if (_reservations.TryGetValue(itemId, out long current) && current == reservation) _reservations.Remove(itemId);
            }
        }

        /// <summary>
        /// Bounded server-side expiry work. The supplied budget caps due queue entries examined,
        /// including stale tombstones, so an idle tick cannot inherit unbounded historical work.
        /// Queue bookkeeping is entirely local; the only client-visible traffic is the existing
        /// authoritative Removed change when a due item actually leaves the world.
        /// </summary>
        public int ProcessTransientDecay(int maximumRemovals = 128)
        {
            if (maximumRemovals < 1)
                return 0;

            double now = MonotonicSeconds();
            List<WorldItemChange> changes = null;
            int removed = 0;
            int examined = 0;
            lock (_gate)
            {
                // Empty/not-due is the overwhelmingly common path. Do not allocate a
                // change list merely because the simulation clock fired.
                if (!_expiryQueue.TryPeek(out _, out double nextDueAt) || nextDueAt > now)
                    return 0;

                // Bound queue work, not only successful removals. Pickups/re-drops/capacity
                // cleanup intentionally leave stale priority-queue entries behind; counting
                // only removals lets one later idle tick drain an arbitrarily large stale run.
                while (examined < maximumRemovals &&
                       _expiryQueue.TryPeek(out TransientDrop queued, out double dueAt) &&
                       dueAt <= now)
                {
                    examined++;
                    _expiryQueue.Dequeue();
                    if (!IsCurrentTransientLocked(queued))
                        continue;

                    if (_reservations.ContainsKey(queued.ItemId))
                    {
                        // A pickup already owns the item. Retry expiry shortly rather than
                        // racing the inventory transaction.
                        _expiryQueue.Enqueue(queued, now + 1d);
                        continue;
                    }

                    if (RemoveTransientLocked(queued.ItemId, out WorldItemChange change))
                    {
                        changes ??= new List<WorldItemChange>(Math.Min(maximumRemovals, 32));
                        changes.Add(change);
                        removed++;
                    }
                }
            }

            PublishChanges(changes);
            return removed;
        }

        private void RegisterTransientDropLocked(WorldItemState item, double now, bool grantOnPickup = false)
        {
            long sequence = _nextDropSequence == long.MaxValue ? 1 : _nextDropSequence + 1;
            _nextDropSequence = sequence;
            var state = new TransientDrop
            {
                ItemId = item.ItemInstanceId.Value,
                WorldKey = Key(item.MapId, item.InstanceId),
                Sequence = sequence,
                ExpiresAt = now + _droppedItemLifetimeSeconds,
                GrantOnPickup = grantOnPickup,
            };
            _transientDrops[state.ItemId] = state;
            _expiryQueue.Enqueue(state, state.ExpiresAt);
            _oldestQueue.Enqueue(state, state.Sequence);
        }

        private List<WorldItemChange> TrimToCapacityLocked()
        {
            if (_transientDrops.Count <= _maxTransientDroppedItems)
                return null;

            var changes = new List<WorldItemChange>();
            var reserved = new List<TransientDrop>();
            while (_transientDrops.Count > _droppedItemCleanupTarget && _oldestQueue.TryDequeue(out TransientDrop queued, out _))
            {
                if (!IsCurrentTransientLocked(queued))
                    continue;
                if (_reservations.ContainsKey(queued.ItemId))
                {
                    reserved.Add(queued);
                    continue;
                }
                if (RemoveTransientLocked(queued.ItemId, out WorldItemChange change))
                    changes.Add(change);
            }

            for (int i = 0; i < reserved.Count; ++i)
                _oldestQueue.Enqueue(reserved[i], reserved[i].Sequence);
            return changes;
        }

        private bool IsCurrentTransientLocked(TransientDrop queued) =>
            queued != null &&
            _transientDrops.TryGetValue(queued.ItemId, out TransientDrop current) &&
            ReferenceEquals(current, queued);

        private bool RemoveTransientLocked(long itemId, out WorldItemChange change)
        {
            change = default;
            if (!_transientDrops.TryGetValue(itemId, out TransientDrop drop))
                return false;
            _transientDrops.Remove(itemId);

            if (!_worlds.TryGetValue(drop.WorldKey, out WorldState world) ||
                !world.Items.TryGetValue(itemId, out WorldItemState item))
                return false;

            world.Items.Remove(itemId);
            world.Revision = NextRevision(world.Revision);
            _reservations.Remove(itemId);
            change = new WorldItemChange(WorldItemChangeKind.Removed, item.MapId, item.InstanceId, world.Revision, itemId, null);
            return true;
        }

        private void PublishChanges(List<WorldItemChange> changes)
        {
            if (changes == null || changes.Count == 0 || Changed == null)
                return;
            for (int i = 0; i < changes.Count; ++i)
                Changed.Invoke(changes[i]);
        }

        private WorldState GetOrCreate(string mapId, string instanceId)
        {
            string key = Key(mapId, instanceId);
            if (!_worlds.TryGetValue(key, out WorldState world)) _worlds[key] = world = new WorldState();
            return world;
        }

        private WorldItemView BuildView(WorldItemState item)
        {
            _content.TryGetItem(item.DefinitionId, out ItemDefinition definition);
            return new WorldItemView
            {
                itemInstanceId = item.ItemInstanceId.Value,
                itemRevision = item.Revision,
                itemDataId = definition?.dataId ?? 0,
                definitionId = item.DefinitionId,
                displayName = definition?.displayName ?? item.DefinitionId, quantity = item.Quantity, durability = item.Durability,
                maxDurability = definition?.maxDurability ?? 0, unitWeight = definition?.weight ?? 0f,
                mapId = item.MapId, instanceId = item.InstanceId,
                positionX = item.Position.X, positionY = item.Position.Y, positionZ = item.Position.Z,
            };
        }

        private string DisplayName(string definitionId) => _content.TryGetItem(definitionId, out ItemDefinition definition) ? definition.displayName : definitionId;
        private static long NextRevision(long current) => current == long.MaxValue ? 1 : current + 1;
        private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private static string Key(string mapId, string instanceId) => (mapId ?? string.Empty) + "\u001f" + (instanceId ?? string.Empty);
    }
}
