using System;
using Game.Server.Application.Combat;
using Game.Server.Application.Content;
using Game.Server.Application.Resources;
using Game.Server.Domain.Actions;
using Game.Server.Domain.Players;
using Game.Server.Domain.Resources;
using Game.Server.Domain.StatusEffects;
using Game.Shared.Abilities;
using Game.Shared.Combat;
using Game.Shared.Content;
using Game.Shared.Effects;
using Game.Shared.Protocol;
using Game.Shared.Resources;

namespace Game.Server.Application.Abilities
{
    public readonly struct FirearmActionProfile
    {
        public string WeaponDefinitionId { get; }
        public FirearmFireMode FireMode { get; }
        public int RoundsPerTrigger { get; }
        public float RoundsPerSecond { get; }
        public float TriggerInterval { get; }
        public float Range { get; }
        public float BaseHitChance { get; }
        public float BloomPerShot { get; }
        public float MaximumBloom { get; }
        public float BloomRecoveryPerSecond { get; }
        public float AimBloomMultiplier { get; }
        public ushort PresentationId { get; }

        public FirearmActionProfile(string weaponDefinitionId, FirearmFireMode fireMode, int roundsPerTrigger,
            float roundsPerSecond, float triggerInterval, float range, float baseHitChance, float bloomPerShot,
            float maximumBloom, float bloomRecoveryPerSecond, float aimBloomMultiplier, ushort presentationId)
        {
            WeaponDefinitionId = weaponDefinitionId ?? string.Empty; FireMode = fireMode; RoundsPerTrigger = roundsPerTrigger;
            RoundsPerSecond = roundsPerSecond; TriggerInterval = triggerInterval; Range = range; BaseHitChance = baseHitChance;
            BloomPerShot = bloomPerShot; MaximumBloom = maximumBloom; BloomRecoveryPerSecond = bloomRecoveryPerSecond;
            AimBloomMultiplier = aimBloomMultiplier; PresentationId = presentationId;
        }
    }

    public readonly struct FirearmActionResolution
    {
        public BasicAttackResult Action { get; }
        public CombatOwnerStateSnapshot OwnerState { get; }
        public FirearmActionProfile Profile { get; }
        public byte RoundsFired { get; }
        public byte HitCount { get; }
        public float EndingBloom { get; }
        public bool Success => Action.Success;

        public FirearmActionResolution(BasicAttackResult action, CombatOwnerStateSnapshot ownerState, FirearmActionProfile profile, byte roundsFired, byte hitCount, float endingBloom)
        { Action = action; OwnerState = ownerState; Profile = profile; RoundsFired = roundsFired; HitCount = hitCount; EndingBloom = endingBloom; }
        public static FirearmActionResolution Rejected(BasicAttackResult action) => new FirearmActionResolution(action, default, default, 0, 0, 0f);
    }

    /// <summary>
    /// Canonical server-authoritative basic attack gateway. Clients send only target and
    /// input class. The server owns cadence, stamina, magazine/ammo, combo truth, damage,
    /// typed responses, hit result and all state mutations.
    /// </summary>
    public sealed class BasicAttackService
    {
        private readonly GameplayContentCatalog _content;
        private readonly CombatService _combat;
        private readonly CharacterResourceService _resources;
        private readonly CombatLoadoutService _loadout;

        public event Action<BasicAttackResult> Resolved;

        public BasicAttackService(GameplayContentCatalog content, CombatService combat)
            : this(content, combat, new CharacterResourceService(content), new CombatLoadoutService(content))
        {
        }

        public BasicAttackService(
            GameplayContentCatalog content,
            CombatService combat,
            CharacterResourceService resources,
            CombatLoadoutService loadout)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
            _combat = combat ?? throw new ArgumentNullException(nameof(combat));
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
        }

        public BasicAttackResult TryAttack(PlayerRuntime source, PlayerRuntime target, double now) =>
            TryAttack(source, target, BasicAttackInputKind.Primary, now);

        public BasicAttackResult TryAttack(
            PlayerRuntime source,
            PlayerRuntime target,
            BasicAttackInputKind inputKind,
            double now)
        {
            long sourceId = source?.CharacterId.Value ?? 0L;
            long targetId = target?.CharacterId.Value ?? 0L;
            CharacterActionState actions = source?.CaptureActionState();
            long actionRevision = actions?.Revision ?? 0L;
            double recoveryEnd = actions?.BasicAttackRecoveryEnd ?? 0d;

            if (!IsValidInput(inputKind))
                return Reject(BasicAttackResultCode.RejectedInvalidInput, sourceId, targetId, actionRevision, recoveryEnd, inputKind);
            if (source == null)
                return Reject(BasicAttackResultCode.RejectedInvalidSource, sourceId, targetId, actionRevision, recoveryEnd, inputKind);
            if (target == null || ReferenceEquals(source, target))
                return Reject(BasicAttackResultCode.RejectedInvalidTarget, sourceId, targetId, actionRevision, recoveryEnd, inputKind);
            if (!IsFiniteTime(now))
                return Reject(BasicAttackResultCode.RejectedInvalidSource, sourceId, targetId, actionRevision, recoveryEnd, inputKind);
            if (!IsAlive(source) || !IsAlive(target))
                return Reject(BasicAttackResultCode.RejectedDead, sourceId, targetId, actionRevision, recoveryEnd, inputKind);
            if (!SameWorld(source, target))
                return Reject(BasicAttackResultCode.RejectedDifferentWorld, sourceId, targetId, actionRevision, recoveryEnd, inputKind);

            CharacterStatusEffectsState statusState = source.CaptureStatusEffects();
            if (statusState.HasControl(ControlEffectType.Stun) || statusState.HasControl(ControlEffectType.Disarm))
                return Reject(BasicAttackResultCode.RejectedControlled, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind);

            actions = source.CaptureActionState();
            if (actions.ActiveCast.IsActive)
                return Reject(BasicAttackResultCode.RejectedAlreadyCasting, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind);
            if (now < actions.BasicAttackRecoveryEnd)
                return Reject(BasicAttackResultCode.RejectedRecovery, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind);

            AttackProfile profile = ResolveAttackProfile(source, inputKind, now);
            if (!profile.Valid)
                return Reject(BasicAttackResultCode.RejectedInvalidInput, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);
            if (!WithinRange(source, target, profile.Mode, profile.Range))
                return Reject(BasicAttackResultCode.RejectedOutOfRange, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);
            if (profile.DamageMax <= 0f)
                return Reject(BasicAttackResultCode.RejectedNoDamage, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);

            // Fail closed from authoritative state before committing a recovery window.
            if (profile.Mode == BasicAttackMode.Unarmed && profile.StaminaCost > 0 &&
                !HasSpendableResource(source, CharacterResourceId.Stamina, profile.StaminaCost))
            {
                return Reject(BasicAttackResultCode.RejectedInsufficientResource, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);
            }
            if (profile.Mode == BasicAttackMode.Firearm && profile.OwnerState.LoadedRounds <= 0)
            {
                return Reject(BasicAttackResultCode.RejectedNoAmmo, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);
            }

            double nextRecovery = now + BasicAttackCadenceTiming.Clamp(profile.Interval);
            CharacterActionState next = actions.WithBasicAttackRecovery(nextRecovery);
            if (!source.TryCommitActionState(
                    actions.Revision,
                    next,
                    CharacterActionChangeReason.BasicAttackCommitted,
                    string.Empty,
                    0,
                    nextRecovery))
            {
                actions = source.CaptureActionState();
                return Reject(BasicAttackResultCode.RejectedRecovery, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, profile.Mode, profile.ComboStep);
            }

            byte committedCombo = profile.ComboStep;
            if (profile.Mode == BasicAttackMode.Unarmed)
            {
                if (profile.StaminaCost > 0)
                {
                    CharacterResourceOperationResult spend = _resources.Spend(
                        source,
                        CharacterResourceId.Stamina,
                        profile.StaminaCost,
                        CharacterResourceChangeReason.UnarmedAttack);
                    if (!spend.Success)
                    {
                        return Reject(BasicAttackResultCode.RejectedInsufficientResource, sourceId, targetId, next.Revision, nextRecovery, inputKind, profile.Mode, profile.ComboStep);
                    }
                }
                committedCombo = _loadout.CommitUnarmedCombo(source, now);
            }
            else if (profile.Mode == BasicAttackMode.Firearm)
            {
                if (!_loadout.TryConsumeRound(source, now, out string ammoDefinitionId, out _))
                {
                    return Reject(BasicAttackResultCode.RejectedNoAmmo, sourceId, targetId, next.Revision, nextRecovery, inputKind, profile.Mode, 0);
                }
                profile = ResolveConsumedFirearmPayload(profile, ammoDefinitionId);
                if (!profile.Valid || profile.DamageMax <= 0f)
                {
                    return Reject(BasicAttackResultCode.RejectedNoDamage, sourceId, targetId, next.Revision, nextRecovery, inputKind, profile.Mode, 0);
                }
            }

            CombatDamageResult damage = _combat.ApplyDamageRange(
                source,
                target,
                profile.DamageMin,
                profile.DamageMax,
                profile.DamageTypeId,
                profile.DamageFlags,
                0f,
                0f,
                profile.PenetrationFlat,
                profile.PenetrationPercent,
                now,
                CombatDamageCause.Combat,
                CombatService.StandardDamagePolicy);

            BasicAttackResultCode code = damage.resultCode == CombatDamageResultCode.Applied ||
                                         damage.resultCode == CombatDamageResultCode.Blocked ||
                                         damage.resultCode == CombatDamageResultCode.Killed
                ? BasicAttackResultCode.Applied
                : BasicAttackResultCode.RejectedNoDamage;

            return Publish(new BasicAttackResult(
                code,
                sourceId,
                targetId,
                next.Revision,
                nextRecovery,
                damage,
                inputKind,
                profile.Mode,
                committedCombo));
        }

        public bool TryResolveFirearmProfile(PlayerRuntime source, double now, out FirearmActionProfile firearm)
        {
            firearm = default;
            if (source == null)
                return false;
            AttackProfile attack = ResolveAttackProfile(source, BasicAttackInputKind.Primary, now);
            if (!attack.Valid || attack.Mode != BasicAttackMode.Firearm ||
                string.IsNullOrWhiteSpace(attack.WeaponDefinitionId) ||
                !_content.TryGetItem(attack.WeaponDefinitionId, out ItemDefinition weapon))
                return false;

            FirearmFireMode mode = weapon.firearmFireMode;
            float rps = FirearmCadenceTiming.ResolveRoundsPerSecond(weapon.firearmRoundsPerSecond, weapon.basicAttackInterval);
            firearm = new FirearmActionProfile(
                attack.WeaponDefinitionId, mode,
                FirearmCadenceTiming.ResolveRoundsPerTrigger(mode, weapon.firearmRoundsPerTrigger),
                rps,
                FirearmCadenceTiming.ResolveTriggerInterval(mode, rps, weapon.basicAttackInterval),
                attack.Range,
                weapon.firearmBaseHitChance > 0f ? Math.Max(0f, Math.Min(1f, weapon.firearmBaseHitChance)) : 1f,
                weapon.firearmBloomPerShot > 0f ? weapon.firearmBloomPerShot : 0.035f,
                weapon.firearmMaximumBloom > 0f ? weapon.firearmMaximumBloom : 0.45f,
                weapon.firearmBloomRecoveryPerSecond > 0f ? weapon.firearmBloomRecoveryPerSecond : 0.35f,
                weapon.firearmAimBloomMultiplier > 0f ? Math.Max(0f, Math.Min(1f, weapon.firearmAimBloomMultiplier)) : 0.65f,
                weapon.presentationId);
            return true;
        }

        public FirearmActionResolution TryFirearmAction(
            PlayerRuntime source, PlayerRuntime target, BasicAttackInputKind inputKind,
            int requestedRounds, float startingBloom, bool aiming, bool enforceRecovery, double now)
        {
            long sourceId = source?.CharacterId.Value ?? 0L;
            long targetId = target?.CharacterId.Value ?? 0L;
            CharacterActionState actions = source?.CaptureActionState();
            long revision = actions?.Revision ?? 0L;
            double recoveryEnd = actions?.BasicAttackRecoveryEnd ?? 0d;

            if (source == null || target == null || ReferenceEquals(source, target) || !IsFiniteTime(now) ||
                !IsAlive(source) || !IsAlive(target) || !SameWorld(source, target))
                return FirearmActionResolution.Rejected(Reject(
                    source == null ? BasicAttackResultCode.RejectedInvalidSource : BasicAttackResultCode.RejectedInvalidTarget,
                    sourceId, targetId, revision, recoveryEnd, inputKind, BasicAttackMode.Firearm));

            CharacterStatusEffectsState status = source.CaptureStatusEffects();
            if (status.HasControl(ControlEffectType.Stun) || status.HasControl(ControlEffectType.Disarm))
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedControlled, sourceId, targetId, revision, recoveryEnd, inputKind, BasicAttackMode.Firearm));

            actions = source.CaptureActionState();
            if (actions.ActiveCast.IsActive)
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedAlreadyCasting, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));
            if (enforceRecovery && now < actions.BasicAttackRecoveryEnd)
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedRecovery, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));

            AttackProfile attack = ResolveAttackProfile(source, inputKind, now);
            if (!attack.Valid || attack.Mode != BasicAttackMode.Firearm)
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedInvalidInput, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));
            if (!WithinRange(source, target, attack.Mode, attack.Range))
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedOutOfRange, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));
            if (!TryResolveFirearmProfile(source, now, out FirearmActionProfile firearm))
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedInvalidInput, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));

            CharacterActionState committed = actions;
            double nextRecovery = actions.BasicAttackRecoveryEnd;
            if (enforceRecovery)
            {
                nextRecovery = now + firearm.TriggerInterval;
                committed = actions.WithBasicAttackRecovery(nextRecovery);
                if (!source.TryCommitActionState(actions.Revision, committed, CharacterActionChangeReason.BasicAttackCommitted, string.Empty, 0, nextRecovery))
                {
                    actions = source.CaptureActionState();
                    return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedRecovery, sourceId, targetId, actions.Revision, actions.BasicAttackRecoveryEnd, inputKind, BasicAttackMode.Firearm));
                }
            }

            int wanted = Math.Max(1, Math.Min(31, requestedRounds));
            if (!_loadout.TryConsumeRounds(source, wanted, now, out string ammoDefinitionId, out int consumed, out CombatOwnerStateSnapshot ownerState))
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedNoAmmo, sourceId, targetId, committed.Revision, nextRecovery, inputKind, BasicAttackMode.Firearm));

            attack = ResolveConsumedFirearmPayload(attack, ammoDefinitionId);
            if (!attack.Valid || attack.DamageMax <= 0f)
                return FirearmActionResolution.Rejected(Reject(BasicAttackResultCode.RejectedNoDamage, sourceId, targetId, committed.Revision, nextRecovery, inputKind, BasicAttackMode.Firearm));

            float bloomGain = firearm.BloomPerShot * (aiming ? firearm.AimBloomMultiplier : 1f);
            CombatDamageVolleyResult volley = _combat.ApplyDamageVolley(
                source, target, consumed, attack.DamageMin, attack.DamageMax, attack.DamageTypeId, attack.DamageFlags,
                attack.PenetrationFlat, attack.PenetrationPercent, firearm.BaseHitChance,
                startingBloom, bloomGain, firearm.MaximumBloom, now, CombatDamageCause.Combat, CombatService.StandardDamagePolicy);

            var result = Publish(new BasicAttackResult(
                BasicAttackResultCode.Applied, sourceId, targetId, committed.Revision, nextRecovery,
                volley.Damage, inputKind, BasicAttackMode.Firearm, 0));
            return new FirearmActionResolution(result, ownerState, firearm, volley.RoundsResolved, volley.HitCount, volley.EndingBloom);
        }

        public ushort ResolvePresentationId(PlayerRuntime source)
        {
            if (source == null)
                return 0;
            var itemSystems = source.CapturePlayerItemSystems();
            var mainHand = itemSystems?.Equipment.Get("MainHand");
            return mainHand != null &&
                   _content.TryGetItem(mainHand.DefinitionId, out ItemDefinition weapon)
                ? weapon.presentationId
                : (ushort)0;
        }

        private AttackProfile ResolveAttackProfile(PlayerRuntime source, BasicAttackInputKind inputKind, double now)
        {
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            CombatOwnerStateSnapshot ownerState = _loadout.Capture(source, now);
            BasicAttackMode mode = ownerState.Mode;
            GameplayEffectFlags flags = GameplayEffectFlags.CanCrit | GameplayEffectFlags.CanBlock |
                                        GameplayEffectFlags.CanBeResisted | GameplayEffectFlags.CanTriggerWeakness;

            float range = CombatRangePolicy.UnarmedRange;
            float interval = BasicAttackCadenceTiming.Clamp(rules.basicAttackInterval);
            float unarmed = Math.Max(1f, source.GetStat("AttackPower", Math.Max(1, rules.unarmedBasicAttackDamage)));

            if (mode == BasicAttackMode.Unarmed)
            {
                bool heavy = inputKind == BasicAttackInputKind.Heavy;
                if (inputKind != BasicAttackInputKind.Primary && inputKind != BasicAttackInputKind.Light && !heavy)
                    return AttackProfile.Invalid(mode, ownerState);

                interval = heavy ? BasicAttackCadenceTiming.HeavyInterval : BasicAttackCadenceTiming.UnarmedLightInterval;
                int staminaCost = heavy
                    ? Math.Max(0, rules.unarmedHeavyStaminaCost)
                    : Math.Max(0, rules.unarmedLightStaminaCost);
                byte comboStep = _loadout.PreviewNextUnarmedComboStep(source, now);
                double comboMultiplier = 1d + Math.Max(0, comboStep - 1) * Math.Max(0f, rules.unarmedComboDamagePerStep);
                double heavyMultiplier = heavy ? Math.Max(1f, rules.unarmedHeavyDamageMultiplier) : 1d;
                float damage = SanitizeDamage(unarmed * comboMultiplier * heavyMultiplier);
                return new AttackProfile(
                    true, mode, range, interval, damage, damage, rules.unarmedDamageTypeId,
                    flags, 0f, 0f, staminaCost, comboStep, ownerState, string.Empty);
            }

            // For now Heavy is an unarmed input class. Weapon-specific alternate attacks
            // should become authored actions rather than client-selected move payloads.
            if (inputKind == BasicAttackInputKind.Heavy)
                return AttackProfile.Invalid(mode, ownerState);

            var itemSystems = source.CapturePlayerItemSystems();
            var mainHand = itemSystems?.Equipment?.Get("MainHand");
            if (mainHand == null || !_content.TryGetItem(mainHand.DefinitionId, out ItemDefinition weapon))
                return AttackProfile.Invalid(mode, ownerState);

            range = CombatRangePolicy.ResolveServerRange(mode, weapon.basicAttackRange);
            if (range <= 0f)
                return AttackProfile.Invalid(mode, ownerState);
            if (weapon.basicAttackInterval > 0f)
                interval = mode == BasicAttackMode.Firearm
                    ? FirearmCadenceTiming.ResolveTriggerInterval(
                        weapon.firearmFireMode, weapon.firearmRoundsPerSecond, weapon.basicAttackInterval)
                    : BasicAttackCadenceTiming.Clamp(weapon.basicAttackInterval);

            float damageMin = weapon.damageMin;
            float damageMax = weapon.damageMax;
            if (damageMax <= 0f)
            {
                damageMin = unarmed;
                damageMax = unarmed;
            }

            if (!weapon.canCrit)
                flags &= ~GameplayEffectFlags.CanCrit;

            return new AttackProfile(
                true,
                mode,
                range,
                interval,
                damageMin,
                damageMax,
                weapon.damageTypeId,
                flags,
                weapon.armorPenetrationFlat,
                weapon.armorPenetrationPercent,
                0,
                0,
                ownerState,
                weapon.definitionId ?? string.Empty);
        }

        private AttackProfile ResolveConsumedFirearmPayload(AttackProfile profile, string ammoDefinitionId)
        {
            if (profile.Mode != BasicAttackMode.Firearm ||
                string.IsNullOrWhiteSpace(profile.WeaponDefinitionId) ||
                !_content.TryGetItem(profile.WeaponDefinitionId, out ItemDefinition weapon) ||
                string.IsNullOrWhiteSpace(ammoDefinitionId) ||
                !_content.TryGetItem(ammoDefinitionId, out ItemDefinition ammo) ||
                ammo.kind != ItemKind.Ammo ||
                !string.Equals(ammo.ammoFamily ?? string.Empty, weapon.ammoFamily ?? string.Empty, StringComparison.Ordinal))
            {
                return AttackProfile.Invalid(BasicAttackMode.Firearm, profile.OwnerState);
            }

            float damageMin = ammo.damageMax > 0f ? ammo.damageMin : profile.DamageMin;
            float damageMax = ammo.damageMax > 0f ? ammo.damageMax : profile.DamageMax;
            ushort damageTypeId = ammo.damageTypeId != 0 ? ammo.damageTypeId : profile.DamageTypeId;
            GameplayEffectFlags flags = profile.DamageFlags;
            if (!ammo.canCrit)
                flags &= ~GameplayEffectFlags.CanCrit;

            float penetrationFlat = Math.Max(0f, profile.PenetrationFlat) + Math.Max(0f, ammo.armorPenetrationFlat);
            float penetrationPercent = Clamp01(profile.PenetrationPercent + ammo.armorPenetrationPercent);
            return profile.WithPayload(
                damageMin,
                damageMax,
                damageTypeId,
                flags,
                penetrationFlat,
                penetrationPercent);
        }

        private static bool HasSpendableResource(PlayerRuntime runtime, CharacterResourceId id, int amount)
        {
            if (amount <= 0)
                return true;
            return runtime != null &&
                   runtime.TryGetCharacterResource(id, out _, out CharacterResourceState state) &&
                   state.Enabled &&
                   (long)state.Current - amount >= state.Minimum;
        }

        private static bool IsValidInput(BasicAttackInputKind input) =>
            input == BasicAttackInputKind.Primary ||
            input == BasicAttackInputKind.Light ||
            input == BasicAttackInputKind.Heavy;

        private static bool IsAlive(PlayerRuntime runtime) =>
            runtime != null &&
            runtime.TryGetCharacterResource(CharacterResourceId.Health, out _, out CharacterResourceState health) &&
            health.Enabled && health.Current > health.Minimum;

        private static bool SameWorld(PlayerRuntime a, PlayerRuntime b)
        {
            var left = a.Location;
            var right = b.Location;
            return string.Equals(left.MapId, right.MapId, StringComparison.Ordinal) &&
                   string.Equals(left.InstanceId, right.InstanceId, StringComparison.Ordinal);
        }

        private static bool WithinRange(PlayerRuntime a, PlayerRuntime b, BasicAttackMode mode, float range)
        {
            var p = a.Location.Position;
            var q = b.Location.Position;
            double dx = p.X - q.X;
            double dy = p.Y - q.Y;
            double dz = p.Z - q.Z;
            double r = Math.Max(0.1, range);

            // Melee reach is measured from actor ground position in the horizontal plane.
            // A bounded vertical tolerance prevents attacking actors on another floor while
            // avoiding collider/pivot height consuming most of a short melee radius.
            if (mode == BasicAttackMode.Unarmed || mode == BasicAttackMode.MeleeWeapon)
            {
                if (Math.Abs(dy) > CombatRangePolicy.MeleeVerticalTolerance)
                    return false;

                // Melee authored range is reach from the attacker to the target body's edge,
                // not a center-to-center requirement. This matches the authoritative swing
                // resolver and avoids forcing actors to visually overlap before a valid hit.
                double centerLimit = r + CombatRangePolicy.MeleeTargetBodyRadius;
                return dx * dx + dz * dz <= centerLimit * centerLimit;
            }

            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        private static bool IsFiniteTime(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;

        private static float SanitizeDamage(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
                return 0f;
            return value >= float.MaxValue ? float.MaxValue : (float)value;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;
            if (value <= 0f) return 0f;
            if (value >= 1f) return 1f;
            return value;
        }

        private BasicAttackResult Reject(
            BasicAttackResultCode code,
            long sourceId,
            long targetId,
            long actionRevision,
            double recoveryEnd,
            BasicAttackInputKind inputKind,
            BasicAttackMode mode = BasicAttackMode.Unarmed,
            byte comboStep = 0) =>
            Publish(new BasicAttackResult(
                code, sourceId, targetId, actionRevision, recoveryEnd, default,
                inputKind, mode, comboStep));

        private BasicAttackResult Publish(BasicAttackResult result)
        {
            Resolved?.Invoke(result);
            return result;
        }

        private readonly struct AttackProfile
        {
            public bool Valid { get; }
            public BasicAttackMode Mode { get; }
            public float Range { get; }
            public float Interval { get; }
            public float DamageMin { get; }
            public float DamageMax { get; }
            public ushort DamageTypeId { get; }
            public GameplayEffectFlags DamageFlags { get; }
            public float PenetrationFlat { get; }
            public float PenetrationPercent { get; }
            public int StaminaCost { get; }
            public byte ComboStep { get; }
            public CombatOwnerStateSnapshot OwnerState { get; }
            public string WeaponDefinitionId { get; }

            public AttackProfile(
                bool valid,
                BasicAttackMode mode,
                float range,
                float interval,
                float damageMin,
                float damageMax,
                ushort damageTypeId,
                GameplayEffectFlags damageFlags,
                float penetrationFlat,
                float penetrationPercent,
                int staminaCost,
                byte comboStep,
                CombatOwnerStateSnapshot ownerState,
                string weaponDefinitionId)
            {
                Valid = valid;
                Mode = mode;
                Range = range;
                Interval = interval;
                DamageMin = damageMin;
                DamageMax = damageMax;
                DamageTypeId = damageTypeId;
                DamageFlags = damageFlags;
                PenetrationFlat = penetrationFlat;
                PenetrationPercent = penetrationPercent;
                StaminaCost = staminaCost;
                ComboStep = comboStep;
                OwnerState = ownerState;
                WeaponDefinitionId = weaponDefinitionId ?? string.Empty;
            }

            public AttackProfile WithPayload(
                float damageMin,
                float damageMax,
                ushort damageTypeId,
                GameplayEffectFlags damageFlags,
                float penetrationFlat,
                float penetrationPercent) =>
                new AttackProfile(
                    Valid, Mode, Range, Interval, damageMin, damageMax, damageTypeId,
                    damageFlags, penetrationFlat, penetrationPercent, StaminaCost,
                    ComboStep, OwnerState, WeaponDefinitionId);

            public static AttackProfile Invalid(BasicAttackMode mode, CombatOwnerStateSnapshot ownerState) =>
                new AttackProfile(
                    false, mode, 0f, BasicAttackCadenceTiming.StandardInterval, 0f, 0f, 0,
                    GameplayEffectFlags.None, 0f, 0f, 0, 0, ownerState, string.Empty);
        }
    }
}
