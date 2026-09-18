using System;
using System.Threading;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Application.StatusEffects;
using Game.Server.Domain.Combat;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Protocol;
using Game.Shared.Resources;

namespace Game.Server.Application.Combat
{
    public interface ICombatRandomSource
    {
        double NextUnit();
    }

    internal sealed class SystemCombatRandomSource : ICombatRandomSource
    {
        private readonly object _gate = new object();
        private readonly Random _random = new Random();

        public double NextUnit()
        {
            lock (_gate) return _random.NextDouble();
        }
    }

    public readonly struct CombatDamageVolleyResult
    {
        public CombatDamageResult Damage { get; }
        public byte RoundsResolved { get; }
        public byte HitCount { get; }
        public float EndingBloom { get; }

        public CombatDamageVolleyResult(CombatDamageResult damage, byte roundsResolved, byte hitCount, float endingBloom)
        {
            Damage = damage;
            RoundsResolved = roundsResolved;
            HitCount = hitCount;
            EndingBloom = endingBloom;
        }
    }

    /// <summary>
    /// Canonical authoritative health damage/healing boundary adapted from uMMORPG.
    /// It mutates Health through CharacterResourceService, so every effective change
    /// automatically emits the revisioned ResourceChanged event. No health polling exists.
    /// </summary>
    public sealed class CombatService
    {
        public const CombatDamagePolicy StandardDamagePolicy =
            CombatDamagePolicy.RespectInvincibility |
            CombatDamagePolicy.ApplyStatusMultipliers |
            CombatDamagePolicy.AllowCritical |
            CombatDamagePolicy.AllowBlock |
            CombatDamagePolicy.ApplyDefense |
            CombatDamagePolicy.UpdateSourceCombatTime |
            CombatDamagePolicy.UpdateTargetCombatTime;

        private readonly GameplayContentCatalog _content;
        private readonly CharacterResourceService _resources;
        private readonly StatusEffectService _statuses;
        private readonly ICombatRandomSource _random;
        private long _nextEventId;

        public event Action<CombatDamageResult> DamageResolved;
        public event Action<CombatHealingResult> HealingResolved;
        public event Action<PlayerRuntime, CombatDamageResult> CharacterKilled;

        public CombatService(
            GameplayContentCatalog content,
            CharacterResourceService resources,
            StatusEffectService statuses,
            ICombatRandomSource random = null)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _statuses = statuses ?? throw new ArgumentNullException(nameof(statuses));
            _random = random ?? new SystemCombatRandomSource();
        }

        public bool IsInCombat(PlayerRuntime runtime, double now)
        {
            if (runtime == null || !IsValidTime(now))
                return false;
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            if (rules.combatStateSeconds <= 0f)
                return false;
            double last = runtime.Combat.LastCombatTime;
            return last > 0d && now < last + rules.combatStateSeconds;
        }

        public double GetCombatExpiry(PlayerRuntime runtime)
        {
            if (runtime == null)
                return 0d;
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            return runtime.Combat.LastCombatTime + Math.Max(0d, rules.combatStateSeconds);
        }

        public CombatDamageResult ApplyStandardDamage(
            PlayerRuntime source,
            PlayerRuntime target,
            int amount,
            double now,
            CombatDamageCause cause = CombatDamageCause.Combat)
        {
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            return ApplyDamageRange(
                source,
                target,
                amount,
                amount,
                rules.unarmedDamageTypeId,
                GameplayEffectFlags.CanCrit | GameplayEffectFlags.CanBlock |
                GameplayEffectFlags.CanBeResisted | GameplayEffectFlags.CanTriggerWeakness,
                0f,
                0f,
                0f,
                0f,
                now,
                cause,
                StandardDamagePolicy);
        }

        public CombatDamageResult ApplyEnvironmentalDamage(
            PlayerRuntime target,
            int amount,
            double now,
            CombatDamageCause cause)
        {
            CombatDamagePolicy policy = CombatDamagePolicy.UpdateTargetCombatTime;
            if (cause == CombatDamageCause.SharedStatus)
            {
                policy |= CombatDamagePolicy.RespectInvincibility |
                          CombatDamagePolicy.ApplyStatusMultipliers |
                          CombatDamagePolicy.ApplyDefense;
            }

            return ApplyDamageRange(
                null,
                target,
                amount,
                amount,
                0,
                GameplayEffectFlags.CanBeResisted | GameplayEffectFlags.CanTriggerWeakness,
                0f,
                0f,
                0f,
                0f,
                now,
                cause,
                policy);
        }

        public CombatDamageResult ApplyGameMasterKill(
            PlayerRuntime source,
            PlayerRuntime target,
            double now)
        {
            return ApplyDamage(
                source,
                target,
                int.MaxValue,
                now,
                CombatDamageCause.GameMaster,
                CombatDamagePolicy.UpdateTargetCombatTime | CombatDamagePolicy.ForceLethal);
        }

        public CombatDamageResult ApplyDamage(
            PlayerRuntime source,
            PlayerRuntime target,
            int amount,
            double now,
            CombatDamageCause cause,
            CombatDamagePolicy policy)
        {
            return ApplyDamageRange(
                source,
                target,
                amount,
                amount,
                0,
                GameplayEffectFlags.CanCrit | GameplayEffectFlags.CanBlock |
                GameplayEffectFlags.CanBeResisted | GameplayEffectFlags.CanTriggerWeakness,
                0f,
                0f,
                0f,
                0f,
                now,
                cause,
                policy);
        }

        /// <summary>
        /// Canonical V1 damage resolver. Every weapon, ammo payload, ability, DOT and
        /// environmental source should enter here rather than implementing its own
        /// damage formula.
        /// </summary>
        public CombatDamageResult ApplyDamageRange(
            PlayerRuntime source,
            PlayerRuntime target,
            float minimum,
            float maximum,
            ushort damageTypeId,
            GameplayEffectFlags effectFlags,
            float criticalChanceModifier,
            float criticalMultiplierModifier,
            float armorPenetrationFlat,
            float armorPenetrationPercent,
            double now,
            CombatDamageCause cause = CombatDamageCause.Combat,
            CombatDamagePolicy policy = StandardDamagePolicy,
            bool publishResult = true)
        {
            long eventId = Interlocked.Increment(ref _nextEventId);
            CombatDamageResult Finish(CombatDamageResult value) => publishResult ? Publish(value) : value;
            long sourceId = source?.CharacterId.Value ?? 0L;
            long targetId = target?.CharacterId.Value ?? 0L;
            float lo = SanitizeNonNegative(minimum);
            float hi = Math.Max(lo, SanitizeNonNegative(maximum));
            int raw = RoundToInt(lo + ((hi - lo) * _random.NextUnit()));

            if (target == null)
                return Finish(new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvalidTarget, cause, sourceId, targetId, raw, 0, 0, 0, CombatDamageType.Normal, false, false, damageTypeId));
            if (raw <= 0 || !IsValidTime(now))
                return Finish(new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvalidAmount, cause, sourceId, targetId, raw, 0, 0, 0, CombatDamageType.Normal, false, false, damageTypeId));
            if (!target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) || !health.Enabled)
                return Finish(new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvalidTarget, cause, sourceId, targetId, raw, 0, 0, 0, CombatDamageType.Normal, false, false, damageTypeId));
            if (health.Current <= health.Minimum)
                return Finish(new CombatDamageResult(eventId, CombatDamageResultCode.RejectedTargetDead, cause, sourceId, targetId, raw, 0, health.Current, health.Current, CombatDamageType.Normal, false, false, damageTypeId));
            if ((policy & CombatDamagePolicy.RespectInvincibility) != 0 && target.Combat.Invincible)
                return Finish(new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvincible, cause, sourceId, targetId, raw, 0, health.Current, health.Current, CombatDamageType.Normal, false, false, damageTypeId));

            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            double resolved = raw;

            if ((policy & CombatDamagePolicy.ApplyStatusMultipliers) != 0)
            {
                if (source != null)
                    resolved *= ClampMultiplier(source.GetStat("DamageDealtMultiplier", 1f));
                resolved *= ClampMultiplier(target.GetStat("DamageTakenMultiplier", 1f));
            }

            bool canCrit = (effectFlags & GameplayEffectFlags.CanCrit) != 0 &&
                           (policy & CombatDamagePolicy.AllowCritical) != 0 &&
                           source != null;
            bool canBlock = (effectFlags & GameplayEffectFlags.CanBlock) != 0 &&
                            (policy & CombatDamagePolicy.AllowBlock) != 0;
            bool canRespond = (effectFlags & GameplayEffectFlags.CanBeResisted) != 0;

            bool critical = false;
            bool blocked = false;
            CombatDamageType resultType = CombatDamageType.Normal;
            CombatDamagePresentationFlags presentationFlags = CombatDamagePresentationFlags.None;

            if (canCrit)
            {
                float baseCrit = source.GetStat("CriticalChance", 0f);
                double critChance = ClampChance(baseCrit + criticalChanceModifier, rules.maximumCriticalChance);
                critical = critChance > 0d && _random.NextUnit() < critChance;
                if (critical)
                {
                    double multiplier = Math.Max(
                        1d,
                        Math.Max(1f, rules.criticalDamageMultiplier) + criticalMultiplierModifier);
                    resolved *= multiplier;
                    resultType = CombatDamageType.Critical;
                    presentationFlags |= CombatDamagePresentationFlags.Critical;
                }
            }

            if (canBlock)
            {
                double blockChance = ClampChance(target.GetStat("BlockChance", 0f), rules.maximumBlockChance);
                blocked = blockChance > 0d && _random.NextUnit() < blockChance;
                if (blocked)
                {
                    resolved *= Clamp01(rules.blockDamageMultiplier);
                    resultType = CombatDamageType.Block;
                    presentationFlags |= CombatDamagePresentationFlags.Blocked;
                }
            }

            if (!blocked && (policy & CombatDamagePolicy.ApplyDefense) != 0)
            {
                string defenseStatId = "Armor";
                if (damageTypeId != 0 &&
                    _content.TryGetDamageType(damageTypeId, out DamageTypeDefinition typed) &&
                    !string.IsNullOrWhiteSpace(typed.defenseStatId))
                {
                    defenseStatId = typed.defenseStatId;
                }

                double defense = Math.Max(0d, target.GetStat(defenseStatId, 0f));
                double penetrationPercent = Clamp01(armorPenetrationPercent);
                defense = Math.Max(0d, (defense * (1d - penetrationPercent)) - Math.Max(0f, armorPenetrationFlat));
                bool hadPositivePreDefenseDamage = resolved > 0d;
                resolved = Math.Max(0d, resolved - defense);

                // Armor/defense is not an immunity mechanic. If a legitimate positive hit
                // reaches this stage, defense may reduce it to the configured floor but may
                // not bypass minimumDamageAfterDefense by landing on exact zero. Explicit
                // block/immunity/damage-response rules still run outside/after this branch
                // and remain able to produce zero effective damage.
                if (hadPositivePreDefenseDamage && rules.minimumDamageAfterDefense > 0)
                    resolved = Math.Max(rules.minimumDamageAfterDefense, resolved);
            }

            if (canRespond)
            {
                DamageResponseAggregate global = _content.GetGlobalDamageResponse(damageTypeId);
                double responseMultiplier = global.Multiplier;
                double responseFlat = global.FlatAdjustment;

                responseMultiplier *= ClampMultiplier(
                    target.GetStat(CombatDerivedStatIds.DamageResponseMultiplier(0), 1f));
                responseFlat += target.GetStat(CombatDerivedStatIds.DamageResponseFlat(0), 0f);

                if (damageTypeId != 0)
                {
                    responseMultiplier *= ClampMultiplier(
                        target.GetStat(CombatDerivedStatIds.DamageResponseMultiplier(damageTypeId), 1f));
                    responseFlat += target.GetStat(CombatDerivedStatIds.DamageResponseFlat(damageTypeId), 0f);
                }

                responseMultiplier = Clamp(
                    responseMultiplier,
                    Math.Max(0f, rules.minimumDamageResponseMultiplier),
                    Math.Max(rules.minimumDamageResponseMultiplier, rules.maximumDamageResponseMultiplier));

                if (responseMultiplier <= 0d)
                {
                    resolved = 0d;
                    presentationFlags |= CombatDamagePresentationFlags.Immune;
                }
                else
                {
                    resolved = Math.Max(0d, (resolved + responseFlat) * responseMultiplier);
                    if ((effectFlags & GameplayEffectFlags.CanTriggerWeakness) != 0 &&
                        responseMultiplier > 1.0001d)
                        presentationFlags |= CombatDamagePresentationFlags.Weakness;
                    else if (responseMultiplier < 0.9999d)
                        presentationFlags |= CombatDamagePresentationFlags.Resisted;
                }
            }

            int resolvedAmount = RoundToInt(resolved);
            if (resolvedAmount > 0 && !blocked)
                resolvedAmount = Math.Max(rules.minimumDamageAfterDefense, resolvedAmount);

            int before = health.Current;
            if ((policy & CombatDamagePolicy.ForceLethal) != 0)
                resolvedAmount = Math.Max(0, before - health.Minimum);

            int dealt = 0;
            if (resolvedAmount > 0)
            {
                CharacterResourceOperationResult mutation = _resources.Remove(
                    target,
                    CharacterResourceId.Health,
                    resolvedAmount,
                    cause == CombatDamageCause.SharedStatus
                        ? CharacterResourceChangeReason.Environmental
                        : CharacterResourceChangeReason.DamageTaken);
                if (mutation.Success &&
                    target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState afterHealth))
                    dealt = Math.Max(0, before - afterHealth.Current);
            }

            target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState finalHealth);
            int after = finalHealth.Current;
            bool killed = before > finalHealth.Minimum && after <= finalHealth.Minimum;
            if (killed)
                presentationFlags |= CombatDamagePresentationFlags.Killed;

            bool validContact = blocked || dealt > 0;
            CombatDamageResultCode code = !validContact
                ? CombatDamageResultCode.RejectedNoEffectiveDamage
                : killed
                    ? CombatDamageResultCode.Killed
                    : blocked
                        ? CombatDamageResultCode.Blocked
                        : CombatDamageResultCode.Applied;

            if (validContact)
            {
                if ((policy & CombatDamagePolicy.UpdateSourceCombatTime) != 0 && source != null)
                    source.MarkCombatActivity(now);
                if ((policy & CombatDamagePolicy.UpdateTargetCombatTime) != 0)
                    target.MarkCombatActivity(now);
            }

            var result = new CombatDamageResult(
                eventId,
                code,
                cause,
                sourceId,
                targetId,
                raw,
                dealt,
                before,
                after,
                resultType,
                killed,
                false,
                damageTypeId,
                presentationFlags);
            Finish(result);

            if (killed)
            {
                _statuses.ClearOnDeath(target);
                _resources.ApplyDeathResets(target);
                CharacterKilled?.Invoke(target, result);
            }
            return result;
        }

        /// <summary>
        /// Resolves multiple firearm rounds independently but emits at most one participant damage event.
        /// Misses are represented in the compact fire-cycle result rather than zero-damage wire events.
        /// </summary>
        public CombatDamageVolleyResult ApplyDamageVolley(
            PlayerRuntime source,
            PlayerRuntime target,
            int attempts,
            float minimum,
            float maximum,
            ushort damageTypeId,
            GameplayEffectFlags effectFlags,
            float armorPenetrationFlat,
            float armorPenetrationPercent,
            float baseHitChance,
            float startingBloom,
            float bloomPerShot,
            float maximumBloom,
            double now,
            CombatDamageCause cause = CombatDamageCause.Combat,
            CombatDamagePolicy policy = StandardDamagePolicy)
        {
            int shotCount = Math.Max(0, Math.Min(31, attempts));
            long sourceId = source?.CharacterId.Value ?? 0L;
            long targetId = target?.CharacterId.Value ?? 0L;
            float bloom = Math.Max(0f, Math.Min(Math.Max(0f, maximumBloom), startingBloom));
            float perShotBloom = Math.Max(0f, bloomPerShot);
            float maxBloom = Math.Max(0f, maximumBloom);
            double hitBase = Math.Max(0d, Math.Min(1d, baseHitChance));
            long eventId = Interlocked.Increment(ref _nextEventId);

            if (target == null || shotCount <= 0 || !IsValidTime(now))
            {
                var rejected = new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvalidTarget, cause,
                    sourceId, targetId, 0, 0, 0, 0, CombatDamageType.Normal, false, false, damageTypeId);
                return new CombatDamageVolleyResult(rejected, (byte)shotCount, 0, bloom);
            }
            if (!target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) || !health.Enabled)
            {
                var rejected = new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvalidTarget, cause,
                    sourceId, targetId, 0, 0, 0, 0, CombatDamageType.Normal, false, false, damageTypeId);
                return new CombatDamageVolleyResult(rejected, (byte)shotCount, 0, bloom);
            }
            if (health.Current <= health.Minimum)
            {
                var dead = new CombatDamageResult(eventId, CombatDamageResultCode.RejectedTargetDead, cause,
                    sourceId, targetId, 0, 0, health.Current, health.Current, CombatDamageType.Normal, false, false, damageTypeId);
                return new CombatDamageVolleyResult(dead, (byte)shotCount, 0, bloom);
            }
            if ((policy & CombatDamagePolicy.RespectInvincibility) != 0 && target.Combat.Invincible)
            {
                var invincible = new CombatDamageResult(eventId, CombatDamageResultCode.RejectedInvincible, cause,
                    sourceId, targetId, 0, 0, health.Current, health.Current, CombatDamageType.Normal, false, false, damageTypeId);
                return new CombatDamageVolleyResult(invincible, (byte)shotCount, 0, bloom);
            }

            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            float lo = SanitizeNonNegative(minimum);
            float hi = Math.Max(lo, SanitizeNonNegative(maximum));
            bool canCrit = (effectFlags & GameplayEffectFlags.CanCrit) != 0 &&
                           (policy & CombatDamagePolicy.AllowCritical) != 0 && source != null;
            bool canBlock = (effectFlags & GameplayEffectFlags.CanBlock) != 0 &&
                            (policy & CombatDamagePolicy.AllowBlock) != 0;
            bool canRespond = (effectFlags & GameplayEffectFlags.CanBeResisted) != 0;

            int hitCount = 0;
            int rawTotal = 0;
            int resolvedTotal = 0;
            bool anyBlocked = false;
            CombatDamagePresentationFlags aggregateFlags = CombatDamagePresentationFlags.None;

            for (int shot = 0; shot < shotCount; ++shot)
            {
                double chance = Math.Max(0d, Math.Min(1d, hitBase - bloom));
                bool hit = chance >= 1d || (chance > 0d && _random.NextUnit() < chance);
                bloom = Math.Min(maxBloom, bloom + perShotBloom);
                if (!hit)
                    continue;

                hitCount++;
                int raw = RoundToInt(lo + ((hi - lo) * _random.NextUnit()));
                if (raw <= 0)
                    continue;
                rawTotal = SaturatingAdd(rawTotal, raw);
                double resolved = raw;

                if ((policy & CombatDamagePolicy.ApplyStatusMultipliers) != 0)
                {
                    if (source != null) resolved *= ClampMultiplier(source.GetStat("DamageDealtMultiplier", 1f));
                    resolved *= ClampMultiplier(target.GetStat("DamageTakenMultiplier", 1f));
                }

                bool blocked = false;
                if (canCrit)
                {
                    double critChance = ClampChance(source.GetStat("CriticalChance", 0f), rules.maximumCriticalChance);
                    if (critChance > 0d && _random.NextUnit() < critChance)
                    {
                        resolved *= Math.Max(1d, Math.Max(1f, rules.criticalDamageMultiplier));
                        aggregateFlags |= CombatDamagePresentationFlags.Critical;
                    }
                }

                if (canBlock)
                {
                    double blockChance = ClampChance(target.GetStat("BlockChance", 0f), rules.maximumBlockChance);
                    blocked = blockChance > 0d && _random.NextUnit() < blockChance;
                    if (blocked)
                    {
                        resolved *= Clamp01(rules.blockDamageMultiplier);
                        aggregateFlags |= CombatDamagePresentationFlags.Blocked;
                        anyBlocked = true;
                    }
                }

                if (!blocked && (policy & CombatDamagePolicy.ApplyDefense) != 0)
                {
                    string defenseStatId = "Armor";
                    if (damageTypeId != 0 && _content.TryGetDamageType(damageTypeId, out DamageTypeDefinition typed) &&
                        !string.IsNullOrWhiteSpace(typed.defenseStatId))
                        defenseStatId = typed.defenseStatId;
                    double defense = Math.Max(0d, target.GetStat(defenseStatId, 0f));
                    defense = Math.Max(0d, (defense * (1d - Clamp01(armorPenetrationPercent))) - Math.Max(0f, armorPenetrationFlat));
                    bool hadPositivePreDefenseDamage = resolved > 0d;
                    resolved = Math.Max(0d, resolved - defense);
                    if (hadPositivePreDefenseDamage && rules.minimumDamageAfterDefense > 0)
                        resolved = Math.Max(rules.minimumDamageAfterDefense, resolved);
                }

                if (canRespond)
                {
                    DamageResponseAggregate global = _content.GetGlobalDamageResponse(damageTypeId);
                    double responseMultiplier = global.Multiplier;
                    double responseFlat = global.FlatAdjustment;
                    responseMultiplier *= ClampMultiplier(target.GetStat(CombatDerivedStatIds.DamageResponseMultiplier(0), 1f));
                    responseFlat += target.GetStat(CombatDerivedStatIds.DamageResponseFlat(0), 0f);
                    if (damageTypeId != 0)
                    {
                        responseMultiplier *= ClampMultiplier(target.GetStat(CombatDerivedStatIds.DamageResponseMultiplier(damageTypeId), 1f));
                        responseFlat += target.GetStat(CombatDerivedStatIds.DamageResponseFlat(damageTypeId), 0f);
                    }
                    responseMultiplier = Clamp(responseMultiplier,
                        Math.Max(0f, rules.minimumDamageResponseMultiplier),
                        Math.Max(rules.minimumDamageResponseMultiplier, rules.maximumDamageResponseMultiplier));
                    if (responseMultiplier <= 0d)
                    {
                        resolved = 0d;
                        aggregateFlags |= CombatDamagePresentationFlags.Immune;
                    }
                    else
                    {
                        resolved = Math.Max(0d, (resolved + responseFlat) * responseMultiplier);
                        if ((effectFlags & GameplayEffectFlags.CanTriggerWeakness) != 0 && responseMultiplier > 1.0001d)
                            aggregateFlags |= CombatDamagePresentationFlags.Weakness;
                        else if (responseMultiplier < 0.9999d)
                            aggregateFlags |= CombatDamagePresentationFlags.Resisted;
                    }
                }

                int resolvedAmount = RoundToInt(resolved);
                if (resolvedAmount > 0 && !blocked)
                    resolvedAmount = Math.Max(rules.minimumDamageAfterDefense, resolvedAmount);
                resolvedTotal = SaturatingAdd(resolvedTotal, Math.Max(0, resolvedAmount));
            }

            int before = health.Current;
            if ((policy & CombatDamagePolicy.ForceLethal) != 0 && hitCount > 0)
                resolvedTotal = Math.Max(resolvedTotal, Math.Max(0, before - health.Minimum));

            int dealt = 0;
            if (resolvedTotal > 0)
            {
                CharacterResourceOperationResult mutation = _resources.Remove(
                    target, CharacterResourceId.Health, resolvedTotal,
                    cause == CombatDamageCause.SharedStatus ? CharacterResourceChangeReason.Environmental : CharacterResourceChangeReason.DamageTaken);
                if (mutation.Success && target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState afterHealth))
                    dealt = Math.Max(0, before - afterHealth.Current);
            }

            target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState finalHealth);
            int after = finalHealth.Current;
            bool killed = before > finalHealth.Minimum && after <= finalHealth.Minimum;
            if (killed) aggregateFlags |= CombatDamagePresentationFlags.Killed;

            bool validContact = anyBlocked || dealt > 0 || (hitCount > 0 && (aggregateFlags & CombatDamagePresentationFlags.Immune) != 0);
            CombatDamageResultCode code = !validContact ? CombatDamageResultCode.RejectedNoEffectiveDamage
                : killed ? CombatDamageResultCode.Killed
                : dealt > 0 ? CombatDamageResultCode.Applied
                : anyBlocked ? CombatDamageResultCode.Blocked
                : CombatDamageResultCode.RejectedNoEffectiveDamage;

            if (validContact)
            {
                if ((policy & CombatDamagePolicy.UpdateSourceCombatTime) != 0 && source != null) source.MarkCombatActivity(now);
                if ((policy & CombatDamagePolicy.UpdateTargetCombatTime) != 0) target.MarkCombatActivity(now);
            }

            CombatDamageType type = (aggregateFlags & CombatDamagePresentationFlags.Critical) != 0 ? CombatDamageType.Critical
                : (aggregateFlags & CombatDamagePresentationFlags.Blocked) != 0 ? CombatDamageType.Block
                : CombatDamageType.Normal;
            var aggregate = new CombatDamageResult(eventId, code, cause, sourceId, targetId, rawTotal, dealt, before, after,
                type, killed, false, damageTypeId, aggregateFlags);

            // Pure misses stay in the compact fire-cycle result only. A hit/contact emits one participant event.
            if (hitCount > 0)
                Publish(aggregate);
            if (killed)
            {
                _statuses.ClearOnDeath(target);
                _resources.ApplyDeathResets(target);
                CharacterKilled?.Invoke(target, aggregate);
            }
            return new CombatDamageVolleyResult(aggregate, (byte)shotCount, (byte)Math.Min(31, hitCount), bloom);
        }

        private static int SaturatingAdd(int left, int right)
        {
            long total = (long)left + right;
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public CombatHealingResult ApplyHealingRange(
            PlayerRuntime source,
            PlayerRuntime target,
            float minimum,
            float maximum,
            double now,
            bool canCrit = false,
            float criticalChanceModifier = 0f,
            float criticalMultiplierModifier = 0f)
        {
            float lo = SanitizeNonNegative(minimum);
            float hi = Math.Max(lo, SanitizeNonNegative(maximum));
            double value = lo + ((hi - lo) * _random.NextUnit());
            if (canCrit && source != null)
            {
                CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
                double chance = ClampChance(
                    source.GetStat("CriticalChance", 0f) + criticalChanceModifier,
                    rules.maximumCriticalChance);
                if (chance > 0d && _random.NextUnit() < chance)
                    value *= Math.Max(1d, rules.criticalDamageMultiplier + criticalMultiplierModifier);
            }
            return ApplyHealing(source, target, RoundToInt(value), now);
        }

        public CombatHealingResult ApplyHealing(
            PlayerRuntime source,
            PlayerRuntime target,
            int amount,
            double now)
        {
            long eventId = Interlocked.Increment(ref _nextEventId);
            long sourceId = source?.CharacterId.Value ?? 0L;
            long targetId = target?.CharacterId.Value ?? 0L;
            if (target == null || amount <= 0 || !IsValidTime(now) ||
                !target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) ||
                !health.Enabled || health.Current <= health.Minimum)
            {
                var rejected = new CombatHealingResult(eventId, sourceId, targetId, Math.Max(0, amount), 0, 0, 0);
                HealingResolved?.Invoke(rejected);
                return rejected;
            }

            int before = health.Current;
            int resolved = Math.Max(0, RoundToInt(
                amount * ClampMultiplier(target.GetStat("HealingReceivedMultiplier", 1f))));
            if (resolved > 0)
            {
                _resources.Add(
                    target,
                    CharacterResourceId.Health,
                    resolved,
                    CharacterResourceChangeReason.Healing);
            }

            target.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState finalHealth);
            var result = new CombatHealingResult(
                eventId,
                sourceId,
                targetId,
                amount,
                Math.Max(0, finalHealth.Current - before),
                before,
                finalHealth.Current);
            HealingResolved?.Invoke(result);
            return result;
        }

        private CombatDamageResult Publish(CombatDamageResult result)
        {
            DamageResolved?.Invoke(result);
            return result;
        }

        private static int RoundToInt(double value)
        {
            if (double.IsNaN(value) || value <= 0d) return 0;
            if (double.IsInfinity(value) || value >= int.MaxValue) return int.MaxValue;
            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private static double ClampChance(float value, float maximum)
        {
            double max = Clamp01(maximum);
            if (float.IsNaN(value) || value <= 0f) return 0d;
            if (float.IsInfinity(value)) return max;
            return Math.Min(value, max);
        }

        private static double ClampMultiplier(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0d;
            if (float.IsInfinity(value) || value >= 10f) return 10d;
            return value;
        }

        private static double Clamp01(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0d;
            if (float.IsInfinity(value) || value >= 1f) return 1d;
            return value;
        }

        private static float SanitizeNonNegative(float value)
        {
            if (float.IsNaN(value) || value <= 0f) return 0f;
            if (float.IsPositiveInfinity(value)) return float.MaxValue;
            return value;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value)) return minimum;
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static bool IsValidTime(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
    }
}
