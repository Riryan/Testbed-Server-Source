using System;
using System.Collections.Generic;
using Game.GameServer.Runtime;
using Game.Server.Application.Actors;
using Game.Server.Application.Population;
using Game.Server.Domain.Characters;
using Game.Shared.Actors;
using Game.Shared.Characters;
using Game.Shared.Population;
using Game.Shared.World;
using LiteNetLib;
using LiteNetLib.Utils;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

/// <summary>
/// Humanoid Population client presentation over the already-established PlayerEntity
/// spawn/snapshot/appearance wire. Population gameplay authority remains in
/// PopulationSimulationService/AuthoritativeActorRuntime; no PlayerRuntime is created for
/// presentation. The client therefore executes the exact current PlayerEntityClient visual,
/// animator, interpolation, IK and authoritative-death presentation path.
/// </summary>
internal sealed partial class GameServerHost
{
    private sealed class PopulationPresentationState
    {
        public ServerPopulationPresentationEntity Entity { get; }
        public HashSet<ClientSession> Observers { get; } = new HashSet<ClientSession>();
        public Dictionary<ClientSession, double> NextSnapshotAt { get; } =
            new Dictionary<ClientSession, double>();
        public PopulationPublicInteractionFlags PublicInteractionFlags { get; private set; }
        public uint AppearanceSequence { get; private set; } = 1u;

        public PopulationPresentationState(
            ServerPopulationPresentationEntity entity,
            PopulationPublicInteractionFlags publicInteractionFlags)
        {
            Entity = entity;
            PublicInteractionFlags = publicInteractionFlags;
        }

        public bool SetPublicInteractionFlags(PopulationPublicInteractionFlags value)
        {
            if (PublicInteractionFlags == value)
                return false;
            PublicInteractionFlags = value;
            AppearanceSequence++;
            if (AppearanceSequence == 0u)
                AppearanceSequence = 1u;
            return true;
        }
    }

    private readonly Dictionary<long, PopulationPresentationState> _populationPresentations =
        new Dictionary<long, PopulationPresentationState>();
    private readonly Dictionary<ClientSession, HashSet<long>> _populationVisibleByObserver =
        new Dictionary<ClientSession, HashSet<long>>();
    private readonly List<ClientSession> _populationObserverScratch = new List<ClientSession>(128);
    private readonly List<long> _populationActorIdScratch = new List<long>(128);
    private readonly List<AuthoritativeActorRuntime> _populationActorScratch =
        new List<AuthoritativeActorRuntime>(128);

    private void InitializePopulationPresentation()
    {
        foreach (PopulationActorRuntime population in _runtime.Population.All)
            EnsurePopulationPresentation(population);

        _runtime.Population.Added += OnPopulationPresentationAdded;
        _runtime.Population.Changed += OnPopulationPresentationChanged;
        _runtime.Population.Removed += OnPopulationPresentationRemoved;
        _runtime.PopulationLoot.PublicInteractionStateChanged += OnPopulationPublicInteractionStateChanged;
    }

    private void DisposePopulationPresentation()
    {
        _runtime.Population.Added -= OnPopulationPresentationAdded;
        _runtime.Population.Changed -= OnPopulationPresentationChanged;
        _runtime.Population.Removed -= OnPopulationPresentationRemoved;
        _runtime.PopulationLoot.PublicInteractionStateChanged -= OnPopulationPublicInteractionStateChanged;

        _populationPresentations.Clear();
        _populationVisibleByObserver.Clear();
        _populationObserverScratch.Clear();
        _populationActorIdScratch.Clear();
        _populationActorScratch.Clear();
    }

    private PopulationPresentationState EnsurePopulationPresentation(PopulationActorRuntime population)
    {
        if (population?.Actor == null || !population.Actor.Handle.IsValid)
            return null;

        long actorId = population.Actor.Handle.actorId;
        if (_populationPresentations.TryGetValue(actorId, out PopulationPresentationState existing))
            return existing;

        var state = new PopulationPresentationState(
            new ServerPopulationPresentationEntity(NextObjectId(), population),
            ResolvePopulationPublicInteractionFlags(population));
        _populationPresentations.Add(actorId, state);
        return state;
    }

    private void OnPopulationPresentationAdded(PopulationActorRuntime population)
    {
        PopulationPresentationState state = EnsurePopulationPresentation(population);
        if (state != null)
            ReconcilePopulationPresentation(state, queueSnapshot: false);
    }

    private void OnPopulationPresentationChanged(PopulationActorRuntime population)
    {
        PopulationPresentationState state = EnsurePopulationPresentation(population);
        if (state == null)
            return;

        if (state.SetPublicInteractionFlags(ResolvePopulationPublicInteractionFlags(population)))
            BroadcastPopulationAppearanceDelta(state);

        bool forceSnapshot = state.Entity.IsDead;
        ReconcilePopulationPresentation(state, queueSnapshot: true, forceSnapshot: forceSnapshot);
    }

    private void OnPopulationPublicInteractionStateChanged(long actorId)
    {
        if (actorId <= 0)
            return;

        _mainThreadCompletions.Enqueue(() =>
        {
            if (!_populationPresentations.TryGetValue(actorId, out PopulationPresentationState state) ||
                state?.Entity?.Population == null)
            {
                return;
            }

            if (state.SetPublicInteractionFlags(ResolvePopulationPublicInteractionFlags(state.Entity.Population)))
                BroadcastPopulationAppearanceDelta(state);
        });
    }

    private PopulationPublicInteractionFlags ResolvePopulationPublicInteractionFlags(PopulationActorRuntime population)
    {
        AuthoritativeActorRuntime actor = population?.Actor;
        if (actor == null || actor.Alive || actor.HealthCurrent > 0)
            return PopulationPublicInteractionFlags.None;

        return _runtime.PopulationLoot.CanLoot(actor)
            ? PopulationPublicInteractionFlags.DeathLootAvailable
            : PopulationPublicInteractionFlags.None;
    }

    private void OnPopulationPresentationRemoved(PopulationActorRuntime population)
    {
        if (population?.Actor == null)
            return;

        long actorId = population.Actor.Handle.actorId;
        if (!_populationPresentations.TryGetValue(actorId, out PopulationPresentationState state))
            return;

        _populationObserverScratch.Clear();
        foreach (ClientSession observer in state.Observers)
            _populationObserverScratch.Add(observer);
        for (int i = 0; i < _populationObserverScratch.Count; ++i)
            RemovePopulationVisibility(_populationObserverScratch[i], state, sendDestroy: true);

        _populationPresentations.Remove(actorId);
    }

    private bool CanPresentPopulation(PopulationPresentationState state)
    {
        PopulationActorRuntime population = state?.Entity?.Population;
        return population?.Actor != null &&
               population.Actor.Handle.kind == AuthoritativeActorKind.Population &&
               population.AiState != PopulationAiState.PortalDormant &&
               !population.IsHibernating;
    }

    private void ReconcilePopulationObserver(ClientSession observer)
    {
        if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null)
            return;

        if (!_populationVisibleByObserver.TryGetValue(observer, out HashSet<long> visible))
        {
            visible = new HashSet<long>();
            _populationVisibleByObserver.Add(observer, visible);
        }

        float exitRange = Math.Max(1f, _options.AoiRange + _options.AoiExitPadding);
        float exitRangeSquared = exitRange * exitRange;

        _populationActorIdScratch.Clear();
        foreach (long actorId in visible)
            _populationActorIdScratch.Add(actorId);
        for (int i = 0; i < _populationActorIdScratch.Count; ++i)
        {
            long actorId = _populationActorIdScratch[i];
            if (!_populationPresentations.TryGetValue(actorId, out PopulationPresentationState state) ||
                !CanPresentPopulation(state) ||
                !IsPopulationInObserverPartition(observer, state.Entity.Population.Actor) ||
                PopulationDistanceSquared(observer, state.Entity.Population.Actor) > exitRangeSquared)
            {
                if (state != null)
                    RemovePopulationVisibility(observer, state, sendDestroy: true);
                else
                    visible.Remove(actorId);
            }
        }

        CharacterLocationState location = observer.Entity.Runtime.Location;
        _runtime.Actors.QueryRadius(
            location.MapId,
            location.InstanceId,
            new WorldPosition(observer.Entity.X, observer.Entity.Y, observer.Entity.Z),
            Math.Max(1f, _options.AoiRange),
            _populationActorScratch);

        float enterRangeSquared = Math.Max(1f, _options.AoiRange) * Math.Max(1f, _options.AoiRange);
        for (int i = 0; i < _populationActorScratch.Count; ++i)
        {
            AuthoritativeActorRuntime actor = _populationActorScratch[i];
            if (actor == null || actor.Handle.kind != AuthoritativeActorKind.Population ||
                !_populationPresentations.TryGetValue(actor.Handle.actorId, out PopulationPresentationState state) ||
                !CanPresentPopulation(state) ||
                PopulationDistanceSquared(observer, actor) > enterRangeSquared)
            {
                continue;
            }

            AddPopulationVisibility(observer, state);
        }
    }

    private void ReconcilePopulationPresentation(
        PopulationPresentationState state,
        bool queueSnapshot,
        bool forceSnapshot = false)
    {
        if (state == null)
            return;

        if (!CanPresentPopulation(state))
        {
            _populationObserverScratch.Clear();
            foreach (ClientSession observer in state.Observers)
                _populationObserverScratch.Add(observer);
            for (int i = 0; i < _populationObserverScratch.Count; ++i)
                RemovePopulationVisibility(_populationObserverScratch[i], state, sendDestroy: true);
            return;
        }

        AuthoritativeActorRuntime actor = state.Entity.Population.Actor;
        float exitRange = Math.Max(1f, _options.AoiRange + _options.AoiExitPadding);
        float exitRangeSquared = exitRange * exitRange;

        _populationObserverScratch.Clear();
        foreach (ClientSession observer in state.Observers)
            _populationObserverScratch.Add(observer);
        for (int i = 0; i < _populationObserverScratch.Count; ++i)
        {
            ClientSession observer = _populationObserverScratch[i];
            if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null ||
                !IsPopulationInObserverPartition(observer, actor) ||
                PopulationDistanceSquared(observer, actor) > exitRangeSquared)
            {
                RemovePopulationVisibility(observer, state, sendDestroy: true);
            }
        }

        _worldInterest.CollectSessionsNear(
            actor.MapId,
            actor.InstanceId,
            actor.Position.X,
            actor.Position.Z,
            Math.Max(1f, _options.AoiRange),
            _populationObserverScratch);
        for (int i = 0; i < _populationObserverScratch.Count; ++i)
            AddPopulationVisibility(_populationObserverScratch[i], state);

        if (!queueSnapshot)
            return;

        _populationObserverScratch.Clear();
        foreach (ClientSession observer in state.Observers)
            _populationObserverScratch.Add(observer);
        for (int i = 0; i < _populationObserverScratch.Count; ++i)
            QueuePopulationSnapshot(_populationObserverScratch[i], state, forceSnapshot);
    }

    private void AddPopulationVisibility(ClientSession observer, PopulationPresentationState state)
    {
        if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null ||
            state == null || !CanPresentPopulation(state))
            return;

        long actorId = state.Entity.Population.Actor.Handle.actorId;
        if (!_populationVisibleByObserver.TryGetValue(observer, out HashSet<long> visible))
        {
            visible = new HashSet<long>();
            _populationVisibleByObserver.Add(observer, visible);
        }
        if (!visible.Add(actorId))
            return;

        state.Observers.Add(observer);
        SendPopulationSpawn(observer, state);

        double interval = PopulationSnapshotInterval(observer, state.Entity.Population.Actor);
        state.NextSnapshotAt[observer] = _scheduler.ServerTime + interval;
    }

    private void RemovePopulationVisibility(
        ClientSession observer,
        PopulationPresentationState state,
        bool sendDestroy)
    {
        if (observer == null || state == null)
            return;

        long actorId = state.Entity.Population.Actor.Handle.actorId;
        if (_populationVisibleByObserver.TryGetValue(observer, out HashSet<long> visible))
        {
            visible.Remove(actorId);
            if (visible.Count == 0)
                _populationVisibleByObserver.Remove(observer);
        }

        if (!state.Observers.Remove(observer))
            return;

        state.NextSnapshotAt.Remove(observer);
        DropPendingSnapshot(observer, state.Entity.ObjectId);
        if (sendDestroy && IsCurrent(observer) && observer.Ready && observer.Entity != null)
            SendDestroy(observer, state.Entity.ObjectId);
    }

    private void RemovePopulationObserver(ClientSession observer)
    {
        if (observer == null || !_populationVisibleByObserver.TryGetValue(observer, out HashSet<long> visible))
            return;

        _populationActorIdScratch.Clear();
        foreach (long actorId in visible)
            _populationActorIdScratch.Add(actorId);
        for (int i = 0; i < _populationActorIdScratch.Count; ++i)
        {
            if (_populationPresentations.TryGetValue(_populationActorIdScratch[i], out PopulationPresentationState state))
            {
                state.Observers.Remove(observer);
                state.NextSnapshotAt.Remove(observer);
                DropPendingSnapshot(observer, state.Entity.ObjectId);
            }
        }
        _populationVisibleByObserver.Remove(observer);
    }

    private void QueuePopulationSnapshot(
        ClientSession observer,
        PopulationPresentationState state,
        bool forceImmediate)
    {
        if (!IsCurrent(observer) || !observer.Ready || observer.Entity == null ||
            state == null || !state.Observers.Contains(observer))
            return;

        double now = _scheduler.ServerTime;
        if (!forceImmediate && state.NextSnapshotAt.TryGetValue(observer, out double nextAt) &&
            now + 0.000001d < nextAt)
            return;

        state.NextSnapshotAt[observer] = now + PopulationSnapshotInterval(observer, state.Entity.Population.Actor);
        QueueSnapshot(
            observer,
            state.Entity,
            state.Entity.SnapshotSpeed,
            state.Entity.SnapshotFlags,
            state.Entity.SnapshotMoveState,
            forceImmediate,
            state.Entity.IsDead ? (byte)PlayerEntityActionState.Dead : (byte)PlayerEntityActionState.None,
            0);
    }

    private double PopulationSnapshotInterval(ClientSession observer, AuthoritativeActorRuntime actor)
    {
        float distanceSquared = PopulationDistanceSquared(observer, actor);
        return _options.ReplicationLodEnabled
            ? _replicationLodPolicy.GetIntervalSeconds(distanceSquared)
            : 1d / Math.Max(1, (int)_options.TickRate);
    }

    private static bool IsPopulationInObserverPartition(ClientSession observer, AuthoritativeActorRuntime actor)
    {
        if (observer?.Entity == null || actor == null)
            return false;
        CharacterLocationState location = observer.Entity.Runtime.Location;
        return string.Equals(location.MapId, actor.MapId, StringComparison.Ordinal) &&
               string.Equals(location.InstanceId, actor.InstanceId, StringComparison.Ordinal);
    }

    private static float PopulationDistanceSquared(ClientSession observer, AuthoritativeActorRuntime actor)
    {
        if (observer?.Entity == null || actor == null)
            return float.PositiveInfinity;
        float x = observer.Entity.X - actor.Position.X;
        float z = observer.Entity.Z - actor.Position.Z;
        return (x * x) + (z * z);
    }

    private void SendPopulationSpawn(ClientSession recipient, PopulationPresentationState state)
    {
        ServerPopulationPresentationEntity entity = state?.Entity;
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null || entity == null)
            return;

        _writer.Reset();
        _writer.PutPackedUShort(SyncBaseLineMessageType);
        _writer.PutPackedUInt(CurrentTick);
        _writer.Put((ushort)1);
        _writer.Put(GameStateSpawn);
        _writer.Put(false); // existing PlayerEntity network prefab, not scene object
        _writer.PutPackedInt(PlayerAssetHash);
        _writer.Put(entity.X);
        _writer.Put(entity.Y);
        _writer.Put(entity.Z);
        _writer.Put(0f);
        _writer.Put(entity.YawDegrees);
        _writer.Put(0f);
        _writer.PutPackedUInt(entity.ObjectId);
        _writer.PutPackedLong(entity.ConnectionId); // -1 = unowned/server-owned
        _writer.PutPackedInt(2);

        _writer.PutPackedInt(SnapshotElementId);
        WriteSnapshot(_writer, entity, entity.SnapshotSpeed, entity.SnapshotFlags, entity.SnapshotMoveState,
            entity.IsDead ? (byte)PlayerEntityActionState.Dead : (byte)PlayerEntityActionState.None);

        _writer.PutPackedInt(AppearanceElementId);
        WritePopulationAppearance(_writer, state);

        _replicationSpawnSends++;
        _replicationPayloadBytes += _writer.Length;
        Send(recipient, _writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendPopulationAppearanceDelta(ClientSession recipient, PopulationPresentationState state)
    {
        ServerPopulationPresentationEntity entity = state?.Entity;
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null || entity == null)
            return;

        _writer.Reset();
        _writer.PutPackedUShort(SyncDeltaMessageType);
        _writer.PutPackedUInt(CurrentTick);
        _writer.Put((ushort)1);
        _writer.PutPackedUInt(entity.ObjectId);

        int dataLengthPosition = _writer.Length;
        _writer.Put((ushort)0);
        int dataStartPosition = _writer.Length;
        _writer.Put((ushort)1);
        _writer.PutPackedInt(AppearanceElementId);
        WritePopulationAppearance(_writer, state);

        int endPosition = _writer.Length;
        int dataLength = endPosition - dataStartPosition;
        if (dataLength > ushort.MaxValue)
            throw new InvalidOperationException("Population appearance delta exceeded protocol payload length.");

        _writer.SetPosition(dataLengthPosition);
        _writer.Put((ushort)dataLength);
        _writer.SetPosition(endPosition);
        _replicationPayloadBytes += _writer.Length;
        Send(recipient, _writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastPopulationAppearanceDelta(PopulationPresentationState state)
    {
        if (state == null)
            return;

        _populationObserverScratch.Clear();
        foreach (ClientSession observer in state.Observers)
            _populationObserverScratch.Add(observer);
        for (int i = 0; i < _populationObserverScratch.Count; ++i)
            SendPopulationAppearanceDelta(_populationObserverScratch[i], state);
    }

    private static void WritePopulationAppearance(
        LiteNetLib.Utils.NetDataWriter writer,
        PopulationPresentationState state)
    {
        ServerPopulationPresentationEntity entity = state.Entity;
        AuthoritativeActorRuntime actor = entity.Population.Actor;
        var appearance = new PlayerEntityAppearance
        {
            generation = entity.Generation,
            sequence = state.AppearanceSequence,
            // Existing long identity field carries the canonical Pop actor id. The network
            // object's ConnectionId=-1 distinguishes this from an owned PlayerEntity.
            characterId = actor.Handle.actorId,
            equipmentVersion = 1u,
            displayName = actor.DisplayName ?? string.Empty,
            guildName = string.Empty,
            populationInteractionFlags = state.PublicInteractionFlags,
            equipmentVisuals = Array.Empty<PlayerEquipmentVisualSelection>(),
            appearance = CharacterAppearanceRecipe.CreateDefault(),
            presentation = CharacterPresentationPreferences.CreateDefault(),
        };
        appearance.Serialize(writer);
    }
}
