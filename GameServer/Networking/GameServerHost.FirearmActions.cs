using Game.GameServer.Runtime;
using Game.Server.Application.Abilities;
using Game.Server.Application.Combat;
using Game.Server.Application.Sessions;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private sealed class FirearmActionState
    {
        public string WeaponDefinitionId = string.Empty;
        public bool AutomaticActive;
        public double ShotAccumulator;
        public double LastAutomaticAt;
        public float Bloom;
        public double LastBloomAt;
        public byte ObserverSequence;
    }

    private sealed class AutomaticFireWorkSystem : ICoreConditionalPreparedBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        public AutomaticFireWorkSystem(GameServerHost owner) => _owner = owner;
        public string Name => "AutomaticFireAuthority";
        public bool ShouldPrepare => _owner._automaticFireActiveCount > 0;
        public bool HasPendingWork => _owner._automaticFireWorkCursor < _owner._automaticFireWork.Count;
        public void Prepare(in CoreTickContext context) => _owner.PrepareAutomaticFireWork();
        public void ExecuteOneWorkUnit(in CoreTickContext context) => _owner.ProcessNextAutomaticFire(context);
    }

    private sealed class FireCycleFlushSystem : ICoreBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        public FireCycleFlushSystem(GameServerHost owner) => _owner = owner;
        public string Name => "CombatFireCycleFlush";
        public bool HasPendingWork => _owner._fireCycleDirty.Count > 0;
        public void ExecuteOneWorkUnit(in CoreTickContext context) => _owner.FlushOneFireCycleBatch();
    }

    private readonly Dictionary<ClientSession, FirearmActionState> _firearmActionStates =
        new Dictionary<ClientSession, FirearmActionState>();
    private readonly List<ClientSession> _automaticFireWork = new List<ClientSession>(64);
    private int _automaticFireWorkCursor;
    private int _automaticFireActiveCount;

    private readonly Dictionary<ClientSession, List<CombatFireCycleWire>> _fireCycleQueues =
        new Dictionary<ClientSession, List<CombatFireCycleWire>>();
    private readonly Queue<ClientSession> _fireCycleDirty = new Queue<ClientSession>();
    private readonly HashSet<ClientSession> _fireCycleDirtySet = new HashSet<ClientSession>();

    private ICoreScheduledSystemHandle _automaticFireHandle;
    private ICoreScheduledSystemHandle _fireCycleFlushHandle;
    private long _wireFireCycleBatches;
    private long _wireFireCycleRecords;
    private long _wireFireCycleBytes;

    private void InitializeFirearmActions()
    {
        _automaticFireHandle = _scheduler.RegisterWorkSystem(
            new AutomaticFireWorkSystem(this),
            "Combat",
            maxWorkUnitsPerTick: Math.Min(2048, Math.Max(64, _options.MaxConnections)),
            quarantineable: false,
            invocationLimitMilliseconds: 1.5);
        _fireCycleFlushHandle = _scheduler.RegisterWorkSystem(
            new FireCycleFlushSystem(this),
            "Combat",
            maxWorkUnitsPerTick: Math.Min(2048, Math.Max(64, _options.MaxConnections)),
            quarantineable: true,
            invocationLimitMilliseconds: 1.0);
    }

    private FirearmActionState GetFirearmActionState(ClientSession session, double now)
    {
        if (!_firearmActionStates.TryGetValue(session, out FirearmActionState state))
        {
            state = new FirearmActionState { LastBloomAt = now };
            _firearmActionStates.Add(session, state);
        }
        return state;
    }

    private static void RecoverBloom(FirearmActionState state, in FirearmActionProfile profile, double now)
    {
        if (state.LastBloomAt <= 0d)
        {
            state.LastBloomAt = now;
            return;
        }
        double elapsed = Math.Max(0d, Math.Min(5d, now - state.LastBloomAt));
        if (elapsed > 0d && state.Bloom > 0f)
            state.Bloom = Math.Max(0f, state.Bloom - (profile.BloomRecoveryPerSecond * (float)elapsed));
        state.LastBloomAt = now;
    }

    private bool TryResolveFirearmTrigger(
        ClientSession session,
        BasicAttackInputKind inputKind,
        PlayerRuntime source,
        PlayerRuntime target,
        out BasicAttackResult result)
    {
        double now = _scheduler.ServerTime;
        result = default;
        if (!_runtime.BasicAttacks.TryResolveFirearmProfile(source, now, out FirearmActionProfile profile))
            return false;

        FirearmActionState state = GetFirearmActionState(session, now);
        if (!string.Equals(state.WeaponDefinitionId, profile.WeaponDefinitionId, StringComparison.Ordinal))
        {
            state.WeaponDefinitionId = profile.WeaponDefinitionId;
            SetAutomaticFireActive(state, false);
            state.ShotAccumulator = 0d;
            state.LastAutomaticAt = 0d;
            state.Bloom = 0f;
            state.LastBloomAt = now;
        }
        RecoverBloom(state, profile, now);

        bool aiming = session.Entity != null && session.Entity.IsCombatReady;
        int requestedRounds = profile.FireMode == FirearmFireMode.Burst
            ? profile.RoundsPerTrigger
            : 1;

        FirearmActionResolution resolution = _runtime.BasicAttacks.TryFirearmAction(
            source, target, inputKind, requestedRounds, state.Bloom, aiming, enforceRecovery: true, now);
        result = resolution.Action;
        if (!resolution.Success)
        {
            SetAutomaticFireActive(state, false);
            state.ShotAccumulator = 0d;
            return true;
        }

        state.Bloom = resolution.EndingBloom;
        state.LastBloomAt = now;
        QueueFireCycle(session, resolution, aiming, ResolveCycleSpan(profile, resolution.RoundsFired, (float)_options.CombatTickRate));

        if (profile.FireMode == FirearmFireMode.FullAutomatic)
        {
            SetAutomaticFireActive(state, true);
            state.ShotAccumulator = 0d;
            state.LastAutomaticAt = now;
        }
        else
        {
            SetAutomaticFireActive(state, false);
            state.ShotAccumulator = 0d;
            state.LastAutomaticAt = 0d;
        }
        return true;
    }

    private void SetAutomaticFireActive(FirearmActionState state, bool active)
    {
        if (state == null || state.AutomaticActive == active)
            return;

        state.AutomaticActive = active;
        _automaticFireActiveCount += active ? 1 : -1;
        if (_automaticFireActiveCount < 0)
            _automaticFireActiveCount = 0;
    }

    private void PrepareAutomaticFireWork()
    {
        _automaticFireWork.Clear();
        _automaticFireWorkCursor = 0;
        foreach (KeyValuePair<ClientSession, FirearmActionState> pair in _firearmActionStates)
        {
            if (pair.Value.AutomaticActive)
                _automaticFireWork.Add(pair.Key);
        }
    }

    private void ProcessNextAutomaticFire(in CoreTickContext context)
    {
        if (_automaticFireWorkCursor >= _automaticFireWork.Count)
            return;
        ClientSession session = _automaticFireWork[_automaticFireWorkCursor++];
        if (session == null || !IsCurrent(session) || !session.Ready || session.Entity == null ||
            !_firearmActionStates.TryGetValue(session, out FirearmActionState state) || !state.AutomaticActive)
            return;

        if (!session.Entity.TryGetFreshCombatInput(Environment.TickCount64, out byte combatFlags) ||
            (combatFlags & (byte)PlayerCombatInputFlags.FireHeld) == 0)
        {
            StopAutomaticFire(session, state, sendOwnerCorrection: false);
            return;
        }

        if (!TryGetGameplayRuntime(session, out PlayerRuntime source) ||
            !_runtime.BasicAttacks.TryResolveFirearmProfile(source, context.Now, out FirearmActionProfile profile) ||
            profile.FireMode != FirearmFireMode.FullAutomatic ||
            !string.Equals(profile.WeaponDefinitionId, state.WeaponDefinitionId, StringComparison.Ordinal))
        {
            StopAutomaticFire(session, state, sendOwnerCorrection: true);
            return;
        }

        PlayerRuntime target = ResolveDirectCombatContact(
            session,
            source,
            profile.Range,
            BasicAttackMode.Firearm);

        RecoverBloom(state, profile, context.Now);
        double elapsed = state.LastAutomaticAt > 0d
            ? Math.Max(0d, Math.Min(0.25d, context.Now - state.LastAutomaticAt))
            : 0d;
        state.LastAutomaticAt = context.Now;
        state.ShotAccumulator += profile.RoundsPerSecond * elapsed;
        int requested = (int)Math.Floor(state.ShotAccumulator);
        if (requested <= 0)
            return;

        // Do not dump an unbounded hitch backlog into one combat cycle.
        int catchupCap = Math.Max(1, Math.Min(31,
            (int)Math.Ceiling(profile.RoundsPerSecond * Math.Max(0.05d, elapsed) * 2d) + 1));
        requested = Math.Min(requested, catchupCap);
        state.ShotAccumulator = Math.Max(0d, state.ShotAccumulator - requested);

        bool aiming = session.Entity.IsCombatReady;
        FirearmActionResolution resolution = _runtime.BasicAttacks.TryFirearmAction(
            source, target, BasicAttackInputKind.Primary, requested, state.Bloom, aiming,
            enforceRecovery: false, context.Now);
        if (!resolution.Success)
        {
            StopAutomaticFire(session, state, sendOwnerCorrection: true);
            return;
        }

        state.Bloom = resolution.EndingBloom;
        state.LastBloomAt = context.Now;
        QueueFireCycle(session, resolution, aiming, Math.Max(0.02f, context.FixedDelta));
        bool empty = resolution.OwnerState.LoadedRounds <= 0;
        if (empty)
        {
            // Clean clients predict deterministic ammo use locally. One reliable correction
            // at the empty boundary prevents long-lived HUD drift without a 10 Hz owner stream.
            SendCombatPredictionCorrection(session, source, includeResources: false);
            StopAutomaticFire(session, state, sendOwnerCorrection: false);
        }
    }

    private void StopAutomaticFire(ClientSession session, FirearmActionState state, bool sendOwnerCorrection)
    {
        if (state == null)
            return;
        SetAutomaticFireActive(state, false);
        state.ShotAccumulator = 0d;
        state.LastAutomaticAt = 0d;

        if (!sendOwnerCorrection || session == null || !IsCurrent(session) ||
            !TryGetGameplayRuntime(session, out PlayerRuntime runtime))
            return;

        // This is an exception/resync path only. Normal full-auto cadence produces no
        // owner-cycle packets; the clean client predicts ammo/presentation from shared data.
        SendCombatPredictionCorrection(session, runtime, includeResources: false);
    }

    private void QueueFireCycle(ClientSession actor, in FirearmActionResolution resolution, bool aiming, float spanSeconds)
    {
        FirearmActionState state = GetFirearmActionState(actor, _scheduler.ServerTime);
        var cycle = CombatFireCycleWire.Create(
            actor.Entity.ObjectId, actor.Entity.Generation, resolution.Profile.PresentationId,
            ++state.ObserverSequence, resolution.RoundsFired, aiming, resolution.Profile.FireMode, spanSeconds);
        QueueFireCycleToObservers(actor, cycle);
    }

    private void QueueFireCycleToObservers(ClientSession actor, CombatFireCycleWire cycle)
    {
        if (actor?.Entity == null || _worldInterest == null ||
            !_worldInterest.TryGetObservers(actor, out HashSet<ClientSession> observers))
            return;

        float maxDistance = Math.Max(1f, Math.Min(_options.AoiRange, _options.CombatPresentationRange));
        float maxDistanceSq = maxDistance * maxDistance;
        foreach (ClientSession observer in observers)
        {
            if (observer == null || ReferenceEquals(observer, actor) || !IsCurrent(observer) || !observer.Ready || observer.Entity == null)
                continue;
            float dx = observer.Entity.X - actor.Entity.X;
            float dy = observer.Entity.Y - actor.Entity.Y;
            float dz = observer.Entity.Z - actor.Entity.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxDistanceSq)
                continue;

            if (!_fireCycleQueues.TryGetValue(observer, out List<CombatFireCycleWire> queue))
            {
                queue = new List<CombatFireCycleWire>(16);
                _fireCycleQueues.Add(observer, queue);
            }
            if (queue.Count >= PlayerCombatFireCycleBatchMessage.MaximumCycles)
                continue;
            queue.Add(cycle);
            if (_fireCycleDirtySet.Add(observer))
                _fireCycleDirty.Enqueue(observer);
        }
    }

    private void FlushOneFireCycleBatch()
    {
        if (_fireCycleDirty.Count == 0)
            return;
        ClientSession recipient = _fireCycleDirty.Dequeue();
        _fireCycleDirtySet.Remove(recipient);
        if (!_fireCycleQueues.TryGetValue(recipient, out List<CombatFireCycleWire> cycles) || cycles.Count == 0)
            return;
        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null)
        {
            cycles.Clear();
            return;
        }

        int count = Math.Min(cycles.Count, PlayerCombatFireCycleBatchMessage.MaximumCycles);
        _writer.Reset();
        _writer.PutPackedUShort(PlayerGameplayActionMessageTypes.CombatFireCycleBatch);
        _writer.Put((byte)count);
        for (int i = 0; i < count; ++i)
            cycles[i].Serialize(_writer);
        int bytes = _writer.Length;
        _wireFireCycleBatches++;
        _wireFireCycleRecords += count;
        _wireFireCycleBytes += bytes;
        TrackGameplayWire(PlayerGameplayActionMessageTypes.CombatFireCycleBatch, bytes);
        Send(recipient, _writer, DeliveryMethod.Unreliable, OutboundPriority.High, OutboundFamily.Combat);
        cycles.Clear();
    }

    private static float ResolveCycleSpan(in FirearmActionProfile profile, byte roundsFired, float combatTickRate)
    {
        if (roundsFired <= 1)
            return 0.02f;
        float rps = Math.Max(FirearmCadenceTiming.MinimumRoundsPerSecond, profile.RoundsPerSecond);
        float authored = roundsFired / rps;
        float combatCycle = combatTickRate > 0f ? 1f / combatTickRate : 0.1f;
        return Math.Max(0.02f, Math.Min(5f, Math.Max(authored, profile.FireMode == FirearmFireMode.Burst ? authored : combatCycle)));
    }

    private void StopAutomaticFireForSession(ClientSession session, bool sendOwnerCorrection)
    {
        if (session != null && _firearmActionStates.TryGetValue(session, out FirearmActionState state) && state.AutomaticActive)
            StopAutomaticFire(session, state, sendOwnerCorrection);
    }

    private void RemoveFirearmActionState(ClientSession session)
    {
        if (session == null)
            return;
        if (_firearmActionStates.TryGetValue(session, out FirearmActionState state))
            SetAutomaticFireActive(state, false);
        _firearmActionStates.Remove(session);
        _fireCycleQueues.Remove(session);
        _fireCycleDirtySet.Remove(session);
    }
}
