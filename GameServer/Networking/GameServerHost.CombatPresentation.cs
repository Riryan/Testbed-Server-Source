using Game.GameServer.Runtime;
using Game.Server.Domain.Players;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Protocol;
using Game.Shared.StatusEffects;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Player.Networking;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private sealed class CombatPresentationFlushSystem : ICoreBudgetedWorkSystem
    {
        private readonly GameServerHost _owner;
        public CombatPresentationFlushSystem(GameServerHost owner) => _owner = owner;
        public string Name => "CombatPresentationFlush";
        public bool HasPendingWork => _owner._combatPresentationDirty.Count > 0;
        public void ExecuteOneWorkUnit(in CoreTickContext context) => _owner.FlushOneCombatPresentationBatch();
    }

    private readonly Dictionary<ClientSession, List<CombatPresentationCueWire>> _combatPresentationQueues =
        new Dictionary<ClientSession, List<CombatPresentationCueWire>>();
    private readonly Queue<ClientSession> _combatPresentationDirty = new Queue<ClientSession>();
    private readonly HashSet<ClientSession> _combatPresentationDirtySet = new HashSet<ClientSession>();
    private ICoreScheduledSystemHandle _combatPresentationHandle;
    private long _wireCombatPresentationBatches;
    private long _wireCombatPresentationCues;
    private long _wireCombatPresentationBytes;
    private long _wireCombatPresentationDropped;
    private long _wireVisibleProjectileCues;

    private void InitializeCombatPresentation()
    {
        _runtime.BasicAttacks.Resolved += OnBasicAttackResolved;
        _combatPresentationHandle = _scheduler.RegisterWorkSystem(
            new CombatPresentationFlushSystem(this),
            "Combat",
            maxWorkUnitsPerTick: Math.Min(2048, Math.Max(64, _options.MaxConnections)),
            quarantineable: true,
            invocationLimitMilliseconds: 1.0);
    }

    private void QueueCombatPresentationToObservers(
        ClientSession actor,
        CombatPresentationCueWire cue,
        ClientSession exclude = null)
    {
        if (actor == null || actor.Entity == null || _worldInterest == null ||
            !_worldInterest.TryGetObservers(actor, out HashSet<ClientSession> observers))
            return;

        float maxDistance = Math.Max(1f, Math.Min(_options.AoiRange, _options.CombatPresentationRange));
        float maxDistanceSq = maxDistance * maxDistance;
        foreach (ClientSession observer in observers)
        {
            if (observer == null || ReferenceEquals(observer, actor) || ReferenceEquals(observer, exclude) ||
                !IsCurrent(observer) || !observer.Ready || observer.Entity == null)
                continue;

            float dx = observer.Entity.X - actor.Entity.X;
            float dy = observer.Entity.Y - actor.Entity.Y;
            float dz = observer.Entity.Z - actor.Entity.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxDistanceSq)
                continue;

            QueueCombatPresentation(observer, cue);
        }
    }

    private void QueueCombatPresentation(ClientSession recipient, CombatPresentationCueWire cue)
    {
        if (!_combatPresentationQueues.TryGetValue(recipient, out List<CombatPresentationCueWire> queue))
        {
            queue = new List<CombatPresentationCueWire>(8);
            _combatPresentationQueues.Add(recipient, queue);
        }

        if (queue.Count >= PlayerCombatPresentationBatchMessage.MaxCues)
        {
            _wireCombatPresentationDropped++;
            return;
        }

        queue.Add(cue);
        if (_combatPresentationDirtySet.Add(recipient))
            _combatPresentationDirty.Enqueue(recipient);
    }

    private void FlushOneCombatPresentationBatch()
    {
        if (_combatPresentationDirty.Count == 0)
            return;

        ClientSession recipient = _combatPresentationDirty.Dequeue();
        _combatPresentationDirtySet.Remove(recipient);
        if (!_combatPresentationQueues.TryGetValue(recipient, out List<CombatPresentationCueWire> cues) || cues.Count == 0)
            return;

        if (!IsCurrent(recipient) || !recipient.Ready || recipient.Entity == null)
        {
            cues.Clear();
            return;
        }

        int count = Math.Min(cues.Count, PlayerCombatPresentationBatchMessage.MaxCues);
        _writer.Reset();
        _writer.PutPackedUShort(PlayerGameplayActionMessageTypes.CombatPresentationBatch);
        _writer.Put((byte)count);
        for (int i = 0; i < count; ++i)
            cues[i].Serialize(_writer);

        int bytes = _writer.Length;
        _wireCombatPresentationBatches++;
        _wireCombatPresentationCues += count;
        _wireCombatPresentationBytes += bytes;
        TrackGameplayWire(PlayerGameplayActionMessageTypes.CombatPresentationBatch, bytes);
        Send(recipient, _writer, DeliveryMethod.Unreliable, OutboundPriority.High, OutboundFamily.Combat);
        cues.Clear();
    }

    private void OnBasicAttackResolved(BasicAttackResult result)
    {
        if (!result.Success || result.Mode == BasicAttackMode.Firearm)
            return;

        _mainThreadCompletions.Enqueue(() =>
        {
            ClientSession sourceSession = FindReadySessionByCharacterId(result.SourceCharacterId);
            ClientSession targetSession = FindReadySessionByCharacterId(result.TargetCharacterId);
            if (sourceSession?.Entity == null ||
                !TryGetGameplayRuntime(sourceSession, out PlayerRuntime sourceRuntime))
                return;

            bool hasNetworkTarget = TryGetCombatTargetReference(
                result.TargetCharacterId,
                targetSession,
                out PlayerTargetReferenceWire targetReference);
            CombatDamagePresentationFlags damageFlags = result.Damage.presentationFlags;
            CombatPresentationCueFlags cueFlags = ToCueFlags(damageFlags);
            if (hasNetworkTarget)
                cueFlags |= CombatPresentationCueFlags.HasTarget;

            var cue = new CombatPresentationCueWire
            {
                kind = (byte)CombatPresentationCueKind.Action,
                source = ToTargetReference(sourceSession),
                target = targetReference,
                semanticId = _runtime.BasicAttacks.ResolvePresentationId(sourceRuntime),
                sequence = unchecked((ushort)result.ActionRevision),
                flags = (byte)cueFlags,
            };
            QueueCombatPresentationToObservers(sourceSession, cue);
        });
    }

    private void QueueAbilityPresentation(AbilityCastResult result)
    {
        ClientSession sourceSession = FindReadySessionByCharacterId(result.SourceCharacterId);
        ClientSession targetSession = FindReadySessionByCharacterId(result.TargetCharacterId);
        if (sourceSession?.Entity == null || !_runtime.Content.TryGetAbility(result.AbilityDefinitionId, out AbilityDefinition ability))
            return;
        TryGetCombatTargetReference(result.TargetCharacterId, targetSession, out PlayerTargetReferenceWire targetReference);

        CombatPresentationCueKind cueKind = result.Phase switch
        {
            AbilityPresentationPhase.CastStarted => CombatPresentationCueKind.AbilityStart,
            AbilityPresentationPhase.CastCompleted => CombatPresentationCueKind.AbilityRelease,
            AbilityPresentationPhase.CastCancelled => CombatPresentationCueKind.AbilityCancel,
            _ => CombatPresentationCueKind.AbilityRelease,
        };
        byte flags = targetReference.IsValid ? (byte)CombatPresentationCueFlags.HasTarget : (byte)0;
        var cue = new CombatPresentationCueWire
        {
            kind = (byte)cueKind,
            source = ToTargetReference(sourceSession),
            target = targetReference,
            semanticId = ability.presentationId,
            sequence = unchecked((ushort)result.CastId),
            flags = flags,
        };
        QueueCombatPresentationToObservers(sourceSession, cue);

        if (result.Phase == AbilityPresentationPhase.CastCompleted &&
            ability.deliveryType == Game.Shared.Effects.AbilityDeliveryType.VisibleProjectile &&
            ability.visibleProjectileMode != Game.Shared.Effects.VisibleProjectileMode.None &&
            ability.projectilePresentationId != 0 &&
            targetReference.IsValid)
        {
            var projectile = new CombatPresentationCueWire
            {
                kind = (byte)CombatPresentationCueKind.VisibleProjectile,
                source = ToTargetReference(sourceSession),
                target = targetReference,
                semanticId = ability.projectilePresentationId,
                sequence = unchecked((ushort)result.CastId),
                flags = (byte)CombatPresentationCueFlags.HasTarget,
            };
            _wireVisibleProjectileCues++;
            QueueCombatPresentationToObservers(sourceSession, projectile);
        }
    }

    private void QueueStatusPresentation(PlayerRuntime runtime, StatusEffectChange change, ushort presentationId)
    {
        if (runtime == null || presentationId == 0)
            return;
        ClientSession affected = FindReadySession(runtime);
        if (affected?.Entity == null)
            return;

        bool removed = change.Stacks == 0 || change.Kind == StatusEffectChangeKind.Removed ||
                       change.Kind == StatusEffectChangeKind.Expired ||
                       change.Kind == StatusEffectChangeKind.ClearedOnDeath;
        var cue = new CombatPresentationCueWire
        {
            kind = (byte)(removed ? CombatPresentationCueKind.StatusRemoved : CombatPresentationCueKind.StatusApplied),
            source = ToTargetReference(affected),
            semanticId = presentationId,
            sequence = unchecked((ushort)change.Revision),
            flags = 0,
        };
        QueueCombatPresentationToObservers(affected, cue);
    }

    private PlayerTargetReferenceWire ToTargetReference(long characterId, ClientSession session)
    {
        if (session?.Entity != null)
            return ToTargetReference(session);
        return IsCombatTestDummyCharacter(characterId) && _combatTestDummyEntity != null
            ? new PlayerTargetReferenceWire
            {
                objectId = _combatTestDummyEntity.ObjectId,
                generation = _combatTestDummyEntity.Generation,
            }
            : default;
    }

    private bool TryGetCombatTargetReference(
        long characterId,
        ClientSession session,
        out PlayerTargetReferenceWire reference)
    {
        reference = ToTargetReference(characterId, session);
        return reference.IsValid;
    }

    private static PlayerTargetReferenceWire ToTargetReference(ClientSession session) =>
        session?.Entity == null
            ? default
            : new PlayerTargetReferenceWire
            {
                objectId = session.Entity.ObjectId,
                generation = session.Entity.Generation,
            };

    private void RemoveCombatPresentationState(ClientSession session)
    {
        if (session == null)
            return;
        _combatPresentationQueues.Remove(session);
        _combatPresentationDirtySet.Remove(session);
    }

    private static CombatPresentationCueFlags ToCueFlags(CombatDamagePresentationFlags flags)
    {
        CombatPresentationCueFlags result = CombatPresentationCueFlags.None;
        if ((flags & CombatDamagePresentationFlags.Critical) != 0) result |= CombatPresentationCueFlags.Critical;
        if ((flags & CombatDamagePresentationFlags.Blocked) != 0) result |= CombatPresentationCueFlags.Blocked;
        if ((flags & CombatDamagePresentationFlags.Weakness) != 0) result |= CombatPresentationCueFlags.Weakness;
        if ((flags & CombatDamagePresentationFlags.Resisted) != 0) result |= CombatPresentationCueFlags.Resisted;
        if ((flags & CombatDamagePresentationFlags.Killed) != 0) result |= CombatPresentationCueFlags.Killed;
        if ((flags & CombatDamagePresentationFlags.Immune) != 0) result |= CombatPresentationCueFlags.Immune;
        return result;
    }
}
