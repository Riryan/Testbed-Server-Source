using System;
using System.Runtime.CompilerServices;
using Game.Server.Application.Content;
using Game.Server.Domain.Inventory;
using Game.Server.Domain.Players;
using Game.Shared.Abilities;
using Game.Shared.Content;
using Game.Shared.Identity;

namespace Game.Server.Application.Combat
{
    /// <summary>
    /// Server-authoritative combat loadout cache with persistent weapon-instance magazine state. Magazine contents and
    /// unarmed combo truth intentionally live outside persisted item definitions.
    /// Per-shot mutations remain server-local; callers decide when an owner snapshot
    /// is worth sending (equip/reload/correction/snapshot, never observer fan-out).
    /// </summary>
    public sealed class CombatLoadoutService
    {
        private sealed class RuntimeState
        {
            public readonly object Gate = new object();
            public long WeaponInstanceId;
            public string WeaponDefinitionId = string.Empty;
            public string LoadedAmmoDefinitionId = string.Empty;
            public int LoadedRounds;
            public long MagazineRevision;
            public long Revision;
            public byte ComboStep;
            public double ComboExpiresAt;
        }

        private readonly GameplayContentCatalog _content;
        private readonly ConditionalWeakTable<PlayerRuntime, RuntimeState> _states =
            new ConditionalWeakTable<PlayerRuntime, RuntimeState>();

        public CombatLoadoutService(GameplayContentCatalog content) =>
            _content = content ?? throw new ArgumentNullException(nameof(content));

        public CombatOwnerStateSnapshot Capture(PlayerRuntime runtime, double now)
        {
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            if (runtime == null)
                return CombatOwnerStateSnapshot.Unavailable(rules);

            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                ResolveEquippedWeapon(runtime, state, out BasicAttackMode mode, out ItemDefinition weapon);
                RefreshComboExpiry(state, now);
                return BuildSnapshot(state, mode, weapon, rules);
            }
        }

        public bool HasLoadedRound(PlayerRuntime runtime, out CombatOwnerStateSnapshot snapshot)
        {
            snapshot = Capture(runtime, 0d);
            return snapshot.Mode == BasicAttackMode.Firearm && snapshot.LoadedRounds > 0;
        }

        public bool TryConsumeRound(
            PlayerRuntime runtime,
            double now,
            out string ammoDefinitionId,
            out CombatOwnerStateSnapshot snapshot)
        {
            bool success = TryConsumeRounds(runtime, 1, now, out ammoDefinitionId, out int consumed, out snapshot);
            return success && consumed == 1;
        }

        public bool TryConsumeRounds(
            PlayerRuntime runtime,
            int requestedRounds,
            double now,
            out string ammoDefinitionId,
            out int consumedRounds,
            out CombatOwnerStateSnapshot snapshot)
        {
            ammoDefinitionId = string.Empty;
            consumedRounds = 0;
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            if (runtime == null || requestedRounds <= 0)
            {
                snapshot = CombatOwnerStateSnapshot.Unavailable(rules);
                return false;
            }

            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                ResolveEquippedWeapon(runtime, state, out BasicAttackMode mode, out ItemDefinition weapon);
                RefreshComboExpiry(state, now);
                if (mode != BasicAttackMode.Firearm || weapon == null || state.LoadedRounds <= 0)
                {
                    snapshot = BuildSnapshot(state, mode, weapon, rules);
                    return false;
                }

                consumedRounds = Math.Min(Math.Min(31, requestedRounds), state.LoadedRounds);
                int nextLoadedRounds = state.LoadedRounds - consumedRounds;
                long expectedMagazineRevision = state.MagazineRevision;
                long nextMagazineRevision = checked(expectedMagazineRevision + 1);
                string nextAmmoDefinitionId = nextLoadedRounds > 0
                    ? (state.LoadedAmmoDefinitionId ?? string.Empty)
                    : string.Empty;

                if (!runtime.TryCommitItemMagazine(
                        new ItemInstanceId(state.WeaponInstanceId),
                        expectedMagazineRevision,
                        nextAmmoDefinitionId,
                        nextLoadedRounds,
                        nextMagazineRevision))
                {
                    // An equip/item transaction may have won the race. Re-resolve from the
                    // canonical item instance and fail closed rather than consuming an
                    // untracked round.
                    ResolveEquippedWeapon(runtime, state, out mode, out weapon);
                    snapshot = BuildSnapshot(state, mode, weapon, rules);
                    consumedRounds = 0;
                    return false;
                }

                ammoDefinitionId = state.LoadedAmmoDefinitionId ?? string.Empty;
                state.LoadedRounds = nextLoadedRounds;
                state.LoadedAmmoDefinitionId = nextAmmoDefinitionId;
                state.MagazineRevision = nextMagazineRevision;
                state.Revision = checked(state.Revision + 1);
                snapshot = BuildSnapshot(state, mode, weapon, rules);
                return consumedRounds > 0;
            }
        }

        public CombatOwnerStateSnapshot ApplyReload(
            PlayerRuntime runtime,
            string ammoDefinitionId,
            int loadedRounds,
            double now)
        {
            // Reload persistence now updates the equipped weapon instance atomically with
            // inventory ammo consumption. Re-resolve the canonical item state rather than
            // creating a second magazine mutation here.
            return Capture(runtime, now);
        }

        public CombatOwnerStateSnapshot ClearMagazine(PlayerRuntime runtime, double now)
        {
            CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
            if (runtime == null)
                return CombatOwnerStateSnapshot.Unavailable(rules);

            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                ResolveEquippedWeapon(runtime, state, out BasicAttackMode mode, out ItemDefinition weapon);
                if (state.WeaponInstanceId > 0)
                {
                    long expected = state.MagazineRevision;
                    long nextRevision = checked(expected + 1);
                    if (runtime.TryCommitItemMagazine(
                            new ItemInstanceId(state.WeaponInstanceId),
                            expected,
                            string.Empty,
                            0,
                            nextRevision))
                    {
                        state.LoadedRounds = 0;
                        state.LoadedAmmoDefinitionId = string.Empty;
                        state.MagazineRevision = nextRevision;
                        state.Revision = checked(state.Revision + 1);
                    }
                    else
                    {
                        ResolveEquippedWeapon(runtime, state, out mode, out weapon);
                    }
                }
                RefreshComboExpiry(state, now);
                return BuildSnapshot(state, mode, weapon, rules);
            }
        }

        public byte PreviewNextUnarmedComboStep(PlayerRuntime runtime, double now)
        {
            if (runtime == null)
                return 0;
            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                ResolveEquippedWeapon(runtime, state, out BasicAttackMode mode, out _);
                if (mode != BasicAttackMode.Unarmed)
                    return 0;
                RefreshComboExpiry(state, now);
                CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
                int max = Math.Max(1, rules.unarmedMaximumComboStep);
                return (byte)Math.Min(max, state.ComboStep + 1);
            }
        }

        public byte CommitUnarmedCombo(PlayerRuntime runtime, double now)
        {
            if (runtime == null)
                return 0;
            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                ResolveEquippedWeapon(runtime, state, out BasicAttackMode mode, out _);
                if (mode != BasicAttackMode.Unarmed)
                    return 0;
                RefreshComboExpiry(state, now);
                CombatRulesDefinition rules = _content.GetCombatRules() ?? new CombatRulesDefinition();
                int max = Math.Max(1, rules.unarmedMaximumComboStep);
                state.ComboStep = (byte)Math.Min(max, state.ComboStep + 1);
                state.ComboExpiresAt = now + Math.Max(0.1d, rules.unarmedComboWindowSeconds);
                state.Revision = checked(state.Revision + 1);
                return state.ComboStep;
            }
        }

        public void ResetForEquipmentChange(PlayerRuntime runtime)
        {
            if (runtime == null)
                return;
            RuntimeState state = _states.GetValue(runtime, _ => new RuntimeState());
            lock (state.Gate)
            {
                state.WeaponInstanceId = 0;
                state.WeaponDefinitionId = string.Empty;
                state.LoadedAmmoDefinitionId = string.Empty;
                state.LoadedRounds = 0;
                state.MagazineRevision = 0;
                state.ComboStep = 0;
                state.ComboExpiresAt = 0d;
                state.Revision = checked(state.Revision + 1);
            }
        }

        private void ResolveEquippedWeapon(
            PlayerRuntime runtime,
            RuntimeState state,
            out BasicAttackMode mode,
            out ItemDefinition weapon)
        {
            mode = BasicAttackMode.Unarmed;
            weapon = null;
            var itemSystems = runtime?.CapturePlayerItemSystems();
            ItemInstanceState mainHand = itemSystems?.Equipment?.Get("MainHand");
            long instanceId = mainHand?.ItemInstanceId.Value ?? 0L;
            string definitionId = mainHand?.DefinitionId ?? string.Empty;

            if (instanceId != state.WeaponInstanceId ||
                !string.Equals(definitionId, state.WeaponDefinitionId, StringComparison.Ordinal))
            {
                state.WeaponInstanceId = instanceId;
                state.WeaponDefinitionId = definitionId;
                state.LoadedAmmoDefinitionId = mainHand?.LoadedAmmoDefinitionId ?? string.Empty;
                state.LoadedRounds = Math.Max(0, mainHand?.LoadedRounds ?? 0);
                state.MagazineRevision = Math.Max(0, mainHand?.MagazineRevision ?? 0);
                state.ComboStep = 0;
                state.ComboExpiresAt = 0d;
                state.Revision = checked(state.Revision + 1);
            }
            else if (mainHand != null && mainHand.MagazineRevision > state.MagazineRevision)
            {
                // Reload/item persistence may install a newer durable magazine while the
                // combat loadout object remains alive. Always move forward by magazine
                // revision; never let an older item snapshot roll local combat state back.
                state.LoadedAmmoDefinitionId = mainHand.LoadedAmmoDefinitionId ?? string.Empty;
                state.LoadedRounds = Math.Max(0, mainHand.LoadedRounds);
                state.MagazineRevision = mainHand.MagazineRevision;
                state.Revision = checked(state.Revision + 1);
            }

            if (mainHand == null || !_content.TryGetItem(definitionId, out weapon))
                return;

            // Fail closed for any ammo-fed weapon. Content validation requires a positive
            // magazine capacity, but treating ammoFamily itself as firearm intent prevents stale or
            // partially-translated content from silently becoming unlimited-ammo melee/basic attack.
            bool ammoFedWeapon = !string.IsNullOrWhiteSpace(weapon.ammoFamily);
            mode = weapon.firearmMagazineCapacity > 0 || ammoFedWeapon
                ? BasicAttackMode.Firearm
                : BasicAttackMode.MeleeWeapon;
            if (mode != BasicAttackMode.Unarmed)
            {
                state.ComboStep = 0;
                state.ComboExpiresAt = 0d;
            }
        }

        private static void RefreshComboExpiry(RuntimeState state, double now)
        {
            if (state.ComboStep == 0 || now <= 0d)
                return;
            if (now > state.ComboExpiresAt)
            {
                state.ComboStep = 0;
                state.ComboExpiresAt = 0d;
            }
        }

        private static CombatOwnerStateSnapshot BuildSnapshot(
            RuntimeState state,
            BasicAttackMode mode,
            ItemDefinition weapon,
            CombatRulesDefinition rules)
        {
            FirearmFireMode fireMode = weapon?.firearmFireMode ?? FirearmFireMode.SemiAutomatic;
            float firearmRoundsPerSecond = weapon != null
                ? FirearmCadenceTiming.ResolveRoundsPerSecond(weapon.firearmRoundsPerSecond, weapon.basicAttackInterval)
                : FirearmCadenceTiming.DefaultRoundsPerSecond;
            int firearmRoundsPerTrigger = weapon != null
                ? FirearmCadenceTiming.ResolveRoundsPerTrigger(fireMode, weapon.firearmRoundsPerTrigger)
                : 1;
            float primaryInterval = mode == BasicAttackMode.Unarmed
                ? BasicAttackCadenceTiming.UnarmedLightInterval
                : mode == BasicAttackMode.Firearm
                    ? FirearmCadenceTiming.ResolveTriggerInterval(fireMode, firearmRoundsPerSecond, weapon?.basicAttackInterval ?? 0f)
                    : BasicAttackCadenceTiming.Clamp(weapon != null && weapon.basicAttackInterval > 0f
                        ? weapon.basicAttackInterval
                        : rules.basicAttackInterval);
            float basicAttackRange = mode == BasicAttackMode.Unarmed
                ? CombatRangePolicy.UnarmedRange
                : weapon != null && CombatRangePolicy.IsValidWeaponRange(mode, weapon.basicAttackRange)
                    ? weapon.basicAttackRange
                    : 0f;

            return new CombatOwnerStateSnapshot(
                true,
                state.Revision,
                mode,
                state.WeaponDefinitionId,
                state.LoadedAmmoDefinitionId,
                state.LoadedRounds,
                weapon?.firearmMagazineCapacity ?? 0,
                Math.Max(0, rules.unarmedLightStaminaCost),
                Math.Max(0, rules.unarmedHeavyStaminaCost),
                primaryInterval,
                BasicAttackCadenceTiming.HeavyInterval,
                state.ComboStep,
                fireMode,
                firearmRoundsPerTrigger,
                firearmRoundsPerSecond,
                weapon?.presentationId ?? 0,
                basicAttackRange);
        }
    }

    public readonly struct CombatOwnerStateSnapshot
    {
        public bool Available { get; }
        public long Revision { get; }
        public BasicAttackMode Mode { get; }
        public string WeaponDefinitionId { get; }
        public string LoadedAmmoDefinitionId { get; }
        public int LoadedRounds { get; }
        public int MagazineCapacity { get; }
        public int UnarmedLightStaminaCost { get; }
        public int UnarmedHeavyStaminaCost { get; }
        public float PrimaryInterval { get; }
        public float HeavyInterval { get; }
        public byte ComboStep { get; }
        public FirearmFireMode FireMode { get; }
        public int FirearmRoundsPerTrigger { get; }
        public float FirearmRoundsPerSecond { get; }
        public ushort FirearmPresentationId { get; }
        public float BasicAttackRange { get; }

        public CombatOwnerStateSnapshot(
            bool available,
            long revision,
            BasicAttackMode mode,
            string weaponDefinitionId,
            string loadedAmmoDefinitionId,
            int loadedRounds,
            int magazineCapacity,
            int unarmedLightStaminaCost,
            int unarmedHeavyStaminaCost,
            float primaryInterval,
            float heavyInterval,
            byte comboStep,
            FirearmFireMode fireMode,
            int firearmRoundsPerTrigger,
            float firearmRoundsPerSecond,
            ushort firearmPresentationId,
            float basicAttackRange)
        {
            Available = available;
            Revision = revision;
            Mode = mode;
            WeaponDefinitionId = weaponDefinitionId ?? string.Empty;
            LoadedAmmoDefinitionId = loadedAmmoDefinitionId ?? string.Empty;
            LoadedRounds = loadedRounds;
            MagazineCapacity = magazineCapacity;
            UnarmedLightStaminaCost = unarmedLightStaminaCost;
            UnarmedHeavyStaminaCost = unarmedHeavyStaminaCost;
            PrimaryInterval = primaryInterval;
            HeavyInterval = heavyInterval;
            ComboStep = comboStep;
            FireMode = fireMode;
            FirearmRoundsPerTrigger = firearmRoundsPerTrigger;
            FirearmRoundsPerSecond = firearmRoundsPerSecond;
            FirearmPresentationId = firearmPresentationId;
            BasicAttackRange = basicAttackRange;
        }

        public static CombatOwnerStateSnapshot Unavailable(CombatRulesDefinition rules) =>
            new CombatOwnerStateSnapshot(
                false, 0, BasicAttackMode.Unarmed, string.Empty, string.Empty, 0, 0,
                Math.Max(0, rules?.unarmedLightStaminaCost ?? 0),
                Math.Max(0, rules?.unarmedHeavyStaminaCost ?? 0),
                BasicAttackCadenceTiming.LightInterval,
                BasicAttackCadenceTiming.HeavyInterval,
                0,
                FirearmFireMode.SemiAutomatic,
                1,
                FirearmCadenceTiming.DefaultRoundsPerSecond,
                0,
                CombatRangePolicy.UnarmedRange);
    }
}
