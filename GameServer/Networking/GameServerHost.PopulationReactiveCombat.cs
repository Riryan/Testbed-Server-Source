using System;
using System.Collections.Generic;
using Game.GameServer.Runtime;
using Game.Server.Application.Abilities;
using Game.Server.Application.Combat;
using Game.Server.Application.Population;
using Game.Server.Domain.Characters;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Protocol;
using Player.Networking;
using Player.Shared;

namespace Game.GameServer.Networking;

internal sealed partial class GameServerHost
{
    private void InitializePopulationReactiveCombat()
    {
        _runtime.Combat.DamageResolved += OnPopulationReactiveDamageResolved;
        _runtime.Population.FightIntent += OnPopulationFightIntent;
    }

    private void DisposePopulationReactiveCombat()
    {
        _runtime.Combat.DamageResolved -= OnPopulationReactiveDamageResolved;
        _runtime.Population.FightIntent -= OnPopulationFightIntent;
    }

    /// <summary>
    /// A valid authoritative hostile contact wakes Population even when HP damage resolves to
    /// zero because of block/immunity/invincibility. Requests rejected before CombatService
    /// resolution never arrive here and therefore cannot manufacture threat.
    /// </summary>
    private void OnPopulationReactiveDamageResolved(CombatDamageResult result)
    {
        if (!IsReactivePopulationContact(result))
            return;

        if (!_runtime.PopulationCombat.TryGetPopulationByCharacterId(
                result.targetCharacterId,
                out PopulationActorRuntime population) ||
            population?.Actor == null ||
            !population.Actor.Alive)
        {
            return;
        }

        // V1 is explicitly player-reactive. Population does not proactively aggro or create
        // Population-vs-Population threat from this path.
        ClientSession attackerSession = FindIndexedReadySessionByCharacterId(result.sourceCharacterId);
        if (attackerSession?.Entity == null ||
            !TryGetGameplayRuntime(attackerSession, out PlayerRuntime attacker))
        {
            return;
        }

        CharacterLocationState attackerLocation = attackerSession.Entity.CaptureLocation();
        if (!string.Equals(attackerLocation.MapId, population.Actor.MapId, StringComparison.Ordinal) ||
            !string.Equals(attackerLocation.InstanceId, population.Actor.InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        if (!_runtime.PopulationCombat.TryActivatePopulation(population, out PlayerRuntime populationRuntime))
            return;

        double now = _scheduler.ServerTime;
        CombatOwnerStateSnapshot attackState = _runtime.CombatLoadout.Capture(populationRuntime, now);
        float engageRange = attackState.Available && attackState.BasicAttackRange > 0f
            ? attackState.BasicAttackRange
            : CombatRangePolicy.UnarmedRange;

        int healthMaximum = Math.Max(1, population.Actor.HealthMaximum);
        int hostileMagnitude = Math.Max(result.rawAmount, result.dealtAmount);
        float severity = Math.Clamp(
            hostileMagnitude > 0 ? (float)hostileMagnitude / healthMaximum : 0.15f,
            0.15f,
            1f);

        _runtime.Population.NotifyThreat(
            population.Actor.Handle.actorId,
            attacker.CharacterId.Value,
            attackerLocation.Position,
            severity,
            engageRange,
            now);
    }

    private static bool IsReactivePopulationContact(CombatDamageResult result)
    {
        if (result.sourceCharacterId <= 0 || result.targetCharacterId <= 0)
            return false;

        return result.resultCode == CombatDamageResultCode.Applied ||
               result.resultCode == CombatDamageResultCode.Blocked ||
               result.resultCode == CombatDamageResultCode.Killed ||
               result.resultCode == CombatDamageResultCode.RejectedNoEffectiveDamage ||
               result.resultCode == CombatDamageResultCode.RejectedInvincible;
    }

    /// <summary>
    /// PopulationSimulationService owns chase/flee/leash/LOS. It raises FightIntent only when
    /// a fighting actor is in authoritative attack position; the existing BasicAttackService
    /// remains the sole cadence/resource/range/damage authority.
    /// </summary>
    private void OnPopulationFightIntent(PopulationActorRuntime population)
    {
        if (population?.Actor == null ||
            !population.Actor.Alive ||
            population.AiState != Game.Shared.Population.PopulationAiState.Fighting ||
            !_runtime.Population.TryGetReactiveThreatTarget(population, out long targetCharacterId))
        {
            return;
        }

        double now = _scheduler.ServerTime;
        ClientSession targetSession = FindIndexedReadySessionByCharacterId(targetCharacterId);
        if (targetSession?.Entity == null ||
            targetSession.Entity.IsDead ||
            !TryGetGameplayRuntime(targetSession, out PlayerRuntime target))
        {
            _runtime.Population.CancelReactiveThreat(population.Actor.Handle.actorId, now);
            return;
        }

        CharacterLocationState targetLocation = targetSession.Entity.CaptureLocation();
        if (!string.Equals(targetLocation.MapId, population.Actor.MapId, StringComparison.Ordinal) ||
            !string.Equals(targetLocation.InstanceId, population.Actor.InstanceId, StringComparison.Ordinal))
        {
            _runtime.Population.CancelReactiveThreat(population.Actor.Handle.actorId, now);
            return;
        }

        if (!_runtime.PopulationCombat.TryActivatePopulation(population, out PlayerRuntime source))
        {
            _runtime.Population.CancelReactiveThreat(population.Actor.Handle.actorId, now);
            return;
        }

        // Cheap preflight avoids publishing predictable recovery rejections at the engaged AI
        // cadence. BasicAttackService still independently verifies every authoritative rule.
        var actionState = source.CaptureActionState();
        if (actionState.ActiveCast.IsActive || now + 0.000001d < actionState.BasicAttackRecoveryEnd)
            return;

        CombatOwnerStateSnapshot ownerState = _runtime.CombatLoadout.Capture(source, now);
        BasicAttackResult result;

        if (ownerState.Available && ownerState.Mode == BasicAttackMode.Firearm)
        {
            // Clean-server suppression: never call the attack authority with a locally-known
            // empty Population magazine. Police refill from infinite transient reserve; every
            // other ranged Population falls back to the already-proven unarmed path.
            if (ownerState.LoadedRounds <= 0)
            {
                if (_runtime.PopulationCombat.TryRefillPoliceInfiniteReserve(source))
                    return;

                if (_runtime.PopulationCombat.TryFallbackEmptyPopulationFirearmToUnarmed(source))
                {
                    _runtime.CombatLoadout.ResetForEquipmentChange(source);
                    CombatOwnerStateSnapshot fallback = _runtime.CombatLoadout.Capture(source, now);
                    _runtime.Population.UpdateReactiveThreatEngageRange(
                        population.Actor.Handle.actorId,
                        fallback.BasicAttackRange > 0f
                            ? fallback.BasicAttackRange
                            : CombatRangePolicy.UnarmedRange,
                        now);
                }
                return;
            }

            FirearmActionResolution firearm = _runtime.BasicAttacks.TryFirearmAction(
                source,
                target,
                BasicAttackInputKind.Primary,
                1,
                0f,
                aiming: true,
                enforceRecovery: true,
                now: now,
                damageCause: CombatDamageCause.PopulationBasicAttack);
            result = firearm.Action;
            if (firearm.Success && firearm.RoundsFired > 0)
                QueuePopulationFireCycle(population, firearm);
        }
        else
        {
            result = _runtime.BasicAttacks.TryAttack(
                source,
                target,
                BasicAttackInputKind.Primary,
                now,
                CombatDamageCause.PopulationBasicAttack);
        }

        if (result.Code == BasicAttackResultCode.RejectedDead ||
            result.Code == BasicAttackResultCode.RejectedDifferentWorld ||
            result.Code == BasicAttackResultCode.RejectedInvalidTarget)
        {
            _runtime.Population.CancelReactiveThreat(population.Actor.Handle.actorId, now);
        }
    }

    private void QueuePopulationFireCycle(
        PopulationActorRuntime population,
        in FirearmActionResolution resolution)
    {
        if (population?.Actor == null ||
            !_populationPresentations.TryGetValue(
                population.Actor.Handle.actorId,
                out PopulationPresentationState state) ||
            state?.Entity == null ||
            resolution.RoundsFired <= 0)
        {
            return;
        }

        byte sequence = unchecked((byte)resolution.Action.ActionRevision);
        CombatFireCycleWire cycle = CombatFireCycleWire.Create(
            state.Entity.ObjectId,
            state.Entity.Generation,
            resolution.Profile.PresentationId,
            sequence,
            resolution.RoundsFired,
            true,
            resolution.Profile.FireMode,
            ResolveCycleSpan(
                resolution.Profile,
                resolution.RoundsFired,
                (float)_options.CombatTickRate));

        float maxDistance = Math.Max(
            1f,
            Math.Min(_options.AoiRange, _options.CombatPresentationRange));
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (ClientSession observer in state.Observers)
        {
            if (observer?.Entity == null || !IsCurrent(observer) || !observer.Ready)
                continue;

            float dx = observer.Entity.X - state.Entity.X;
            float dy = observer.Entity.Y - state.Entity.Y;
            float dz = observer.Entity.Z - state.Entity.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxDistanceSq)
                continue;

            if (!_fireCycleQueues.TryGetValue(observer, out List<CombatFireCycleWire> queue))
            {
                queue = new List<CombatFireCycleWire>(8);
                _fireCycleQueues.Add(observer, queue);
            }
            if (queue.Count >= PlayerCombatFireCycleBatchMessage.MaximumCycles)
                continue;

            queue.Add(cycle);
            if (_fireCycleDirtySet.Add(observer))
                _fireCycleDirty.Enqueue(observer);
        }
    }

    private bool TryGetPopulationPresentationReference(
        long characterId,
        out PlayerTargetReferenceWire reference)
    {
        reference = default;
        if (!_runtime.PopulationCombat.TryGetPopulationByCharacterId(
                characterId,
                out PopulationActorRuntime population) ||
            population?.Actor == null ||
            !_populationPresentations.TryGetValue(
                population.Actor.Handle.actorId,
                out PopulationPresentationState state) ||
            state?.Entity == null)
        {
            return false;
        }

        reference = new PlayerTargetReferenceWire
        {
            objectId = state.Entity.ObjectId,
            generation = state.Entity.Generation,
        };
        return reference.IsValid;
    }

    private bool TryQueuePopulationBasicAttackPresentation(
        BasicAttackResult result,
        ClientSession targetSession)
    {
        if (!_runtime.PopulationCombat.TryGetPopulationByCharacterId(
                result.SourceCharacterId,
                out PopulationActorRuntime population) ||
            population?.Actor == null ||
            !_populationPresentations.TryGetValue(
                population.Actor.Handle.actorId,
                out PopulationPresentationState state) ||
            state?.Entity == null)
        {
            return false;
        }

        bool hasTarget = targetSession?.Entity != null;
        PlayerTargetReferenceWire targetReference = hasTarget
            ? ToTargetReference(targetSession)
            : default;

        CombatPresentationCueFlags cueFlags = ToCueFlags(result.Damage.presentationFlags);
        if (hasTarget)
            cueFlags |= CombatPresentationCueFlags.HasTarget;

        var cue = new CombatPresentationCueWire
        {
            kind = (byte)CombatPresentationCueKind.Action,
            source = new PlayerTargetReferenceWire
            {
                objectId = state.Entity.ObjectId,
                generation = state.Entity.Generation,
            },
            target = targetReference,
            semanticId = _runtime.BasicAttacks.ResolvePresentationId(population.CombatRuntime),
            sequence = unchecked((ushort)result.ActionRevision),
            flags = (byte)cueFlags,
        };

        float maxDistance = Math.Max(
            1f,
            Math.Min(_options.AoiRange, _options.CombatPresentationRange));
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (ClientSession observer in state.Observers)
        {
            if (observer?.Entity == null || !IsCurrent(observer) || !observer.Ready)
                continue;

            float dx = observer.Entity.X - state.Entity.X;
            float dy = observer.Entity.Y - state.Entity.Y;
            float dz = observer.Entity.Z - state.Entity.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) > maxDistanceSq)
                continue;

            QueueCombatPresentation(observer, cue);
        }

        return true;
    }
}
