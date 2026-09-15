using System;
using System.Runtime.Serialization;
using Game.Shared.Content;

namespace Game.Shared.Progression
{
    [Serializable, DataContract]
    public sealed class ProgressTrackState
    {
        [DataMember(Name = "dataId")] public ushort dataId;
        [DataMember(Name = "value")] public int value;
        [DataMember(Name = "mode")] public ProgressTrackMode mode;
        [DataMember(Name = "lastChangedUtcTicks")] public long lastChangedUtcTicks;

        public ProgressTrackState Clone() => (ProgressTrackState)MemberwiseClone();
    }

    [Serializable, DataContract]
    public sealed class ReputationState
    {
        [DataMember(Name = "factionDataId")] public ushort factionDataId;
        [DataMember(Name = "value")] public int value;
        public ReputationState Clone() => (ReputationState)MemberwiseClone();
    }

    [Serializable, DataContract]
    public sealed class HeatState
    {
        [DataMember(Name = "jurisdictionDataId")] public ushort jurisdictionDataId;
        [DataMember(Name = "value")] public int value;
        [DataMember(Name = "bounty")] public long bounty;
        [DataMember(Name = "evidence")] public int evidence;
        [DataMember(Name = "updatedUtcTicks")] public long updatedUtcTicks;
        public HeatState Clone() => (HeatState)MemberwiseClone();
    }

    /// <summary>Character-owned soft gameplay breadth state. Persisted by the normal player coalescer.</summary>
    [Serializable, DataContract]
    public sealed class CharacterProgressionState
    {
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "experience")] public long experience;
        [DataMember(Name = "level")] public int level = 1;
        [DataMember(Name = "factionDataId")] public ushort factionDataId;
        [DataMember(Name = "tracks")] public ProgressTrackState[] tracks = Array.Empty<ProgressTrackState>();
        [DataMember(Name = "reputation")] public ReputationState[] reputation = Array.Empty<ReputationState>();
        [DataMember(Name = "heat")] public HeatState[] heat = Array.Empty<HeatState>();
        [DataMember(Name = "knownRecipeDataIds")] public ushort[] knownRecipeDataIds = Array.Empty<ushort>();

        public CharacterProgressionState Clone()
        {
            var copy = new CharacterProgressionState
            {
                revision = revision,
                experience = experience,
                level = level,
                factionDataId = factionDataId,
                tracks = CloneArray(tracks),
                reputation = CloneArray(reputation),
                heat = CloneArray(heat),
                knownRecipeDataIds = knownRecipeDataIds == null ? Array.Empty<ushort>() : (ushort[])knownRecipeDataIds.Clone(),
            };
            return copy;
        }

        public static CharacterProgressionState CreateDefault() => new CharacterProgressionState { level = 1 };

        private static ProgressTrackState[] CloneArray(ProgressTrackState[] source)
        {
            source = source ?? Array.Empty<ProgressTrackState>();
            var result = new ProgressTrackState[source.Length];
            for (int i = 0; i < source.Length; ++i) result[i] = source[i]?.Clone();
            return result;
        }
        private static ReputationState[] CloneArray(ReputationState[] source)
        {
            source = source ?? Array.Empty<ReputationState>();
            var result = new ReputationState[source.Length];
            for (int i = 0; i < source.Length; ++i) result[i] = source[i]?.Clone();
            return result;
        }
        private static HeatState[] CloneArray(HeatState[] source)
        {
            source = source ?? Array.Empty<HeatState>();
            var result = new HeatState[source.Length];
            for (int i = 0; i < source.Length; ++i) result[i] = source[i]?.Clone();
            return result;
        }
    }
}
