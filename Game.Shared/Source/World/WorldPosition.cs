using System;

namespace Game.Shared.World
{
    public readonly struct WorldPosition : IEquatable<WorldPosition>
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public WorldPosition(float x, float y, float z)
        {
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                throw new ArgumentOutOfRangeException(nameof(x), "WorldPosition components must be finite.");
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(WorldPosition other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is WorldPosition other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"({X}, {Y}, {Z})";
        public static bool operator ==(WorldPosition left, WorldPosition right) => left.Equals(right);
        public static bool operator !=(WorldPosition left, WorldPosition right) => !left.Equals(right);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
