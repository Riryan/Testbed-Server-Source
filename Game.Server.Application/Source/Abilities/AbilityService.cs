using System;
using System.Threading;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Effects;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Actions;
using Game.Server.Domain.Players;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Abilities;
using Game.Shared.Actors;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;
using Game.Shared.World;

namespace Game.Server.Application.Abilities
{
    /// <summary>
    /// Portable authoritative ability execution core adapted from uMMORPG. It owns
    /// validation, resource spending, effects and cooldown state but no Unity/Mirror
    /// target discovery. Timed cast completion is scheduled by the integration layer.
    /// </summary>
    public sealed class AbilityService
    {
        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly CombatService _combat;
        private readonly StatusEffectService _statuses;
        private readonly GameplayEffectService _effects;
        private long _nextCastId;

        public event Action<AbilityCastResult> CastStateChanged;

        public AbilityService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            CombatService combat,
            StatusEffectService statuses,
            GameplayEffectService effects = null)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
            _effects = effects ?? new GameplayEffectService(_content, _resources, _combat, _statuses);
        }

        public AbilityCastResult TryBeginCast(
            PlayerRuntime source,
            PlayerRuntime target,
            ushort abilityWireId,
            int rank,
            WorldPosition requestedPoint,
            double now)
        {
            if (!_content.TryGetAbility(abilityWireId, out AbilityDefinition ability))
                return Publish(Failed(AbilityCastFailure.UnknownAbility, string.Empty, source, target));
            return TryBeginCast(source, target, ability.definitionId, rank, requestedPoint, now);
        }

        public AbilityCastResult TryBeginCast(
            PlayerRuntime source,
            PlayerRuntime target,
            string abilityDefinitionId,
            int rank,
            WorldPosition requestedPoint,
            double now)
        {
            rank = Math.Max(1, rank);
            AbilityCastFailure failure = ValidateRequest(
                source,
                target,
                abilityDefinitionId,
                now,
                out AbilityDefinition ability,
                out PlayerRuntime resolvedTarget);
            if (failure != AbilityCastFailure.None)
                return Publish(Failed(failure, abilityDefinitionId, source, target));

            if (!ValidateEffects(source, resolvedTarget, ability, rank))
                return Publish(Failed(AbilityCastFailure.EffectValidationFailed, abilityDefinitionId, source, resolvedTarget));

            if (ability.castTimeSeconds <= 0f)
                return ExecuteImmediate(source, resolvedTarget, ability, rank, now, 0L);

            CharacterActionState state = source.CaptureActionState();
            if (state.ActiveCast.IsActive)
                return Publish(Failed(AbilityCastFailure.AlreadyCasting, abilityDefinitionId, source, resolvedTarget));

            long castId = Interlocked.Increment(ref _nextCastId);
            double completesAt = now + Math.Max(0d, ability.castTimeSeconds);
            var cast = new ActiveAbilityCastState(
                castId,
                ability.definitionId,
                rank,
                resolvedTarget?.CharacterId.Value ?? 0L,
                requestedPoint,
                now,
                completesAt);
            CharacterActionState next = state.WithActiveCast(cast);
            if (!source.TryCommitActionState(
                    state.Revision,
                    next,
                    CharacterActionChangeReason.AbilityCastStarted,
                    ability.definitionId,
                    castId,
                    completesAt))
            {
                return Publish(Failed(AbilityCastFailure.InvalidState, abilityDefinitionId, source, resolvedTarget));
            }

            return Publish(new AbilityCastResult(
                AbilityCastFailure.None,
                AbilityPresentationPhase.CastStarted,
                castId,
                ability.definitionId,
                source.CharacterId.Value,
                resolvedTarget?.CharacterId.Value ?? 0L,
                next.Revision,
                completesAt,
                0));
        }

        public AbilityCastResult TryCompleteCast(
            PlayerRuntime source,
            PlayerRuntime target,
            long castId,
            double now)
        {
            if (source == null || castId <= 0 || !IsFiniteTime(now))
                return Publish(Failed(AbilityCastFailure.InvalidState, string.Empty, source, target));

            CharacterActionState state = source.CaptureActionState();
            ActiveAbilityCastState cast = state.ActiveCast;
            if (!cast.IsActive || cast.CastId != castId)
                return Publish(Failed(AbilityCastFailure.Interrupted, cast.AbilityDefinitionId, source, target));
            if (now + 0.0001d < cast.CompletesAt)
                return Publish(Failed(AbilityCastFailure.InvalidState, cast.AbilityDefinitionId, source, target));

            AbilityCastFailure failure = ValidateRequest(
                source,
                target,
                cast.AbilityDefinitionId,
                now,
                out AbilityDefinition ability,
                out PlayerRuntime resolvedTarget,
                ignoreActiveCast: true);
            if (failure != AbilityCastFailure.None ||
                !ValidateEffects(source, resolvedTarget, ability, cast.Rank))
            {
                return CancelCastInternal(
                    source,
                    cast,
                    failure == AbilityCastFailure.None ? AbilityCastFailure.EffectValidationFailed : failure);
            }

            return ExecuteImmediate(source, resolvedTarget, ability, cast.Rank, now, castId);
        }

        public AbilityCastResult CancelCast(PlayerRuntime source, AbilityCastFailure failure = AbilityCastFailure.Interrupted)
        {
            if (source == null)
                return Publish(Failed(AbilityCastFailure.InvalidState, string.Empty, source, null));
            CharacterActionState state = source.CaptureActionState();
            if (!state.ActiveCast.IsActive)
                return Publish(Failed(AbilityCastFailure.InvalidState, string.Empty, source, null));
            return CancelCastInternal(source, state.ActiveCast, failure);
        }

        private AbilityCastResult ExecuteImmediate(
            PlayerRuntime source,
            PlayerRuntime target,
            AbilityDefinition ability,
            int rank,
            double now,
            long castId)
        {
            if (ability.resourceCost > 0)
            {
                CharacterResourceOperationResult spend = _resources.Spend(
                    source,
                    ability.resourceId,
                    ability.resourceCost,
                    CharacterResourceChangeReason.AbilityCost);
                if (!spend.Success)
                {
                    if (castId > 0)
                        return CancelCastInternal(
                            source,
                            source.CaptureActionState().ActiveCast,
                            AbilityCastFailure.InsufficientResource);
                    return Publish(Failed(AbilityCastFailure.InsufficientResource, ability.definitionId, source, target));
                }
            }

            int affected = 0;
            GameplayEffectDefinition[] effects = ability.effects ?? Array.Empty<GameplayEffectDefinition>();
            for (int i = 0; i < effects.Length; ++i)
            {
                GameplayEffectDefinition effect = effects[i];
                if (effect == null || effect.timing != GameplayEffectTiming.Instant)
                    continue;
                affected += _effects.Apply(
                    source,
                    target,
                    effect,
                    rank,
                    now,
                    StatusEffectChangeReason.Ability,
                    Game.Shared.Combat.CombatDamageCause.Combat);
            }

            if (affected <= 0)
            {
                if (castId > 0)
                    return CancelCastInternal(source, source.CaptureActionState().ActiveCast, AbilityCastFailure.EffectValidationFailed);
                return Publish(Failed(AbilityCastFailure.EffectValidationFailed, ability.definitionId, source, target));
            }

            CharacterActionState state = source.CaptureActionState();
            double cooldownEnd = ResolveCooldownEnd(ability, state, now);
            bool sharedBasicRecovery = ability.cooldownPolicy == AbilityCooldownPolicy.ActorBasicAttackRate;
            CharacterActionState next;

            if (ability.cooldownPolicy == AbilityCooldownPolicy.None && !state.ActiveCast.IsActive)
            {
                next = state;
            }
            else
            {
                next = state.ResolveAbility(ability.definitionId, cooldownEnd, sharedBasicRecovery);
                CharacterActionChangeReason reason = castId > 0
                    ? CharacterActionChangeReason.AbilityCastCompleted
                    : CharacterActionChangeReason.AbilityCooldownCommitted;
                if (!source.TryCommitActionState(
                        state.Revision,
                        next,
                        reason,
                        ability.definitionId,
                        castId,
                        cooldownEnd))
                {
                    return Publish(Failed(AbilityCastFailure.InvalidState, ability.definitionId, source, target));
                }
            }

            return Publish(new AbilityCastResult(
                AbilityCastFailure.None,
                AbilityPresentationPhase.CastCompleted,
                castId,
                ability.definitionId,
                source.CharacterId.Value,
                target?.CharacterId.Value ?? 0L,
                next.Revision,
                0d,
                affected));
        }

        private AbilityCastResult CancelCastInternal(
            PlayerRuntime source,
            ActiveAbilityCastState cast,
            AbilityCastFailure failure)
        {
            if (source == null || !cast.IsActive)
                return Publish(Failed(failure, cast.AbilityDefinitionId, source, null));

            CharacterActionState state = source.CaptureActionState();
            if (!state.ActiveCast.IsActive || state.ActiveCast.CastId != cast.CastId)
                return Publish(Failed(failure, cast.AbilityDefinitionId, source, null));

            CharacterActionState next = state.ClearActiveCast();
            if (!source.TryCommitActionState(
                    state.Revision,
                    next,
                    CharacterActionChangeReason.AbilityCastCancelled,
                    cast.AbilityDefinitionId,
                    cast.CastId,
                    0d))
            {
                return Publish(Failed(AbilityCastFailure.InvalidState, cast.AbilityDefinitionId, source, null));
            }

            return Publish(new AbilityCastResult(
                failure,
                AbilityPresentationPhase.CastCancelled,
                cast.CastId,
                cast.AbilityDefinitionId,
                source.CharacterId.Value,
                cast.TargetCharacterId,
                next.Revision,
                0d,
                0));
        }

        private AbilityCastFailure ValidateRequest(
            PlayerRuntime source,
            PlayerRuntime target,
            string abilityDefinitionId,
            double now,
            out AbilityDefinition ability,
            out PlayerRuntime resolvedTarget,
            bool ignoreActiveCast = false)
        {
            ability = null;
            resolvedTarget = target;
            if (source == null || !IsFiniteTime(now) || !IsAlive(source))
                return AbilityCastFailure.InvalidState;
            if (!_content.TryGetAbility(abilityDefinitionId, out ability))
                return AbilityCastFailure.UnknownAbility;
            if ((ability.allowedActors & GameplayActorAccessMask.Player) == 0)
                return AbilityCastFailure.ActorRestricted;

            CharacterStatusEffectsState statusState = source.CaptureStatusEffects();
            if (statusState.HasControl(ControlEffectType.Stun) || statusState.HasControl(ControlEffectType.Silence))
                return AbilityCastFailure.Controlled;

            CharacterActionState action = source.CaptureActionState();
            if (!ignoreActiveCast && action.ActiveCast.IsActive)
                return AbilityCastFailure.AlreadyCasting;

            double cooldownEnd = ability.cooldownPolicy == AbilityCooldownPolicy.ActorBasicAttackRate
                ? action.BasicAttackRecoveryEnd
                : ability.cooldownPolicy == AbilityCooldownPolicy.None
                    ? 0d
                    : action.GetCooldownEnd(ability.definitionId);
            if (now < cooldownEnd)
                return AbilityCastFailure.OnCooldown;

            if (ability.resourceCost > 0)
            {
                if (!source.TryGetCharacterResource(ability.resourceId, out _, out var resource) ||
                    !resource.Enabled || resource.Current - ability.resourceCost < resource.Minimum)
                    return AbilityCastFailure.InsufficientResource;
            }

            if (ability.movementPolicy == AbilityMovementPolicy.RequireMoving)
                return AbilityCastFailure.MovementRequired;
            if (ability.movementPolicy == AbilityMovementPolicy.RequireSprinting)
                return AbilityCastFailure.SprintingRequired;

            if (!MeetsWeaponRequirement(source, ability.requiredWeaponTag))
                return AbilityCastFailure.WeaponRequirement;

            switch (ability.targetMode)
            {
                case AbilityTargetMode.Self:
                    resolvedTarget = source;
                    break;
                case AbilityTargetMode.Entity:
                    if (resolvedTarget == null)
                        return AbilityCastFailure.InvalidTarget;
                    break;
                case AbilityTargetMode.Point:
                    // Point/movement effects are intentionally not executed until the
                    // portable world-query/movement authority phase is migrated.
                    return AbilityCastFailure.InvalidTarget;
                default:
                    return AbilityCastFailure.InvalidTarget;
            }

            if (!IsAlive(resolvedTarget))
                return AbilityCastFailure.InvalidTarget;
            if (!SameWorld(source, resolvedTarget))
                return AbilityCastFailure.InvalidTarget;
            if (ability.range > 0f && !WithinRange(source, resolvedTarget, ability.range))
                return AbilityCastFailure.OutOfRange;

            switch (ability.targetRelation)
            {
                case AbilityTargetRelation.Self:
                    if (!ReferenceEquals(source, resolvedTarget))
                        return AbilityCastFailure.InvalidTarget;
                    break;
                case AbilityTargetRelation.AnyLiving:
                    break;
                case AbilityTargetRelation.Friendly:
                case AbilityTargetRelation.Hostile:
                    // Relationship/faction/group ownership has not been migrated yet.
                    // Fail closed instead of treating every player as friend or enemy.
                    return AbilityCastFailure.UnsupportedTargetRelation;
            }

            return AbilityCastFailure.None;
        }

        private bool ValidateEffects(PlayerRuntime source, PlayerRuntime target, AbilityDefinition ability, int rank)
        {
            if (source == null || target == null || ability == null)
                return false;

            GameplayEffectDefinition[] effects = ability.effects ?? Array.Empty<GameplayEffectDefinition>();
            if (effects.Length == 0)
                return false;

            bool hasExecutableEffect = false;
            for (int i = 0; i < effects.Length; ++i)
            {
                GameplayEffectDefinition effect = effects[i];
                if (effect == null)
                    return false;

                // Periodic/while-active mechanics belong inside an applied Status definition.
                if (effect.timing != GameplayEffectTiming.Instant)
                    return false;

                if (!_effects.CanApply(source, target, effect))
                    return false;
                hasExecutableEffect = true;
            }
            return hasExecutableEffect;
        }

        private double ResolveCooldownEnd(AbilityDefinition ability, CharacterActionState state, double now)
        {
            switch (ability.cooldownPolicy)
            {
                case AbilityCooldownPolicy.ActorBasicAttackRate:
                    return now + ResolveBasicAttackInterval();
                case AbilityCooldownPolicy.None:
                    return 0d;
                default:
                    return now + Math.Max(0d, ability.cooldownSeconds);
            }
        }

        private double ResolveBasicAttackInterval()
        {
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            return BasicAttackCadenceTiming.Clamp(rules.basicAttackInterval);
        }

        private bool MeetsWeaponRequirement(PlayerRuntime source, string requiredWeaponTag)
        {
            if (string.IsNullOrWhiteSpace(requiredWeaponTag))
                return true;
            var systems = source.CapturePlayerItemSystems();
            if (systems == null)
                return false;
            var mainHand = systems.Equipment.Get("MainHand");
            if (mainHand == null || !_content.TryGetItem(mainHand.DefinitionId, out ItemDefinition item))
                return false;
            string[] tags = item.tags ?? Array.Empty<string>();
            for (int i = 0; i < tags.Length; ++i)
                if (string.Equals(tags[i], requiredWeaponTag, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null &&
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
            health.Enabled && health.Current > health.Minimum;

        private static bool SameWorld(PlayerRuntime a, PlayerRuntime b)
        {
            var left = a.Location;
            var right = b.Location;
            return string.Equals(left.MapId, right.MapId, StringComparison.Ordinal) &&
                   string.Equals(left.InstanceId, right.InstanceId, StringComparison.Ordinal);
        }

        private static bool WithinRange(PlayerRuntime a, PlayerRuntime b, float range)
        {
            var p = a.Location.Position;
            var q = b.Location.Position;
            double dx = p.X - q.X;
            double dy = p.Y - q.Y;
            double dz = p.Z - q.Z;
            double r = Math.Max(0d, range);
            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        private static bool IsFiniteTime(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private static int RoundToInt(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            if (value >= int.MaxValue) return int.MaxValue;
            if (value <= int.MinValue) return int.MinValue;
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static AbilityCastResult Failed(
            AbilityCastFailure failure,
            string abilityDefinitionId,
            PlayerRuntime source,
            PlayerRuntime target)
        {
            return new AbilityCastResult(
                failure,
                AbilityPresentationPhase.CastCancelled,
                0L,
                abilityDefinitionId,
                source?.CharacterId.Value ?? 0L,
                target?.CharacterId.Value ?? 0L,
                source?.CaptureActionState().Revision ?? 0L,
                0d,
                0);
        }

        private AbilityCastResult Publish(AbilityCastResult result)
        {
            CastStateChanged?.Invoke(result);
            return result;
        }
    }
}
