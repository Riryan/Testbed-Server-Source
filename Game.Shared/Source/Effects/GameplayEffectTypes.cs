using System;

namespace Game.Shared.Effects
{
    /// <summary>Canonical gameplay mutations. Delivery/targeting are owned by the action; effects own what changes.</summary>
    public enum GameplayEffectKind : byte
    {
        Damage = 0,
        Heal = 1,
        ResourceChange = 2,
        StatModifier = 3,
        ApplyStatusEffect = 4,
        RemoveStatusEffect = 5,
        Control = 6,
    }

    /// <summary>When an effect inside a status definition executes.</summary>
    public enum GameplayEffectTiming : byte
    {
        Instant = 0,
        Periodic = 1,
        WhileActive = 2,
    }

    public enum GameplayPulseTiming : byte
    {
        Delayed = 0,
        Immediate = 1,
    }

    public enum ResourceChangeOperation : byte
    {
        Add = 0,
        Subtract = 1,
        Set = 2,
    }

    public enum StatModifierOperation : byte
    {
        FlatAdd = 0,
        PercentAdd = 1,
        Multiply = 2,
        Override = 3,
    }

    public enum ControlEffectType : byte
    {
        None = 0,
        Stun = 1,
        Root = 2,
        Silence = 3,
        Disarm = 4,
        Knockback = 5,
        Knockdown = 6,
        Fear = 7,
        Sleep = 8,
    }

    public enum AbilityDeliveryType : byte
    {
        Self = 0,
        Direct = 1,
        Melee = 2,
        Hitscan = 3,
        VisibleProjectile = 4,
        Area = 5,
        Cone = 6,
        Line = 7,
        Chain = 8,
        GroundTarget = 9,
    }

    /// <summary>
    /// Only visibly travelling objects use projectile presentation traffic. Normal firearm bullets are None.
    /// </summary>
    public enum VisibleProjectileMode : byte
    {
        None = 0,
        EntityTarget = 1,
        WorldPosition = 2,
        Direction = 3,
    }

    [Flags]
    public enum GameplayEffectFlags : ushort
    {
        None = 0,
        CanCrit = 1 << 0,
        CanBlock = 1 << 1,
        CanBeResisted = 1 << 2,
        CanTriggerWeakness = 1 << 3,
    }
}
