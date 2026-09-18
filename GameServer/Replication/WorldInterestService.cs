using Game.GameServer.Runtime;

namespace Game.GameServer.Replication;

/// <summary>
/// Server-authoritative player interest graph backed by an XZ spatial hash.
///
/// The graph is directed (observer -> target) even though the current player visibility
/// policy is symmetric. Keeping directed edges allows future stealth, spectator, party,
/// NPC, and scripted visibility rules without replacing the replication foundation.
///
/// This service performs no networking. It only owns spatial membership and visibility
/// edges. GameServerHost applies the returned add/remove deltas using the existing wire
/// protocol.
/// </summary>
internal sealed class WorldInterestService
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
        public ClientSession Target { get; }
        public uint TargetObjectId { get; }

        public Change(ChangeKind kind, ClientSession observer, ClientSession target, uint targetObjectId)
        {
            Kind = kind;
            Observer = observer;
            Target = target;
            TargetObjectId = targetObjectId;
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
            X == other.X &&
            Z == other.Z &&
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

    private struct EdgeState
    {
        public double NextSnapshotAt;
        public bool Privileged;
    }

    private sealed class Entry
    {
        public readonly ClientSession Session;
        public readonly Dictionary<ClientSession, EdgeState> VisibleTargets = new Dictionary<ClientSession, EdgeState>(32);
        public readonly HashSet<ClientSession> Observers = new HashSet<ClientSession>();
        public string MapId = string.Empty;
        public string InstanceId = string.Empty;
        public float X;
        public float Z;
        public CellKey Cell;
        public int CellIndex = -1;
        public bool InGrid;
        public byte AdditionScanPhase;
        public int AdditionCellOffset;
        public int AdditionSessionIndex;
        public bool AdditionScanQueued;
        public bool AdditionScanDirty;

        public Entry(ClientSession session) => Session = session;
    }

    private sealed class SpatialCell
    {
        public readonly List<ClientSession> Sessions = new List<ClientSession>(8);
    }

    private readonly float _cellSize;
    private readonly float _enterRangeSquared;
    private readonly float _exitRangeSquared;
    private readonly int _queryCellRadius;
    private readonly Dictionary<ClientSession, Entry> _entries = new Dictionary<ClientSession, Entry>();
    private readonly Dictionary<CellKey, SpatialCell> _grid = new Dictionary<CellKey, SpatialCell>();
    private readonly Stack<SpatialCell> _cellPool = new Stack<SpatialCell>();
    private readonly List<Change> _changes = new List<Change>(128);
    private readonly List<ClientSession> _edgeScratch = new List<ClientSession>(128);
    private readonly List<ClientSession> _privilegedFollowObserverScratch = new List<ClientSession>(32);
    private readonly Queue<Entry> _pendingAdditionScans = new Queue<Entry>();
    private readonly Dictionary<ClientSession, ClientSession> _privilegedFollowTargets =
        new Dictionary<ClientSession, ClientSession>();
    private readonly Func<ClientSession, ClientSession, bool> _visibilityPolicy;
    private long _observerEdgeCount;

    public int RegisteredCount => _entries.Count;
    public int GridCellCount => _grid.Count;
    public long ObserverEdgeCount => _observerEdgeCount;
    public int PendingAdditionScanCount => _pendingAdditionScans.Count;

    public WorldInterestService(
        float enterRange,
        float exitPadding,
        float cellSize,
        Func<ClientSession, ClientSession, bool> visibilityPolicy = null)
    {
        if (!float.IsFinite(enterRange) || enterRange <= 0f)
            throw new ArgumentOutOfRangeException(nameof(enterRange));
        if (!float.IsFinite(exitPadding) || exitPadding < 0f)
            throw new ArgumentOutOfRangeException(nameof(exitPadding));
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        _visibilityPolicy = visibilityPolicy;
        _cellSize = cellSize;
        float exitRange = enterRange + exitPadding;
        _enterRangeSquared = enterRange * enterRange;
        _exitRangeSquared = exitRange * exitRange;
        _queryCellRadius = Math.Max(1, (int)Math.Ceiling(exitRange / cellSize));
    }

    /// <summary>
    /// Registers one ready player as both an observer and a target. The returned list is
    /// reused internally and must be consumed before the next service mutation.
    /// </summary>
    public IReadOnlyList<Change> Register(ClientSession session)
    {
        _changes.Clear();
        if (!IsInterestEligible(session) || _entries.ContainsKey(session))
            return _changes;

        var entry = new Entry(session);
        RefreshPosition(entry);
        _entries.Add(session, entry);
        AddToGrid(entry);

        // Owner/self visibility is mandatory and preserves the current spawn contract.
        AddEdge(session, session);

        // The new player is both an observer and a target. With only one registered
        // player the mandatory self edge is already complete, so there is no addition
        // work to queue. The second+ registration scans nearby candidates once and
        // evaluates both directed visibility edges from that shared spatial result.
        if (_entries.Count > 1)
            QueueAdditionScan(entry);
        return _changes;
    }

    /// <summary>
    /// Reconciles both sides of a moved player's interest relationships. Updating both
    /// observer and target views is required so movement against stationary players cannot
    /// leave stale or missing edges.
    /// </summary>
    public IReadOnlyList<Change> ReconcileMoved(ClientSession session)
    {
        _changes.Clear();
        if (!_entries.TryGetValue(session, out Entry entry) || !IsInterestEligible(session))
            return _changes;

        RefreshPosition(entry);
        MoveGridCellIfNeeded(entry);

        // With one player, the mandatory self edge is the complete graph. Keep spatial
        // membership current for a future join, but skip all edge reconciliation work.
        if (_entries.Count <= 1)
            return _changes;

        // Removals remain immediate so a moved player never leaks stale visibility.
        // Additions are queued as resumable candidate work and can degrade in cadence
        // under dense-crowd load without creating one unbounded reconciliation spike.
        ReconcileObserverEdges(entry);
        ReconcileTargetObservers(entry);
        if (_entries.Count > 1)
            QueueAdditionScan(entry);
        SyncAllPrivilegedFollows();
        return _changes;
    }

    /// <summary>
    /// Removes a player and all directed visibility edges. Only observers which could still
    /// receive network traffic are returned as destroy recipients; the disconnecting player
    /// does not receive teardown for objects it was observing.
    /// </summary>
    public IReadOnlyList<Change> Unregister(ClientSession session)
    {
        _changes.Clear();
        if (!_entries.TryGetValue(session, out Entry entry))
            return _changes;

        _edgeScratch.Clear();
        foreach (ClientSession observer in entry.Observers)
            _edgeScratch.Add(observer);
        for (int i = 0; i < _edgeScratch.Count; ++i)
        {
            ClientSession observer = _edgeScratch[i];
            bool notifyObserver = !ReferenceEquals(observer, session) && IsInterestEligible(observer);
            RemoveEdge(observer, session, notifyObserver);
        }

        _edgeScratch.Clear();
        foreach (ClientSession target in entry.VisibleTargets.Keys)
            _edgeScratch.Add(target);
        for (int i = 0; i < _edgeScratch.Count; ++i)
            RemoveEdge(session, _edgeScratch[i], notifyObserver: false);

        RemoveFromGrid(entry);
        entry.AdditionScanQueued = false;
        entry.AdditionScanDirty = false;
        entry.AdditionScanPhase = 0;
        _entries.Remove(session);

        // Once only one player remains there can be no non-self additions. Drop any
        // resumable scans left over from the previous multi-player graph immediately.
        if (_entries.Count <= 1 && _pendingAdditionScans.Count > 0)
        {
            while (_pendingAdditionScans.Count > 0)
            {
                Entry pending = _pendingAdditionScans.Dequeue();
                pending.AdditionScanQueued = false;
                pending.AdditionScanDirty = false;
                pending.AdditionScanPhase = 0;
                pending.AdditionCellOffset = 0;
                pending.AdditionSessionIndex = 0;
            }
        }

        _privilegedFollowTargets.Remove(session);
        _edgeScratch.Clear();
        foreach (KeyValuePair<ClientSession, ClientSession> pair in _privilegedFollowTargets)
            if (ReferenceEquals(pair.Value, session)) _edgeScratch.Add(pair.Key);
        for (int i = 0; i < _edgeScratch.Count; ++i)
            _privilegedFollowTargets.Remove(_edgeScratch[i]);
        return _changes;
    }

    /// <summary>Re-evaluates edges after a server-side visibility policy change such as GM hide/show.</summary>
    public IReadOnlyList<Change> ReconcileVisibility(ClientSession session)
    {
        _changes.Clear();
        if (!_entries.TryGetValue(session, out Entry entry) || !IsInterestEligible(session))
            return _changes;
        ReconcileObserverEdges(entry);
        ReconcileTargetObservers(entry);
        if (_entries.Count > 1)
            QueueAdditionScan(entry);
        SyncAllPrivilegedFollows();
        return _changes;
    }

    /// <summary>
    /// Privileged same-map staff observation. It does not move or persist the staff actor;
    /// it only augments the observer's replication subscriptions with the target's view.
    /// </summary>
    public IReadOnlyList<Change> SetPrivilegedFollow(ClientSession observer, ClientSession target)
    {
        _changes.Clear();
        if (!_entries.TryGetValue(observer, out Entry observerEntry) ||
            !_entries.TryGetValue(target, out Entry targetEntry) ||
            !IsInterestEligible(observer) || !IsInterestEligible(target) ||
            !SamePartition(observerEntry, targetEntry))
            return _changes;
        _privilegedFollowTargets[observer] = target;
        SyncPrivilegedFollow(observer, target);
        return _changes;
    }

    public IReadOnlyList<Change> ClearPrivilegedFollow(ClientSession observer)
    {
        _changes.Clear();
        if (observer == null || !_privilegedFollowTargets.Remove(observer))
            return _changes;
        if (!_entries.TryGetValue(observer, out Entry observerEntry))
            return _changes;

        _edgeScratch.Clear();
        foreach (KeyValuePair<ClientSession, EdgeState> pair in observerEntry.VisibleTargets)
            if (pair.Value.Privileged && !ReferenceEquals(pair.Key, observer)) _edgeScratch.Add(pair.Key);
        for (int i = 0; i < _edgeScratch.Count; ++i)
        {
            ClientSession target = _edgeScratch[i];
            if (_entries.TryGetValue(target, out Entry targetEntry) &&
                CanSee(observer, target) && SamePartition(observerEntry, targetEntry) &&
                DistanceSquared(observerEntry, targetEntry) <= _enterRangeSquared)
            {
                EdgeState state = observerEntry.VisibleTargets[target];
                state.Privileged = false;
                observerEntry.VisibleTargets[target] = state;
            }
            else
            {
                RemoveEdge(observer, target, notifyObserver: IsInterestEligible(observer));
            }
        }
        return _changes;
    }

    /// <summary>
    /// Processes a bounded number of queued AOI-addition candidate checks. Existing-edge
    /// removals are handled synchronously by ReconcileMoved; only newly entering visibility
    /// is allowed to defer under load. Returned changes are reused internally and must be
    /// consumed before the next service mutation.
    /// </summary>
    public IReadOnlyList<Change> ProcessPendingAdditions(int maxCandidateChecks, int maxEdgeAdditions)
    {
        _changes.Clear();
        if (maxCandidateChecks <= 0 || maxEdgeAdditions <= 0 || _pendingAdditionScans.Count == 0)
            return _changes;

        int remaining = maxCandidateChecks;
        int entryPasses = _pendingAdditionScans.Count;
        while (remaining > 0 &&
               _changes.Count < maxEdgeAdditions &&
               entryPasses-- > 0 &&
               _pendingAdditionScans.Count > 0)
        {
            Entry entry = _pendingAdditionScans.Dequeue();
            entry.AdditionScanQueued = false;

            if (!_entries.TryGetValue(entry.Session, out Entry current) ||
                !ReferenceEquals(current, entry) ||
                !IsInterestEligible(entry.Session))
            {
                entry.AdditionScanPhase = 0;
                continue;
            }

            bool completed = ProcessAdditionScan(entry, ref remaining, maxEdgeAdditions);
            if (!completed)
            {
                entry.AdditionScanQueued = true;
                _pendingAdditionScans.Enqueue(entry);
            }
            else if (entry.AdditionScanDirty)
            {
                // Movement/visibility changed while this pass was pending. Queue one
                // clean pass from the beginning without allowing per-tick restart churn.
                entry.AdditionScanPhase = 1;
                entry.AdditionCellOffset = 0;
                entry.AdditionSessionIndex = 0;
                entry.AdditionScanDirty = false;
                entry.AdditionScanQueued = true;
                _pendingAdditionScans.Enqueue(entry);
            }
        }

        return _changes;
    }

    /// <summary>
    /// Copies ready player observers near an arbitrary authoritative world position using
    /// the existing player AOI spatial grid. Non-player presentation uses this instead of
    /// scanning every connected session or building a parallel observer index.
    /// </summary>
    public int CollectSessionsNear(
        string mapId,
        string instanceId,
        float x,
        float z,
        float radius,
        List<ClientSession> results)
    {
        if (results == null)
            throw new ArgumentNullException(nameof(results));
        results.Clear();

        float range = Math.Max(0f, radius);
        float rangeSquared = range * range;
        int cellRadius = Math.Max(0, (int)Math.Ceiling(range / _cellSize));
        int centerX = ToCellCoordinate(x);
        int centerZ = ToCellCoordinate(z);
        string normalizedMap = mapId ?? string.Empty;
        string normalizedInstance = instanceId ?? string.Empty;

        for (int dz = -cellRadius; dz <= cellRadius; ++dz)
        {
            for (int dx = -cellRadius; dx <= cellRadius; ++dx)
            {
                var key = new CellKey(normalizedMap, normalizedInstance, centerX + dx, centerZ + dz);
                if (!_grid.TryGetValue(key, out SpatialCell cell))
                    continue;

                List<ClientSession> sessions = cell.Sessions;
                for (int i = 0; i < sessions.Count; ++i)
                {
                    ClientSession session = sessions[i];
                    if (!_entries.TryGetValue(session, out Entry entry) || !IsInterestEligible(session))
                        continue;
                    float deltaX = entry.X - x;
                    float deltaZ = entry.Z - z;
                    if ((deltaX * deltaX) + (deltaZ * deltaZ) <= rangeSquared)
                        results.Add(session);
                }
            }
        }

        return results.Count;
    }

    public bool TryGetObservers(ClientSession target, out HashSet<ClientSession> observers)
    {
        if (_entries.TryGetValue(target, out Entry entry))
        {
            observers = entry.Observers;
            return true;
        }

        observers = null;
        return false;
    }

    /// <summary>
    /// Returns whether target currently belongs to observer's authoritative visibility set.
    /// Interaction target resolution uses the same graph as replication so guessed/stale
    /// object ids cannot bypass AOI admission.
    /// </summary>
    public bool IsVisibleTo(ClientSession observer, ClientSession target)
    {
        if (observer == null || target == null)
            return false;
        return _entries.TryGetValue(observer, out Entry entry) &&
               entry.VisibleTargets.ContainsKey(target);
    }

    /// <summary>
    /// Copies the observer's current authoritative AOI targets into caller-owned scratch.
    /// Combat contact uses this instead of scanning all connected sessions.
    /// </summary>
    public int CollectVisibleTargets(ClientSession observer, List<ClientSession> results)
    {
        if (results == null)
            throw new ArgumentNullException(nameof(results));
        results.Clear();
        if (observer == null || !_entries.TryGetValue(observer, out Entry entry))
            return 0;

        foreach (ClientSession target in entry.VisibleTargets.Keys)
        {
            if (target != null && !ReferenceEquals(target, observer))
                results.Add(target);
        }
        return results.Count;
    }


    public bool TryGetDistanceSquared(ClientSession observer, ClientSession target, out float distanceSquared)
    {
        if (!_entries.TryGetValue(observer, out Entry observerEntry) ||
            !_entries.TryGetValue(target, out Entry targetEntry) ||
            !SamePartition(observerEntry, targetEntry))
        {
            distanceSquared = float.PositiveInfinity;
            return false;
        }

        distanceSquared = DistanceSquared(observerEntry, targetEntry);
        return true;
    }

    /// <summary>
    /// Per-observer LOD gate stored on the visibility edge. This avoids a separate global
    /// pair dictionary and keeps hot replication state colocated with observer membership.
    /// </summary>
    public bool TryAcquireSnapshotSlot(
        ClientSession observer,
        ClientSession target,
        double now,
        double intervalSeconds,
        bool forceImmediate)
    {
        if (!_entries.TryGetValue(observer, out Entry observerEntry) ||
            !observerEntry.VisibleTargets.TryGetValue(target, out EdgeState state))
            return false;

        if (!forceImmediate && now + 0.000001d < state.NextSnapshotAt)
            return false;

        state.NextSnapshotAt = now + Math.Max(0.001d, intervalSeconds);
        observerEntry.VisibleTargets[target] = state;
        return true;
    }

    public void MarkBaselineSent(ClientSession observer, ClientSession target, double nextSnapshotAt)
    {
        if (!_entries.TryGetValue(observer, out Entry observerEntry) ||
            !observerEntry.VisibleTargets.TryGetValue(target, out EdgeState state))
            return;

        if (nextSnapshotAt > state.NextSnapshotAt)
        {
            state.NextSnapshotAt = nextSnapshotAt;
            observerEntry.VisibleTargets[target] = state;
        }
    }

    private void ReconcileObserverEdges(Entry observer)
    {
        _edgeScratch.Clear();
        foreach (ClientSession target in observer.VisibleTargets.Keys)
            _edgeScratch.Add(target);

        for (int i = 0; i < _edgeScratch.Count; ++i)
        {
            ClientSession targetSession = _edgeScratch[i];
            if (ReferenceEquals(targetSession, observer.Session))
                continue;
            if (observer.VisibleTargets.TryGetValue(targetSession, out EdgeState existingState) && existingState.Privileged)
                continue;

            if (!_entries.TryGetValue(targetSession, out Entry target) ||
                !IsInterestEligible(targetSession) ||
                !CanSee(observer.Session, targetSession) ||
                !SamePartition(observer, target) ||
                DistanceSquared(observer, target) > _exitRangeSquared)
            {
                RemoveEdge(observer.Session, targetSession, notifyObserver: IsInterestEligible(observer.Session));
            }
        }
    }

    private void ReconcileTargetObservers(Entry target)
    {
        _edgeScratch.Clear();
        foreach (ClientSession observer in target.Observers)
            _edgeScratch.Add(observer);

        for (int i = 0; i < _edgeScratch.Count; ++i)
        {
            ClientSession observerSession = _edgeScratch[i];
            if (ReferenceEquals(observerSession, target.Session))
                continue;

            if (_entries.TryGetValue(observerSession, out Entry privilegedObserver) &&
                privilegedObserver.VisibleTargets.TryGetValue(target.Session, out EdgeState privilegedState) &&
                privilegedState.Privileged)
                continue;

            if (!_entries.TryGetValue(observerSession, out Entry observer) ||
                !IsInterestEligible(observerSession) ||
                !CanSee(observerSession, target.Session) ||
                !SamePartition(observer, target) ||
                DistanceSquared(observer, target) > _exitRangeSquared)
            {
                RemoveEdge(observerSession, target.Session, notifyObserver: IsInterestEligible(observerSession));
            }
        }
    }

    private void QueueAdditionScan(Entry entry)
    {
        if (entry == null)
            return;

        if (entry.AdditionScanQueued)
        {
            // Do not restart a partially processed scan on every movement tick or a
            // continuously moving player could starve itself forever. Mark it dirty so
            // one fresh full pass follows the current bounded pass.
            entry.AdditionScanDirty = true;
            return;
        }

        entry.AdditionScanPhase = 1;
        entry.AdditionCellOffset = 0;
        entry.AdditionSessionIndex = 0;
        entry.AdditionScanDirty = false;
        entry.AdditionScanQueued = true;
        _pendingAdditionScans.Enqueue(entry);
    }

    private bool ProcessAdditionScan(
        Entry center,
        ref int remainingCandidateChecks,
        int maxEdgeAdditions)
    {
        int diameter = (_queryCellRadius * 2) + 1;
        int cellCount = diameter * diameter;
        int centerX = ToCellCoordinate(center.X);
        int centerZ = ToCellCoordinate(center.Z);

        // One spatial candidate pass updates both directed edges. Visibility remains
        // directional; only the shared partition/distance lookup is coalesced. Combat and
        // other AOI consumers still see the same observer -> target graph.
        while (center.AdditionCellOffset < cellCount)
        {
            int offset = center.AdditionCellOffset;
            int dx = (offset % diameter) - _queryCellRadius;
            int dz = (offset / diameter) - _queryCellRadius;
            var key = new CellKey(center.MapId, center.InstanceId, centerX + dx, centerZ + dz);

            if (!_grid.TryGetValue(key, out SpatialCell cell))
            {
                center.AdditionCellOffset++;
                center.AdditionSessionIndex = 0;
                continue;
            }

            List<ClientSession> sessions = cell.Sessions;
            while (center.AdditionSessionIndex < sessions.Count)
            {
                if (remainingCandidateChecks <= 0 || _changes.Count >= maxEdgeAdditions)
                    return false;

                // Do not advance the resumable cursor until both directed decisions have
                // been attempted. If the first edge consumes the final edge-budget slot,
                // the next pass revisits this candidate, observes that edge already exists,
                // and safely completes the reverse direction.
                ClientSession candidateSession = sessions[center.AdditionSessionIndex];
                remainingCandidateChecks--;

                if (!_entries.TryGetValue(candidateSession, out Entry candidate) ||
                    !IsInterestEligible(candidateSession) ||
                    !SamePartition(center, candidate) ||
                    DistanceSquared(center, candidate) > _enterRangeSquared)
                {
                    center.AdditionSessionIndex++;
                    continue;
                }

                AddEdge(center.Session, candidateSession);
                if (_changes.Count >= maxEdgeAdditions)
                    return false;

                AddEdge(candidateSession, center.Session);
                center.AdditionSessionIndex++;
            }

            center.AdditionCellOffset++;
            center.AdditionSessionIndex = 0;
        }

        center.AdditionScanPhase = 0;
        return true;
    }

    private void AddEdge(ClientSession observerSession, ClientSession targetSession, bool privileged = false)
    {
        if (!_entries.TryGetValue(observerSession, out Entry observer) ||
            !_entries.TryGetValue(targetSession, out Entry target) ||
            (!privileged && !CanSee(observerSession, targetSession)))
            return;

        if (observer.VisibleTargets.TryGetValue(targetSession, out EdgeState existing))
        {
            if (privileged && !existing.Privileged)
            {
                existing.Privileged = true;
                observer.VisibleTargets[targetSession] = existing;
            }
            return;
        }

        observer.VisibleTargets.Add(targetSession, new EdgeState { Privileged = privileged });
        target.Observers.Add(observerSession);
        _observerEdgeCount++;
        _changes.Add(new Change(ChangeKind.Added, observerSession, targetSession, targetSession.Entity.ObjectId));
    }

    private void RemoveEdge(ClientSession observerSession, ClientSession targetSession, bool notifyObserver)
    {
        if (!_entries.TryGetValue(observerSession, out Entry observer) ||
            !observer.VisibleTargets.Remove(targetSession))
            return;

        uint targetObjectId = targetSession?.Entity?.ObjectId ?? 0u;
        if (_entries.TryGetValue(targetSession, out Entry target))
            target.Observers.Remove(observerSession);

        _observerEdgeCount = Math.Max(0L, _observerEdgeCount - 1L);
        if (notifyObserver && targetObjectId != 0u)
            _changes.Add(new Change(ChangeKind.Removed, observerSession, targetSession, targetObjectId));
    }

    private bool CanSee(ClientSession observer, ClientSession target)
    {
        if (ReferenceEquals(observer, target)) return true;
        return _visibilityPolicy == null || _visibilityPolicy(observer, target);
    }

    private void SyncAllPrivilegedFollows()
    {
        if (_privilegedFollowTargets.Count == 0) return;
        // Keep the outer observer snapshot separate from SyncPrivilegedFollow's edge-removal
        // scratch. Sharing one list lets the inner call overwrite the outer iteration.
        _privilegedFollowObserverScratch.Clear();
        foreach (ClientSession observer in _privilegedFollowTargets.Keys)
            _privilegedFollowObserverScratch.Add(observer);
        for (int i = 0; i < _privilegedFollowObserverScratch.Count; ++i)
        {
            ClientSession observer = _privilegedFollowObserverScratch[i];
            if (_privilegedFollowTargets.TryGetValue(observer, out ClientSession target))
                SyncPrivilegedFollow(observer, target);
        }
    }

    private void SyncPrivilegedFollow(ClientSession observerSession, ClientSession followTargetSession)
    {
        if (!_entries.TryGetValue(observerSession, out Entry observer) ||
            !_entries.TryGetValue(followTargetSession, out Entry followTarget) ||
            !IsInterestEligible(observerSession) || !IsInterestEligible(followTargetSession) ||
            !SamePartition(observer, followTarget))
            return;

        var desired = new HashSet<ClientSession>();
        desired.Add(followTargetSession);
        foreach (ClientSession target in followTarget.VisibleTargets.Keys)
            if (IsInterestEligible(target)) desired.Add(target);

        _edgeScratch.Clear();
        foreach (KeyValuePair<ClientSession, EdgeState> pair in observer.VisibleTargets)
            if (pair.Value.Privileged && !desired.Contains(pair.Key)) _edgeScratch.Add(pair.Key);
        for (int i = 0; i < _edgeScratch.Count; ++i)
            RemoveEdge(observerSession, _edgeScratch[i], notifyObserver: true);

        foreach (ClientSession target in desired)
            AddEdge(observerSession, target, privileged: true);
    }

    private void RefreshPosition(Entry entry)
    {
        var location = entry.Session.Entity.Runtime.Location;
        entry.MapId = location.MapId;
        entry.InstanceId = location.InstanceId;
        entry.X = entry.Session.Entity.X;
        entry.Z = entry.Session.Entity.Z;
    }

    private void AddToGrid(Entry entry)
    {
        var key = MakeCellKey(entry);
        if (!_grid.TryGetValue(key, out SpatialCell cell))
        {
            cell = _cellPool.Count > 0 ? _cellPool.Pop() : new SpatialCell();
            _grid.Add(key, cell);
        }

        entry.Cell = key;
        entry.CellIndex = cell.Sessions.Count;
        entry.InGrid = true;
        cell.Sessions.Add(entry.Session);
    }

    private void MoveGridCellIfNeeded(Entry entry)
    {
        CellKey desired = MakeCellKey(entry);
        if (entry.InGrid && entry.Cell.Equals(desired))
            return;

        RemoveFromGrid(entry);
        AddToGrid(entry);
    }

    private void RemoveFromGrid(Entry entry)
    {
        if (!entry.InGrid || !_grid.TryGetValue(entry.Cell, out SpatialCell cell))
        {
            entry.InGrid = false;
            entry.CellIndex = -1;
            return;
        }

        int index = entry.CellIndex;
        int lastIndex = cell.Sessions.Count - 1;
        if ((uint)index > (uint)lastIndex || !ReferenceEquals(cell.Sessions[index], entry.Session))
            index = cell.Sessions.IndexOf(entry.Session);

        if (index >= 0)
        {
            lastIndex = cell.Sessions.Count - 1;
            ClientSession moved = cell.Sessions[lastIndex];
            cell.Sessions[index] = moved;
            cell.Sessions.RemoveAt(lastIndex);
            if (!ReferenceEquals(moved, entry.Session) && _entries.TryGetValue(moved, out Entry movedEntry))
                movedEntry.CellIndex = index;
        }

        entry.InGrid = false;
        entry.CellIndex = -1;

        if (cell.Sessions.Count == 0)
        {
            _grid.Remove(entry.Cell);
            if (_cellPool.Count < 2048)
                _cellPool.Push(cell);
        }
    }

    private CellKey MakeCellKey(Entry entry) =>
        new CellKey(entry.MapId, entry.InstanceId, ToCellCoordinate(entry.X), ToCellCoordinate(entry.Z));

    private int ToCellCoordinate(float value) => (int)MathF.Floor(value / _cellSize);

    private static bool SamePartition(Entry a, Entry b) =>
        string.Equals(a.MapId, b.MapId, StringComparison.Ordinal) &&
        string.Equals(a.InstanceId, b.InstanceId, StringComparison.Ordinal);

    private static float DistanceSquared(Entry a, Entry b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static bool IsInterestEligible(ClientSession session) =>
        session != null &&
        session.Connected &&
        session.Ready &&
        session.Entity != null;
}
