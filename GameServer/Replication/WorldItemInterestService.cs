using Game.GameServer.Runtime;
using Game.Shared.WorldItems;

namespace Game.GameServer.Replication;

/// <summary>
/// Server-authoritative AOI membership for static world-item entities.
///
/// Player AOI remains owned by WorldInterestService. This service deliberately uses the
/// same enter range, exit padding, XZ distance semantics, map/instance partitioning, and
/// spatial-cell sizing without modifying the already validated player observer graph.
/// World items are spatially indexed because querying the complete world-item collection
/// on every player movement would defeat the scalability boundary established by AOI.
///
/// The revision exposed here is observer-local. A client intentionally does not receive
/// world changes outside its AOI, so the backend/world revision cannot be used as a
/// contiguous client delta sequence. Each visible enter/leave/change increments only the
/// affected observer's view revision.
/// </summary>
internal sealed class WorldItemInterestService
{
    internal enum ChangeKind : byte
    {
        Added = 1,
        Removed = 2,
    }

    internal readonly struct Change
    {
        public ChangeKind Kind { get; }
        public ClientSession Observer { get; }
        public long ViewRevision { get; }
        public long ItemInstanceId { get; }
        public WorldItemView Item { get; }

        public Change(ChangeKind kind, ClientSession observer, long viewRevision, long itemInstanceId, WorldItemView item)
        {
            Kind = kind;
            Observer = observer;
            ViewRevision = viewRevision;
            ItemInstanceId = itemInstanceId;
            Item = item;
        }
    }

    private readonly struct CellKey : IEquatable<CellKey>
    {
        public readonly string MapId;
        public readonly string InstanceId;
        public readonly int X;
        public readonly int Z;

        public CellKey(string mapId, string instanceId, int x, int z)
        {
            MapId = mapId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            X = x;
            Z = z;
        }

        public bool Equals(CellKey other) =>
            X == other.X && Z == other.Z &&
            string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
            string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is CellKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(MapId);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(InstanceId);
                hash = (hash * 397) ^ X;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }
    }

    private sealed class SpatialCell
    {
        public readonly List<long> ItemIds = new List<long>(16);
        public readonly List<ClientSession> Observers = new List<ClientSession>(8);
    }

    private sealed class ItemEntry
    {
        public WorldItemView Item;
        public CellKey Cell;
        public int CellIndex = -1;
        public bool InGrid;
        // Reverse visibility membership makes removal/pickup O(actual observers) instead
        // of O(total CCU).
        public readonly HashSet<ClientSession> Observers = new HashSet<ClientSession>();

        public ItemEntry(WorldItemView item) => Item = item;
    }

    private sealed class ObserverState
    {
        public readonly ClientSession Session;
        public readonly HashSet<long> VisibleItems = new HashSet<long>();
        public string MapId = string.Empty;
        public string InstanceId = string.Empty;
        public float X;
        public float Z;
        public long Revision;
        public CellKey Cell;
        public int CellIndex = -1;
        public bool InGrid;

        public ObserverState(ClientSession session) => Session = session;
    }

    private readonly float _cellSize;
    private readonly float _enterRangeSquared;
    private readonly float _exitRangeSquared;
    private readonly int _queryCellRadius;
    private readonly Dictionary<CellKey, SpatialCell> _grid = new Dictionary<CellKey, SpatialCell>();
    private readonly Stack<SpatialCell> _cellPool = new Stack<SpatialCell>();
    private readonly Dictionary<long, ItemEntry> _items = new Dictionary<long, ItemEntry>();
    private readonly Dictionary<ClientSession, ObserverState> _observers = new Dictionary<ClientSession, ObserverState>();
    private readonly List<Change> _changes = new List<Change>(64);
    private readonly List<long> _itemScratch = new List<long>(128);
    private readonly List<ClientSession> _observerScratch = new List<ClientSession>(64);
    private long _authoritativeAddObserverCandidates;
    private long _authoritativeRemovalObservers;

    public long AuthoritativeAddObserverCandidates => _authoritativeAddObserverCandidates;
    public long AuthoritativeRemovalObservers => _authoritativeRemovalObservers;

    public void ResetDiagnostics()
    {
        _authoritativeAddObserverCandidates = 0;
        _authoritativeRemovalObservers = 0;
    }

    public int RegisteredObserverCount => _observers.Count;
    public int IndexedItemCount => _items.Count;
    public int GridCellCount => _grid.Count;

    public long VisibleReferenceCount
    {
        get
        {
            long count = 0;
            foreach (ObserverState observer in _observers.Values)
                count += observer.VisibleItems.Count;
            return count;
        }
    }

    public WorldItemInterestService(float enterRange, float exitPadding, float cellSize)
    {
        if (!float.IsFinite(enterRange) || enterRange <= 0f)
            throw new ArgumentOutOfRangeException(nameof(enterRange));
        if (!float.IsFinite(exitPadding) || exitPadding < 0f)
            throw new ArgumentOutOfRangeException(nameof(exitPadding));
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        _cellSize = cellSize;
        float exitRange = enterRange + exitPadding;
        _enterRangeSquared = enterRange * enterRange;
        _exitRangeSquared = exitRange * exitRange;
        _queryCellRadius = Math.Max(1, (int)Math.Ceiling(exitRange / cellSize));
    }

    public void Register(ClientSession session, WorldItemsSnapshot authoritativeSnapshot)
    {
        if (!IsEligible(session))
            return;

        Unregister(session);
        Seed(authoritativeSnapshot);

        var observer = new ObserverState(session);
        RefreshObserver(observer);
        _observers.Add(session, observer);
        AddObserverToGrid(observer);
        AddNearbyVisible(observer, emitChanges: false);
    }

    public void Unregister(ClientSession session)
    {
        if (session == null || !_observers.Remove(session, out ObserverState observer))
            return;

        _itemScratch.Clear();
        foreach (long itemId in observer.VisibleItems)
            _itemScratch.Add(itemId);
        for (int i = 0; i < _itemScratch.Count; ++i)
            if (_items.TryGetValue(_itemScratch[i], out ItemEntry entry))
                entry.Observers.Remove(session);
        observer.VisibleItems.Clear();
        RemoveObserverFromGrid(observer);
    }

    public bool NeedsPartitionRefresh(ClientSession session)
    {
        if (!_observers.TryGetValue(session, out ObserverState observer) || !IsEligible(session))
            return false;

        var location = session.Entity.Runtime.Location;
        return !string.Equals(observer.MapId, location.MapId ?? string.Empty, StringComparison.Ordinal) ||
               !string.Equals(observer.InstanceId, location.InstanceId ?? string.Empty, StringComparison.Ordinal);
    }

    public IReadOnlyList<Change> ReconcileMoved(ClientSession session, WorldItemsSnapshot partitionSnapshot = null)
    {
        _changes.Clear();
        if (!_observers.TryGetValue(session, out ObserverState observer) || !IsEligible(session))
            return _changes;

        string oldMap = observer.MapId;
        string oldInstance = observer.InstanceId;
        RefreshObserver(observer);
        MoveObserverGridIfNeeded(observer);
        bool partitionChanged =
            !string.Equals(oldMap, observer.MapId, StringComparison.Ordinal) ||
            !string.Equals(oldInstance, observer.InstanceId, StringComparison.Ordinal);

        if (partitionChanged && partitionSnapshot != null)
            Seed(partitionSnapshot);

        // Empty world-item catalogs are common on sparse/test maps. Refreshing observer
        // position is enough; do not walk the AOI cell neighborhood when no item can enter
        // or exit visibility.
        if (_items.Count == 0 && observer.VisibleItems.Count == 0)
            return _changes;

        ReconcileVisibleExits(observer);
        AddNearbyVisible(observer, emitChanges: true);
        return _changes;
    }

    public IReadOnlyList<Change> ApplyAuthoritativeChange(WorldItemChange change)
    {
        _changes.Clear();

        if (change.Kind == WorldItemChangeKind.Added && change.Item != null)
        {
            Upsert(change.Item);

            // Query only observer cells intersecting the item AOI. An item spawn/drop no
            // longer walks every connected player on the GameServer.
            int centerX = ToCellCoordinate(change.Item.positionX);
            int centerZ = ToCellCoordinate(change.Item.positionZ);
            for (int dz = -_queryCellRadius; dz <= _queryCellRadius; ++dz)
            {
                for (int dx = -_queryCellRadius; dx <= _queryCellRadius; ++dx)
                {
                    var key = new CellKey(
                        change.Item.mapId,
                        change.Item.instanceId,
                        centerX + dx,
                        centerZ + dz);
                    if (!_grid.TryGetValue(key, out SpatialCell cell))
                        continue;

                    List<ClientSession> candidates = cell.Observers;
                    for (int i = 0; i < candidates.Count; ++i)
                    {
                        _authoritativeAddObserverCandidates++;
                        ClientSession session = candidates[i];
                        if (!_observers.TryGetValue(session, out ObserverState observer) ||
                            !IsEligible(session) ||
                            observer.VisibleItems.Contains(change.ItemInstanceId) ||
                            DistanceSquared(observer.X, observer.Z, change.Item.positionX, change.Item.positionZ) > _enterRangeSquared)
                            continue;

                        AddVisible(observer, change.ItemInstanceId, change.Item, emitChange: true);
                    }
                }
            }
        }
        else if (change.Kind == WorldItemChangeKind.Removed)
        {
            if (_items.TryGetValue(change.ItemInstanceId, out ItemEntry entry))
            {
                _observerScratch.Clear();
                foreach (ClientSession observer in entry.Observers)
                    _observerScratch.Add(observer);
                _authoritativeRemovalObservers += _observerScratch.Count;

                for (int i = 0; i < _observerScratch.Count; ++i)
                {
                    ClientSession session = _observerScratch[i];
                    if (!_observers.TryGetValue(session, out ObserverState observer) ||
                        !observer.VisibleItems.Remove(change.ItemInstanceId))
                        continue;
                    entry.Observers.Remove(session);
                    if (IsEligible(session))
                        _changes.Add(new Change(
                            ChangeKind.Removed,
                            session,
                            NextRevision(observer),
                            change.ItemInstanceId,
                            null));
                }
            }
            RemoveItem(change.ItemInstanceId);
        }

        return _changes;
    }

    public WorldItemsSnapshot BuildSnapshot(ClientSession session, WorldItemsSnapshot authoritativeSnapshot)
    {
        if (authoritativeSnapshot == null)
            return null;

        if (!_observers.TryGetValue(session, out ObserverState observer))
        {
            Register(session, authoritativeSnapshot);
            if (!_observers.TryGetValue(session, out observer))
                return new WorldItemsSnapshot
                {
                    mapId = authoritativeSnapshot.mapId ?? string.Empty,
                    instanceId = authoritativeSnapshot.instanceId ?? string.Empty,
                    revision = 0,
                    items = Array.Empty<WorldItemView>(),
                };
        }
        else
        {
            Seed(authoritativeSnapshot);
        }

        _itemScratch.Clear();
        foreach (long itemId in observer.VisibleItems)
            _itemScratch.Add(itemId);
        _itemScratch.Sort();

        var visible = new List<WorldItemView>(_itemScratch.Count);
        for (int i = 0; i < _itemScratch.Count; ++i)
        {
            if (_items.TryGetValue(_itemScratch[i], out ItemEntry entry) &&
                SamePartition(observer, entry.Item.mapId, entry.Item.instanceId))
                visible.Add(entry.Item);
        }

        return new WorldItemsSnapshot
        {
            mapId = observer.MapId,
            instanceId = observer.InstanceId,
            revision = observer.Revision,
            items = visible.ToArray(),
        };
    }

    private void Seed(WorldItemsSnapshot snapshot)
    {
        if (snapshot?.items == null)
            return;
        for (int i = 0; i < snapshot.items.Length; ++i)
        {
            WorldItemView item = snapshot.items[i];
            if (item != null && item.itemInstanceId > 0)
                Upsert(item);
        }
    }

    private void ReconcileVisibleExits(ObserverState observer)
    {
        _itemScratch.Clear();
        foreach (long itemId in observer.VisibleItems)
            _itemScratch.Add(itemId);

        for (int i = 0; i < _itemScratch.Count; ++i)
        {
            long itemId = _itemScratch[i];
            if (_items.TryGetValue(itemId, out ItemEntry entry) &&
                SamePartition(observer, entry.Item.mapId, entry.Item.instanceId) &&
                DistanceSquared(observer.X, observer.Z, entry.Item.positionX, entry.Item.positionZ) <= _exitRangeSquared)
                continue;

            observer.VisibleItems.Remove(itemId);
            if (_items.TryGetValue(itemId, out ItemEntry removedEntry))
                removedEntry.Observers.Remove(observer.Session);
            _changes.Add(new Change(ChangeKind.Removed, observer.Session, NextRevision(observer), itemId, null));
        }
    }

    private void AddNearbyVisible(ObserverState observer, bool emitChanges)
    {
        int centerX = ToCellCoordinate(observer.X);
        int centerZ = ToCellCoordinate(observer.Z);
        for (int dz = -_queryCellRadius; dz <= _queryCellRadius; ++dz)
        {
            for (int dx = -_queryCellRadius; dx <= _queryCellRadius; ++dx)
            {
                var key = new CellKey(observer.MapId, observer.InstanceId, centerX + dx, centerZ + dz);
                if (!_grid.TryGetValue(key, out SpatialCell cell))
                    continue;

                List<long> ids = cell.ItemIds;
                for (int i = 0; i < ids.Count; ++i)
                {
                    long itemId = ids[i];
                    if (observer.VisibleItems.Contains(itemId) || !_items.TryGetValue(itemId, out ItemEntry entry))
                        continue;
                    WorldItemView item = entry.Item;
                    if (DistanceSquared(observer.X, observer.Z, item.positionX, item.positionZ) > _enterRangeSquared)
                        continue;

                    AddVisible(observer, itemId, item, emitChanges);
                }
            }
        }
    }

    private void AddVisible(ObserverState observer, long itemId, WorldItemView item, bool emitChange)
    {
        if (!observer.VisibleItems.Add(itemId))
            return;
        if (_items.TryGetValue(itemId, out ItemEntry entry))
            entry.Observers.Add(observer.Session);
        if (emitChange)
            _changes.Add(new Change(ChangeKind.Added, observer.Session, NextRevision(observer), itemId, item));
    }

    private void Upsert(WorldItemView item)
    {
        if (_items.TryGetValue(item.itemInstanceId, out ItemEntry entry))
        {
            CellKey desired = MakeCellKey(item.mapId, item.instanceId, item.positionX, item.positionZ);
            if (!entry.InGrid || !entry.Cell.Equals(desired))
            {
                RemoveFromGrid(entry, item.itemInstanceId);
                entry.Item = item;
                AddToGrid(entry, item.itemInstanceId);
            }
            else
            {
                entry.Item = item;
            }
            return;
        }

        entry = new ItemEntry(item);
        _items.Add(item.itemInstanceId, entry);
        AddToGrid(entry, item.itemInstanceId);
    }

    private void RemoveItem(long itemId)
    {
        if (!_items.Remove(itemId, out ItemEntry entry))
            return;
        entry.Observers.Clear();
        RemoveFromGrid(entry, itemId);
    }

    private void AddToGrid(ItemEntry entry, long itemId)
    {
        CellKey key = MakeCellKey(entry.Item.mapId, entry.Item.instanceId, entry.Item.positionX, entry.Item.positionZ);
        if (!_grid.TryGetValue(key, out SpatialCell cell))
        {
            cell = _cellPool.Count > 0 ? _cellPool.Pop() : new SpatialCell();
            _grid.Add(key, cell);
        }

        entry.Cell = key;
        entry.CellIndex = cell.ItemIds.Count;
        entry.InGrid = true;
        cell.ItemIds.Add(itemId);
    }

    private void RemoveFromGrid(ItemEntry entry, long itemId)
    {
        if (!entry.InGrid || !_grid.TryGetValue(entry.Cell, out SpatialCell cell))
        {
            entry.InGrid = false;
            entry.CellIndex = -1;
            return;
        }

        int index = entry.CellIndex;
        int lastIndex = cell.ItemIds.Count - 1;
        if (index < 0 || index > lastIndex || cell.ItemIds[index] != itemId)
            index = cell.ItemIds.IndexOf(itemId);

        if (index >= 0)
        {
            long movedId = cell.ItemIds[lastIndex];
            cell.ItemIds[index] = movedId;
            cell.ItemIds.RemoveAt(lastIndex);
            if (index != lastIndex && _items.TryGetValue(movedId, out ItemEntry moved))
                moved.CellIndex = index;
        }

        entry.InGrid = false;
        entry.CellIndex = -1;

        TryReleaseCell(entry.Cell, cell);
    }

    private void AddObserverToGrid(ObserverState observer)
    {
        CellKey key = MakeCellKey(observer.MapId, observer.InstanceId, observer.X, observer.Z);
        if (!_grid.TryGetValue(key, out SpatialCell cell))
        {
            cell = _cellPool.Count > 0 ? _cellPool.Pop() : new SpatialCell();
            _grid.Add(key, cell);
        }

        observer.Cell = key;
        observer.CellIndex = cell.Observers.Count;
        observer.InGrid = true;
        cell.Observers.Add(observer.Session);
    }

    private void MoveObserverGridIfNeeded(ObserverState observer)
    {
        CellKey desired = MakeCellKey(observer.MapId, observer.InstanceId, observer.X, observer.Z);
        if (observer.InGrid && observer.Cell.Equals(desired))
            return;
        RemoveObserverFromGrid(observer);
        AddObserverToGrid(observer);
    }

    private void RemoveObserverFromGrid(ObserverState observer)
    {
        if (!observer.InGrid || !_grid.TryGetValue(observer.Cell, out SpatialCell cell))
        {
            observer.InGrid = false;
            observer.CellIndex = -1;
            return;
        }

        int index = observer.CellIndex;
        int lastIndex = cell.Observers.Count - 1;
        if (index < 0 || index > lastIndex || !ReferenceEquals(cell.Observers[index], observer.Session))
            index = cell.Observers.IndexOf(observer.Session);

        if (index >= 0)
        {
            ClientSession movedSession = cell.Observers[lastIndex];
            cell.Observers[index] = movedSession;
            cell.Observers.RemoveAt(lastIndex);
            if (index != lastIndex && _observers.TryGetValue(movedSession, out ObserverState moved))
                moved.CellIndex = index;
        }

        CellKey oldCell = observer.Cell;
        observer.InGrid = false;
        observer.CellIndex = -1;
        TryReleaseCell(oldCell, cell);
    }

    private void TryReleaseCell(CellKey key, SpatialCell cell)
    {
        if (cell == null || cell.ItemIds.Count != 0 || cell.Observers.Count != 0)
            return;
        _grid.Remove(key);
        if (_cellPool.Count < 4096)
            _cellPool.Push(cell);
    }

    private void RefreshObserver(ObserverState observer)
    {
        var location = observer.Session.Entity.Runtime.Location;
        observer.MapId = location.MapId ?? string.Empty;
        observer.InstanceId = location.InstanceId ?? string.Empty;
        observer.X = observer.Session.Entity.X;
        observer.Z = observer.Session.Entity.Z;
    }

    private CellKey MakeCellKey(string mapId, string instanceId, float x, float z) =>
        new CellKey(mapId, instanceId, ToCellCoordinate(x), ToCellCoordinate(z));

    private int ToCellCoordinate(float value) => (int)Math.Floor(value / _cellSize);

    private static bool SamePartition(ObserverState observer, string mapId, string instanceId) =>
        string.Equals(observer.MapId, mapId ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(observer.InstanceId, instanceId ?? string.Empty, StringComparison.Ordinal);

    private static float DistanceSquared(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return dx * dx + dz * dz;
    }

    private static long NextRevision(ObserverState observer)
    {
        if (observer.Revision == long.MaxValue)
            throw new InvalidOperationException("world-item observer revision exhausted");
        return ++observer.Revision;
    }

    private static bool IsEligible(ClientSession session) =>
        session != null && session.Connected && session.Ready && session.Entity != null;
}
