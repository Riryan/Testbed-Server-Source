using System;

namespace Game.Server.Application.Connections
{
    // Transport-neutral wrapper. LiteNetLib connection IDs are converted to this
    // at the integration boundary instead of leaking transport types inward.
    public readonly struct ConnectionKey : IEquatable<ConnectionKey>
    {
        public long Value { get; }
        public bool IsValid => Value >= 0;

        public ConnectionKey(long value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "ConnectionKey cannot be negative.");
            Value = value;
        }

        public bool Equals(ConnectionKey other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ConnectionKey other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(ConnectionKey left, ConnectionKey right) => left.Equals(right);
        public static bool operator !=(ConnectionKey left, ConnectionKey right) => !left.Equals(right);
    }
}
