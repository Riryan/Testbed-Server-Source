using System;
using Game.Shared.World;

namespace Game.Server.Domain.Characters
{
    public readonly struct CharacterLocationState : IEquatable<CharacterLocationState>
    {
        public string MapId { get; }
        public string InstanceId { get; }
        public WorldPosition Position { get; }
        public float YawDegrees { get; }

        public CharacterLocationState(string mapId, string instanceId, WorldPosition position, float yawDegrees)
        {
            string canonicalMapId = ServerMapId.Normalize(mapId);
            if (canonicalMapId.Length == 0)
                throw new ArgumentException("MapId is required.", nameof(mapId));
            if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees))
                throw new ArgumentOutOfRangeException(nameof(yawDegrees), "Yaw must be finite.");

            MapId = canonicalMapId;
            InstanceId = (instanceId ?? string.Empty).Trim();
            Position = position;
            YawDegrees = yawDegrees;
        }

        public bool Equals(CharacterLocationState other) =>
            string.Equals(MapId, other.MapId, StringComparison.Ordinal) &&
            string.Equals(InstanceId, other.InstanceId, StringComparison.Ordinal) &&
            Position.Equals(other.Position) &&
            YawDegrees.Equals(other.YawDegrees);

        public override bool Equals(object obj) => obj is CharacterLocationState other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MapId.GetHashCode();
                hash = (hash * 397) ^ InstanceId.GetHashCode();
                hash = (hash * 397) ^ Position.GetHashCode();
                hash = (hash * 397) ^ YawDegrees.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(CharacterLocationState left, CharacterLocationState right) => left.Equals(right);
        public static bool operator !=(CharacterLocationState left, CharacterLocationState right) => !left.Equals(right);
    }
}
