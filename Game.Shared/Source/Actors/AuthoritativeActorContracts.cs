using System;
using System.Runtime.Serialization;
using Game.Shared.World;

namespace Game.Shared.Actors
{
    public enum AuthoritativeActorKind : byte
    {
        None = 0,
        Player = 1,
        Monster = 2,
        Npc = 3,
        Population = 4,
        Pet = 5,
        Vehicle = 6,
    }

    public enum ActorFaction : byte
    {
        Neutral = 0,
        Human = 1,
        Vampire = 2,
        Hunter = 3,
        Undead = 4,
        Police = 5,
        Civilian = 6,
        Criminal = 7,
        Gang = 8,
        Monster = 9,
    }

    public enum ActorRelationshipDisposition : sbyte
    {
        Hostile = -2,
        Unfriendly = -1,
        Neutral = 0,
        Friendly = 1,
        Allied = 2,
    }

    public enum ActorMovementMode : byte
    {
        Grounded = 0,
        Falling = 1,
        Jumping = 2,
        Vaulting = 3,
        Ladder = 4,
        InteractionLocked = 5,
        Mounted = 6,
        Dead = 7,
    }

    public enum ActorWeightClass : byte
    {
        Light = 0,
        Normal = 1,
        Heavy = 2,
        VeryHeavy = 3,
        Immovable = 4,
    }

    [Serializable, DataContract]
    public struct AuthoritativeActorHandle : IEquatable<AuthoritativeActorHandle>
    {
        [DataMember(Name = "actorId")] public long actorId;
        [DataMember(Name = "generation")] public ushort generation;
        [DataMember(Name = "kind")] public AuthoritativeActorKind kind;

        public bool IsValid => actorId > 0 && generation > 0 && kind != AuthoritativeActorKind.None;

        public AuthoritativeActorHandle(long actorId, ushort generation, AuthoritativeActorKind kind)
        {
            this.actorId = actorId;
            this.generation = generation;
            this.kind = kind;
        }

        public bool Equals(AuthoritativeActorHandle other) =>
            actorId == other.actorId && generation == other.generation && kind == other.kind;
        public override bool Equals(object obj) => obj is AuthoritativeActorHandle other && Equals(other);
        public override int GetHashCode() => unchecked((actorId.GetHashCode() * 397) ^ generation ^ ((int)kind << 24));
    }

    [Serializable, DataContract]
    public sealed class AuthoritativeActorSnapshot
    {
        [DataMember(Name = "handle")] public AuthoritativeActorHandle handle;
        [DataMember(Name = "archetypeId")] public string archetypeId = string.Empty;
        [DataMember(Name = "displayName")] public string displayName = string.Empty;
        [DataMember(Name = "faction")] public ActorFaction faction;
        [DataMember(Name = "mapId")] public string mapId = string.Empty;
        [DataMember(Name = "instanceId")] public string instanceId = string.Empty;
        [DataMember(Name = "positionX")] public float positionX;
        [DataMember(Name = "positionY")] public float positionY;
        [DataMember(Name = "positionZ")] public float positionZ;
        [DataMember(Name = "yawDegrees")] public float yawDegrees;
        [DataMember(Name = "velocityX")] public float velocityX;
        [DataMember(Name = "velocityY")] public float velocityY;
        [DataMember(Name = "velocityZ")] public float velocityZ;
        [DataMember(Name = "movementMode")] public ActorMovementMode movementMode;
        [DataMember(Name = "weightClass")] public ActorWeightClass weightClass = ActorWeightClass.Normal;
        [DataMember(Name = "healthCurrent")] public int healthCurrent;
        [DataMember(Name = "healthMaximum")] public int healthMaximum;
        [DataMember(Name = "alive")] public bool alive = true;
        [DataMember(Name = "revision")] public long revision;

        public WorldPosition Position => new WorldPosition(positionX, positionY, positionZ);
    }
}
