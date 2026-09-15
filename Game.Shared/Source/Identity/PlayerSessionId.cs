using System;

namespace Game.Shared.Identity
{
    public readonly struct PlayerSessionId : IEquatable<PlayerSessionId>
    {
        public Guid Value { get; }
        public bool IsValid => Value != Guid.Empty;

        public PlayerSessionId(Guid value)
        {
            if (value == Guid.Empty)
                throw new ArgumentException("PlayerSessionId cannot be empty.", nameof(value));
            Value = value;
        }

        public static PlayerSessionId New() => new PlayerSessionId(Guid.NewGuid());

        public bool Equals(PlayerSessionId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is PlayerSessionId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("N");
        public static bool operator ==(PlayerSessionId left, PlayerSessionId right) => left.Equals(right);
        public static bool operator !=(PlayerSessionId left, PlayerSessionId right) => !left.Equals(right);
    }
}
