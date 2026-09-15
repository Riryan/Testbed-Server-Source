using System;

namespace Game.Shared.Identity
{
    public readonly struct AccountId : IEquatable<AccountId>
    {
        public long Value { get; }
        public bool IsValid => Value > 0;

        public AccountId(long value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "AccountId must be greater than zero.");
            Value = value;
        }

        public bool Equals(AccountId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AccountId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(AccountId left, AccountId right) => left.Equals(right);
        public static bool operator !=(AccountId left, AccountId right) => !left.Equals(right);
    }
}
