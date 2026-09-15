using System;

namespace Game.Shared.Identity
{
    public readonly struct ItemInstanceId : IEquatable<ItemInstanceId>, IComparable<ItemInstanceId>
    {
        public long Value { get; }
        public bool IsValid => Value > 0;

        public ItemInstanceId(long value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Item instance id must be positive.");
            Value = value;
        }

        public bool Equals(ItemInstanceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ItemInstanceId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(ItemInstanceId other) => Value.CompareTo(other.Value);
        public override string ToString() => Value.ToString();
        public static bool operator ==(ItemInstanceId left, ItemInstanceId right) => left.Equals(right);
        public static bool operator !=(ItemInstanceId left, ItemInstanceId right) => !left.Equals(right);
    }
}
