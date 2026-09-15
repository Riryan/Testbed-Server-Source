using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Game.Shared.Characters
{
    public static class CharacterPersistenceSchema
    {
        public const int CurrentVersion = 4;
    }

    public enum CharacterColorEncoding : byte
    {
        Rgba32 = 0,
        PaletteIndex = 1,
    }

    [Serializable, DataContract]
    public struct CharacterMeshSelection
    {
        [DataMember(Name = "slotId")] public ushort slotId;
        [DataMember(Name = "optionId")] public ushort optionId;

        public CharacterMeshSelection(ushort slotId, ushort optionId)
        {
            this.slotId = slotId;
            this.optionId = optionId;
        }
    }

    [Serializable, DataContract]
    public struct CharacterMorphSelection
    {
        [DataMember(Name = "channelId")] public ushort channelId;
        [DataMember(Name = "value")] public byte value;

        public CharacterMorphSelection(ushort channelId, byte value)
        {
            this.channelId = channelId;
            this.value = value;
        }
    }

    [Serializable, DataContract]
    public struct CharacterColorSelection
    {
        [DataMember(Name = "channelId")] public ushort channelId;
        [DataMember(Name = "encoding")] public CharacterColorEncoding encoding;
        [DataMember(Name = "value")] public uint value;

        public CharacterColorSelection(ushort channelId, CharacterColorEncoding encoding, uint value)
        {
            this.channelId = channelId;
            this.encoding = encoding;
            this.value = value;
        }
    }

    /// <summary>
    /// Character-system-agnostic authoritative appearance recipe. It deliberately
    /// contains semantic ids and compact values only; Unity meshes, materials,
    /// blendshape names, Animator data, and other presentation implementation details
    /// remain client-side in the selected visual profile/adapter.
    /// </summary>
    [Serializable, DataContract]
    public sealed class CharacterAppearanceRecipe
    {
        public const ushort CurrentSchemaVersion = 1;
        public const ushort DefaultVisualProfileId = 0;
        public const int MaxMeshSelections = 64;
        public const int MaxMorphSelections = 96;
        public const int MaxColorSelections = 32;

        [DataMember(Name = "schemaVersion")] public ushort schemaVersion = CurrentSchemaVersion;
        [DataMember(Name = "visualProfileId")] public ushort visualProfileId = DefaultVisualProfileId;
        [DataMember(Name = "revision")] public uint revision;
        [DataMember(Name = "meshes")] public CharacterMeshSelection[] meshes = Array.Empty<CharacterMeshSelection>();
        [DataMember(Name = "morphs")] public CharacterMorphSelection[] morphs = Array.Empty<CharacterMorphSelection>();
        [DataMember(Name = "colors")] public CharacterColorSelection[] colors = Array.Empty<CharacterColorSelection>();

        public static CharacterAppearanceRecipe CreateDefault(ushort visualProfileId = DefaultVisualProfileId) =>
            new CharacterAppearanceRecipe
            {
                schemaVersion = CurrentSchemaVersion,
                visualProfileId = visualProfileId,
                revision = 0,
                meshes = Array.Empty<CharacterMeshSelection>(),
                morphs = Array.Empty<CharacterMorphSelection>(),
                colors = Array.Empty<CharacterColorSelection>(),
            };

        public CharacterAppearanceRecipe Clone() =>
            new CharacterAppearanceRecipe
            {
                schemaVersion = schemaVersion,
                visualProfileId = visualProfileId,
                revision = revision,
                meshes = meshes == null || meshes.Length == 0
                    ? Array.Empty<CharacterMeshSelection>()
                    : (CharacterMeshSelection[])meshes.Clone(),
                morphs = morphs == null || morphs.Length == 0
                    ? Array.Empty<CharacterMorphSelection>()
                    : (CharacterMorphSelection[])morphs.Clone(),
                colors = colors == null || colors.Length == 0
                    ? Array.Empty<CharacterColorSelection>()
                    : (CharacterColorSelection[])colors.Clone(),
            };

        public bool IsValid(out string error)
        {
            if (schemaVersion == 0 || schemaVersion > CurrentSchemaVersion)
            {
                error = "unsupported appearance schema version";
                return false;
            }

            CharacterMeshSelection[] meshValues = meshes ?? Array.Empty<CharacterMeshSelection>();
            CharacterMorphSelection[] morphValues = morphs ?? Array.Empty<CharacterMorphSelection>();
            CharacterColorSelection[] colorValues = colors ?? Array.Empty<CharacterColorSelection>();

            if (meshValues.Length > MaxMeshSelections ||
                morphValues.Length > MaxMorphSelections ||
                colorValues.Length > MaxColorSelections)
            {
                error = "appearance selection count exceeds contract maximum";
                return false;
            }

            var meshSlots = new HashSet<ushort>();
            for (int i = 0; i < meshValues.Length; ++i)
            {
                if (meshValues[i].slotId == 0 || !meshSlots.Add(meshValues[i].slotId))
                {
                    error = "appearance contains an invalid or duplicate mesh slot";
                    return false;
                }
            }

            var morphChannels = new HashSet<ushort>();
            for (int i = 0; i < morphValues.Length; ++i)
            {
                if (morphValues[i].channelId == 0 || !morphChannels.Add(morphValues[i].channelId))
                {
                    error = "appearance contains an invalid or duplicate morph channel";
                    return false;
                }
            }

            var colorChannels = new HashSet<ushort>();
            for (int i = 0; i < colorValues.Length; ++i)
            {
                if (colorValues[i].channelId == 0 || !colorChannels.Add(colorValues[i].channelId) ||
                    (colorValues[i].encoding != CharacterColorEncoding.Rgba32 &&
                     colorValues[i].encoding != CharacterColorEncoding.PaletteIndex))
                {
                    error = "appearance contains an invalid or duplicate color channel";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
