using System;

namespace Game.Shared.Identity
{
    public readonly struct CharacterId : IEquatable<CharacterId>
    {
        public long Value { get; }
        public bool IsValid => Value > 0;

        public CharacterId(long value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "CharacterId must be greater than zero.");
            Value = value;
        }

        public bool Equals(CharacterId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is CharacterId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(CharacterId left, CharacterId right) => left.Equals(right);
        public static bool operator !=(CharacterId left, CharacterId right) => !left.Equals(right);
    }
}
