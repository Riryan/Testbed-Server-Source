using System;
using System.Runtime.Serialization;

namespace Game.Shared.Presentation
{
    /// <summary>
    /// Sustained physical posture. Transitioning into/out of a posture is an Action;
    /// this value lets late observers reconstruct the actor's current state.
    /// </summary>
    public enum ActorPosture : byte
    {
        Standing = 0,
        Crouching = 1,
        Sitting = 2,
        Lying = 3,
        Mounted = 4,
        Swimming = 5,
        Dead = 6,
    }

    [Flags]
    public enum ActorActionFlags : byte
    {
        None = 0,
        Looping = 1 << 0,
        InteractionBound = 1 << 1,
        FullBodyOverride = 1 << 2,
    }

    /// <summary>
    /// Compact semantic "what is this actor doing?" state. The server may replicate
    /// these fields in a delta/presence-masked snapshot or rare state lane. Clients
    /// resolve ActionId into animations, expressions, IK, props, audio and FX locally.
    /// No Animator hashes, clips, morph curves, or Unity presentation data belong here.
    /// </summary>
    [Serializable, DataContract]
    public struct ActorPresentationState
    {
        [DataMember(Name = "posture")] public ActorPosture posture;
        [DataMember(Name = "actionId")] public ushort actionId;
        [DataMember(Name = "actionSequence")] public ushort actionSequence;
        [DataMember(Name = "actionStartTick")] public uint actionStartTick;
        [DataMember(Name = "actionFlags")] public ActorActionFlags actionFlags;

        // Zero means the action is not bound to a synchronized interaction session.
        [DataMember(Name = "interactionSessionId")] public ulong interactionSessionId;
        [DataMember(Name = "interactionRoleId")] public byte interactionRoleId;

        public bool HasAction => actionId != 0;
        public bool HasInteraction => interactionSessionId != 0;
    }
}
