using System;
using System.Runtime.Serialization;

namespace Game.Shared.Characters
{
    /// <summary>
    /// Character-owned presentation preferences that are authoritative/persistent but
    /// remain presentation-semantic only. They deliberately contain no Animator hashes,
    /// animation clip names, Unity assets, or sex/gender assumptions.
    /// </summary>
    [Serializable, DataContract]
    public sealed class CharacterPresentationPreferences
    {
        public const ushort CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion")] public ushort schemaVersion = CurrentSchemaVersion;
        [DataMember(Name = "revision")] public uint revision;

        // 0..255 maps to the client humanoid movement-style blend. The current canonical
        // PlayerHumanoid adapter maps this to Animator float BodyAnimationStyle 0..1.
        [DataMember(Name = "movementStyle")] public byte movementStyle;

        public static CharacterPresentationPreferences CreateDefault() =>
            new CharacterPresentationPreferences
            {
                schemaVersion = CurrentSchemaVersion,
                revision = 0,
                movementStyle = 0,
            };

        public CharacterPresentationPreferences Clone() =>
            new CharacterPresentationPreferences
            {
                schemaVersion = schemaVersion,
                revision = revision,
                movementStyle = movementStyle,
            };

        public bool IsValid(out string error)
        {
            if (schemaVersion == 0 || schemaVersion > CurrentSchemaVersion)
            {
                error = "unsupported character presentation schema version";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
