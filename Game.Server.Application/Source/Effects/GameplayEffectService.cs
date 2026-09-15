using System;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Players;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Protocol;
using Game.Shared.Resources;
using Game.Shared.StatusEffects;

namespace Game.Server.Application.Effects
{
    /// <summary>
    /// Canonical authoritative executor shared by abilities and periodic statuses.
    /// Content describes effects; this service owns numeric rolls and routes every
    /// mutation through the existing combat/resource/status authorities.
    /// </summary>
    public sealed class GameplayEffectService
    {
        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly CombatService _combat;
        private readonly StatusEffectService _statuses;
        private readonly ICombatRandomSource _random;

        public GameplayEffectService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            CombatService combat,
            StatusEffectService statuses,
            ICombatRandomSource random = null)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
            _random = random ?? new EffectRandomSource();
        }

        public bool CanApply(PlayerRuntime source, PlayerRuntime target, GameplayEffectDefinition effect)
        {
            if (target == null || effect == null)
                return false;
            switch (effect.kind)
            {
                case GameplayEffectKind.Damage:
                case GameplayEffectKind.Heal:
                    return IsAlive(target);
                case GameplayEffectKind.ResourceChange:
                    return effect.resourceId != CharacterResourceId.None &&
                           target.TryGetCharacterResource(effect.resourceId, out _, out var resource) &&
                           resource.Enabled;
                case GameplayEffectKind.ApplyStatusEffect:
                case GameplayEffectKind.RemoveStatusEffect:
                    return !string.IsNullOrWhiteSpace(effect.statusEffectId) &&
                           _content.TryGetStatusEffect(effect.statusEffectId, out _);
                case GameplayEffectKind.StatModifier:
                case GameplayEffectKind.Control:
                    // Persistent stat/control effects are represented by an active Status.
                    return effect.timing == GameplayEffectTiming.WhileActive;
                default:
                    return false;
            }
        }

        public int Apply(
            PlayerRuntime source,
            PlayerRuntime target,
            GameplayEffectDefinition effect,
            int rank,
            double now,
            StatusEffectChangeReason statusReason = StatusEffectChangeReason.Ability,
            CombatDamageCause damageCause = CombatDamageCause.Combat,
            int stackMultiplier = 1)
        {
            if (!CanApply(source, target, effect) || !IsValidTime(now))
                return 0;
            if (effect.chance < 1f && _random.NextUnit() >= Math.Max(0d, effect.chance))
                return 0;

            int stacks = Math.Max(1, stackMultiplier);
            float minimum = ResolveValue(source, effect.minValue, effect.minValuePerRank, effect, rank) * stacks;
            float maximum = ResolveValue(source, effect.maxValue, effect.maxValuePerRank, effect, rank) * stacks;
            if (maximum < minimum)
            {
                float swap = minimum;
                minimum = maximum;
                maximum = swap;
            }

            switch (effect.kind)
            {
                case GameplayEffectKind.Damage:
                {
                    var result = _combat.ApplyDamageRange(
                        source,
                        target,
                        minimum,
                        maximum,
                        effect.damageTypeId,
                        effect.flags,
                        effect.critChanceModifier,
                        effect.critMultiplierModifier,
                        effect.armorPenetrationFlat,
                        effect.armorPenetrationPercent,
                        now,
                        damageCause,
                        CombatService.StandardDamagePolicy);
                    return result.dealtAmount > 0 || result.resultCode == CombatDamageResultCode.Blocked ? 1 : 0;
                }
                case GameplayEffectKind.Heal:
                {
                    bool canCrit = (effect.flags & GameplayEffectFlags.CanCrit) != 0;
                    var result = _combat.ApplyHealingRange(
                        source,
                        target,
                        minimum,
                        maximum,
                        now,
                        canCrit,
                        effect.critChanceModifier,
                        effect.critMultiplierModifier);
                    return result.appliedAmount > 0 ? 1 : 0;
                }
                case GameplayEffectKind.ResourceChange:
                {
                    int amount = RollToInt(minimum, maximum);
                    if (amount <= 0) return 0;
                    CharacterResourceOperationResult result;
                    switch (effect.resourceOperation)
                    {
                        case ResourceChangeOperation.Subtract:
                            result = _resources.Remove(target, effect.resourceId, amount, CharacterResourceChangeReason.AbilityCost);
                            break;
                        case ResourceChangeOperation.Set:
                            result = _resources.Set(target, effect.resourceId, amount, CharacterResourceChangeReason.Healing);
                            break;
                        default:
                            result = _resources.Add(target, effect.resourceId, amount, CharacterResourceChangeReason.Healing);
                            break;
                    }
                    return result.Success ? 1 : 0;
                }
                case GameplayEffectKind.ApplyStatusEffect:
                {
                    int requestedStatusStacks = Math.Max(1, effect.stacks);
                    var result = _statuses.Apply(
                        target,
                        effect.statusEffectId,
                        source?.CharacterId.Value ?? 0L,
                        requestedStatusStacks,
                        now,
                        statusReason);
                    return result.Success ? 1 : 0;
                }
                case GameplayEffectKind.RemoveStatusEffect:
                {
                    var result = _statuses.Remove(target, effect.statusEffectId, statusReason);
                    return result.Success ? 1 : 0;
                }
                case GameplayEffectKind.StatModifier:
                case GameplayEffectKind.Control:
                    // Their authority is the active StatusEffectDefinition; no direct one-shot mutation.
                    return 1;
                default:
                    return 0;
            }
        }

        private float ResolveValue(
            PlayerRuntime source,
            float baseValue,
            float perRank,
            GameplayEffectDefinition effect,
            int rank)
        {
            double value = baseValue + perRank * Math.Max(0, rank - 1);
            if (source != null && !string.IsNullOrWhiteSpace(effect.casterStatId) && effect.casterStatScale != 0f)
                value += source.GetStat(effect.casterStatId, 0f) * effect.casterStatScale;
            if (double.IsNaN(value) || value <= 0d) return 0f;
            if (double.IsInfinity(value) || value >= float.MaxValue) return float.MaxValue;
            return (float)value;
        }

        private int RollToInt(float minimum, float maximum)
        {
            double value = minimum + (Math.Max(minimum, maximum) - minimum) * _random.NextUnit();
            if (double.IsNaN(value) || value <= 0d) return 0;
            if (double.IsInfinity(value) || value >= int.MaxValue) return int.MaxValue;
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null &&
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out var health) &&
            health.Enabled && health.Current > health.Minimum;

        private static bool IsValidTime(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private sealed class EffectRandomSource : ICombatRandomSource
        {
            private readonly object _gate = new object();
            private readonly Random _random = new Random();
            public double NextUnit() { lock (_gate) return _random.NextDouble(); }
        }
    }
}
