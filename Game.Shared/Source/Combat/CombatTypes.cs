using System;

namespace Game.Shared.Combat
{
    // Stable values retained from the uMMORPG authoritative damage contract.
    public enum CombatDamageCause : byte
    {
        Combat = 0,
        SharedStatus = 1,
        Fall = 2,
        Fog = 3,
        ForcedFeeding = 4,
        PopulationBasicAttack = 5,
        PopulationAbility = 6,
        GameMaster = 7,
    }

    public enum CombatDamageResultCode : byte
    {
        RejectedInvalidTarget = 0,
        RejectedInvalidAmount = 1,
        RejectedTargetDead = 2,
        RejectedInvincible = 3,
        RejectedNoEffectiveDamage = 4,
        Applied = 5,
        Blocked = 6,
        Killed = 7,
    }

    public enum CombatDamageType : byte
    {
        Normal = 0,
        Critical = 1,
        Block = 2,
    }

    [Flags]
    public enum CombatDamagePresentationFlags : ushort
    {
        None = 0,
        Killed = 1 << 0,
        Critical = 1 << 1,
        Blocked = 1 << 2,
        Stunned = 1 << 3,
        Weakness = 1 << 4,
        Resisted = 1 << 5,
        Immune = 1 << 6,
    }

    [Flags]
    public enum CombatDamagePolicy : ushort
    {
        None = 0,
        RespectInvincibility = 1 << 0,
        ApplyStatusMultipliers = 1 << 1,
        AllowCritical = 1 << 2,
        AllowBlock = 1 << 3,
        ApplyDefense = 1 << 4,
        UpdateSourceCombatTime = 1 << 5,
        UpdateTargetCombatTime = 1 << 6,
        ForceLethal = 1 << 7,
    }
}
