using System;

namespace Player.Shared
{
    public static class PlayerEntityProtocol
    {
        public const ushort Version = 14;
        public const ushort DefaultTickRate = 20;
    }

    /// <summary>
    /// Bootstrap fallbacks used only until the authoritative Gameplay Settings snapshot arrives.
    /// Production movement tuning is data-driven from GameplayContent.json and synchronized by the GameServer.
    /// </summary>
    public static class PlayerMovementDefaults
    {
        public const float WalkSpeed = 3.0f;
        public const float SprintSpeed = 5.95f;
        public const float Gravity = 24f;
        public const float JumpSpeed = 7f;
    }

    public enum PlayerEntityMoveState : byte
    {
        Idle = 0,
        Moving = 1,
        Airborne = 2,
        Crouched = 3,
        Swimming = 4,
        Mounted = 5,
        Dead = 6,
    }

    public enum PlayerEntityActionState : byte
    {
        None = 0,
        Attacking = 1,
        Casting = 2,
        Interacting = 3,
        Stunned = 4,
        Dead = 5,
        Reloading = 6,
    }

    [Flags]
    public enum PlayerCombatInputFlags : byte
    {
        None = 0,
        FireHeld = 1 << 0,
        AimHeld = 1 << 1,
    }

    [Flags]
    public enum PlayerEntityFlags : byte
    {
        None = 0,
        Grounded = 1 << 0,
        Sprinting = 1 << 1,
        Crouching = 1 << 2,
        Jumping = 1 << 3,
        Teleport = 1 << 4,
        InCombat = 1 << 5,

        // CombatReady is a presentation-facing name for the existing combat-state bit.
        // PlayerEntitySnapshot.flags is serialized as one byte and all eight bits are
        // already allocated, so this must remain an alias rather than a new bit.
        // The owner client may toggle this bit on its local presentation-copy of a
        // snapshot without changing authoritative/server state.
        CombatReady = InCombat,

        Hidden = 1 << 6,
        Running = 1 << 7,
    }
}
