using System;

namespace Game.Server.Application.World
{
    /// <summary>
    /// Opaque world-runtime handle owned by the outer integration adapter.
    /// The application layer never assumes that this is a Unity object ID,
    /// LiteNetLib object ID, ECS entity, or future .NET-world entity ID.
    /// </summary>
    public readonly struct PlayerWorldHandle : IEquatable<PlayerWorldHandle>
    {
        public ulong Value { get; }
        public bool IsValid => Value != 0;

        public PlayerWorldHandle(ulong value)
        {
            if (value == 0)
                throw new ArgumentOutOfRangeException(nameof(value), "PlayerWorldHandle must be non-zero.");
            Value = value;
        }

        public bool Equals(PlayerWorldHandle other) => Value == other.Value;
        public override bool Equals(object obj) => obj is PlayerWorldHandle other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(PlayerWorldHandle left, PlayerWorldHandle right) => left.Equals(right);
        public static bool operator !=(PlayerWorldHandle left, PlayerWorldHandle right) => !left.Equals(right);
    }
}
