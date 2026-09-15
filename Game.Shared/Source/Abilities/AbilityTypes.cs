using System;

namespace Game.Shared.Abilities
{
    public enum AbilityCategory : byte
    {
        Damage = 0,
        Healing = 1,
        Buff = 2,
        Utility = 3,
        Movement = 4,
        Summon = 5,
    }

    public enum AbilityTargetMode : byte
    {
        Self = 0,
        Entity = 1,
        Point = 2,
    }

    public enum AbilityTargetRelation : byte
    {
        Self = 0,
        Friendly = 1,
        Hostile = 2,
        AnyLiving = 3,
    }

    public enum AbilityMovementPolicy : byte
    {
        Stationary = 0,
        AllowWalk = 1,
        AllowFullMovement = 2,
        RequireMoving = 3,
        RequireSprinting = 4,
    }

    public enum AbilityCooldownPolicy : byte
    {
        AbilityDefined = 0,
        ActorBasicAttackRate = 1,
        None = 2,
    }

    public enum AbilityCastFailure : byte
    {
        None = 0,
        UnknownAbility = 1,
        NotLearned = 2,
        FactionRestricted = 3,
        ProficiencyTooLow = 4,
        InvalidState = 5,
        AlreadyCasting = 6,
        OnCooldown = 7,
        InsufficientResource = 8,
        InvalidTarget = 9,
        OutOfRange = 10,
        NoLineOfSight = 11,
        WeaponRequirement = 12,
        EffectValidationFailed = 13,
        Interrupted = 14,
        MovementRequired = 15,
        SprintingRequired = 16,
        SprintingNotAllowed = 17,
        ActorRestricted = 18,
        UnsupportedTargetRelation = 19,
        Controlled = 20,
    }

    public enum AbilityPresentationPhase : byte
    {
        CastStarted = 0,
        CastCompleted = 1,
        CastCancelled = 2,
    }

    public enum AbilityEffectKind : byte
    {
        Damage = 0,
        HealHealth = 1,
        RestoreResource = 2,
        ApplyStatusEffect = 3,
    }

    public enum CharacterActionChangeReason : byte
    {
        None = 0,
        BasicAttackCommitted = 1,
        AbilityCastStarted = 2,
        AbilityCastCompleted = 3,
        AbilityCastCancelled = 4,
        AbilityCooldownCommitted = 5,
    }

    public enum BasicAttackInputKind : byte
    {
        Primary = 0,
        Light = 1,
        Heavy = 2,
    }

    public enum BasicAttackMode : byte
    {
        Unarmed = 0,
        MeleeWeapon = 1,
        Firearm = 2,
    }

    /// <summary>
    /// Shared hard-range policy. Client values are request-suppression ceilings only;
    /// the GameServer always resolves and validates authoritative range independently.
    /// </summary>
    public static class CombatRangePolicy
    {
        // Hard authoritative attack ranges. Clients may preflight against these limits,
        // but the GameServer always validates equipped content and authoritative positions.
        public const float UnarmedRange = 2.0f;
        public const float MinimumWeaponRange = 0.1f;
        public const float MaximumMeleeWeaponRange = 4.0f;
        public const float MaximumFirearmRange = 45.0f;
        public const float MeleeVerticalTolerance = 1.5f;
        // Target body radius is treated as edge reach for melee rather than extending a thin ray.
        public const float MeleeTargetBodyRadius = 0.85f;
        public const float UnarmedSwingArcDegrees = 100.0f;
        public const float MeleeWeaponSwingArcDegrees = 120.0f;
        public const float ClientTargetRaycastDistance = 55.0f;

        public static float SwingArcDegreesFor(BasicAttackMode mode)
        {
            switch (mode)
            {
                case BasicAttackMode.Unarmed: return UnarmedSwingArcDegrees;
                case BasicAttackMode.MeleeWeapon: return MeleeWeaponSwingArcDegrees;
                default: return 0f;
            }
        }

        public static float MaximumFor(BasicAttackMode mode)
        {
            switch (mode)
            {
                case BasicAttackMode.Unarmed: return UnarmedRange;
                case BasicAttackMode.MeleeWeapon: return MaximumMeleeWeaponRange;
                case BasicAttackMode.Firearm: return MaximumFirearmRange;
                default: return 0f;
            }
        }

        public static bool IsValidWeaponRange(BasicAttackMode mode, float range)
        {
            if (mode != BasicAttackMode.MeleeWeapon && mode != BasicAttackMode.Firearm)
                return false;
            if (float.IsNaN(range) || float.IsInfinity(range) || range < MinimumWeaponRange)
                return false;
            return range <= MaximumFor(mode);
        }

        // Compatibility for BasicAttackService versions that resolve through this helper.
        // Invalid/missing authored weapon ranges fail closed instead of receiving a fallback.
        public static float ResolveServerRange(BasicAttackMode mode, float authoredRange)
        {
            if (mode == BasicAttackMode.Unarmed)
                return UnarmedRange;
            return IsValidWeaponRange(mode, authoredRange) ? authoredRange : 0f;
        }

        // Request-suppression ceiling only. This never grants attack authority.
        public static float ClientRequestRangeForMode(BasicAttackMode mode) => MaximumFor(mode);
    }

    /// <summary>Authoritative firearm trigger behavior. One client trigger action may resolve multiple server-side rounds.</summary>
    public enum FirearmFireMode : byte
    {
        SemiAutomatic = 0,
        Burst = 1,
        FullAutomatic = 2,
    }

    public static class FirearmCadenceTiming
    {
        public const float MinimumRoundsPerSecond = 0.5f;
        public const float MaximumRoundsPerSecond = 60f;
        public const float DefaultRoundsPerSecond = 10f;
        public const float MinimumTriggerInterval = 0.05f;
        public const float MaximumTriggerInterval = 5f;

        public static float ResolveRoundsPerSecond(float authoredRoundsPerSecond, float legacyInterval)
        {
            if (!float.IsNaN(authoredRoundsPerSecond) && !float.IsInfinity(authoredRoundsPerSecond) && authoredRoundsPerSecond > 0f)
                return ClampRoundsPerSecond(authoredRoundsPerSecond);
            if (!float.IsNaN(legacyInterval) && !float.IsInfinity(legacyInterval) && legacyInterval > 0f)
                return ClampRoundsPerSecond(1f / Math.Max(MinimumTriggerInterval, legacyInterval));
            return DefaultRoundsPerSecond;
        }

        public static float ResolveTriggerInterval(FirearmFireMode mode, float roundsPerSecond, float legacyInterval)
        {
            float rps = ResolveRoundsPerSecond(roundsPerSecond, legacyInterval);
            if (mode == FirearmFireMode.FullAutomatic)
                return MinimumTriggerInterval;
            if (!float.IsNaN(legacyInterval) && !float.IsInfinity(legacyInterval) && legacyInterval > 0f)
                return Math.Max(MinimumTriggerInterval, Math.Min(MaximumTriggerInterval, legacyInterval));
            return Math.Max(MinimumTriggerInterval, Math.Min(MaximumTriggerInterval, 1f / rps));
        }

        public static int ResolveRoundsPerTrigger(FirearmFireMode mode, int authored)
        {
            if (mode == FirearmFireMode.Burst)
                return Math.Max(2, Math.Min(31, authored > 0 ? authored : 3));
            return 1;
        }

        public static float ClampRoundsPerSecond(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                return DefaultRoundsPerSecond;
            return Math.Max(MinimumRoundsPerSecond, Math.Min(MaximumRoundsPerSecond, value));
        }
    }

    public enum BasicAttackCadenceProfile : byte
    {
        Standard = 0,
        Light = 1,
        Heavy = 2,
        VeryHeavy = 3,
        Custom = 4,
    }

    /// <summary>Portable version of the slow authoritative uMMORPG attack cadence.</summary>
    public static class BasicAttackCadenceTiming
    {
        public const float MinimumInterval = 1.25f;
        public const float MaximumInterval = 10f;
        public const float LightInterval = 1.50f;
        public const float UnarmedLightInterval = 1.35f;
        public const float StandardInterval = 2.00f;
        public const float HeavyInterval = 3.00f;
        public const float VeryHeavyInterval = 4.00f;

        public static float Resolve(BasicAttackCadenceProfile profile, float customInterval = StandardInterval)
        {
            switch (profile)
            {
                case BasicAttackCadenceProfile.Light: return LightInterval;
                case BasicAttackCadenceProfile.Heavy: return HeavyInterval;
                case BasicAttackCadenceProfile.VeryHeavy: return VeryHeavyInterval;
                case BasicAttackCadenceProfile.Custom: return Clamp(customInterval);
                default: return StandardInterval;
            }
        }

        public static float Clamp(float interval)
        {
            if (float.IsNaN(interval) || float.IsInfinity(interval))
                interval = StandardInterval;
            if (interval < MinimumInterval) return MinimumInterval;
            if (interval > MaximumInterval) return MaximumInterval;
            return interval;
        }
    }
}
