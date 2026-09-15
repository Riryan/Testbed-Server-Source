using System;
using Game.Shared.Characters;
using Game.Shared.Population;
using LiteNetLib.Utils;

namespace Player.Networking
{
    /// <summary>
    /// Compact binary codec for the generic appearance recipe. This is used only on
    /// transport boundaries; the shared recipe itself deliberately has no LiteNetLib
    /// dependency so it remains reusable by the standalone server and persistence code.
    /// </summary>
    public static class CharacterAppearanceNetworkCodec
    {
        public static void Write(NetDataWriter writer, CharacterAppearanceRecipe recipe)
        {
            // Transport writes are on a hot observer path. Recipes are validated when
            // accepted into authoritative/client state, so do not clone arrays or allocate
            // validation HashSets every time the same appearance is serialized to an observer.
            CharacterMeshSelection[] meshes = recipe?.meshes ?? Array.Empty<CharacterMeshSelection>();
            CharacterMorphSelection[] morphs = recipe?.morphs ?? Array.Empty<CharacterMorphSelection>();
            CharacterColorSelection[] colors = recipe?.colors ?? Array.Empty<CharacterColorSelection>();

            bool structurallyWritable = recipe != null &&
                recipe.schemaVersion > 0 &&
                recipe.schemaVersion <= CharacterAppearanceRecipe.CurrentSchemaVersion &&
                meshes.Length <= CharacterAppearanceRecipe.MaxMeshSelections &&
                morphs.Length <= CharacterAppearanceRecipe.MaxMorphSelections &&
                colors.Length <= CharacterAppearanceRecipe.MaxColorSelections;

            if (!structurallyWritable)
            {
                writer.PutPackedUShort(CharacterAppearanceRecipe.CurrentSchemaVersion);
                writer.PutPackedUShort(CharacterAppearanceRecipe.DefaultVisualProfileId);
                writer.PutPackedUInt(0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                return;
            }

            writer.PutPackedUShort(recipe.schemaVersion);
            writer.PutPackedUShort(recipe.visualProfileId);
            writer.PutPackedUInt(recipe.revision);

            writer.Put((byte)meshes.Length);
            for (int i = 0; i < meshes.Length; ++i)
            {
                writer.PutPackedUShort(meshes[i].slotId);
                writer.PutPackedUShort(meshes[i].optionId);
            }

            writer.Put((byte)morphs.Length);
            for (int i = 0; i < morphs.Length; ++i)
            {
                writer.PutPackedUShort(morphs[i].channelId);
                writer.Put(morphs[i].value);
            }

            writer.Put((byte)colors.Length);
            for (int i = 0; i < colors.Length; ++i)
            {
                writer.PutPackedUShort(colors[i].channelId);
                writer.Put((byte)colors[i].encoding);
                writer.PutPackedUInt(colors[i].value);
            }
        }

        public static CharacterAppearanceRecipe Read(NetDataReader reader)
        {
            var result = new CharacterAppearanceRecipe
            {
                schemaVersion = reader.GetPackedUShort(),
                visualProfileId = reader.GetPackedUShort(),
                revision = reader.GetPackedUInt(),
            };

            int meshCount = reader.GetByte();
            if (meshCount > CharacterAppearanceRecipe.MaxMeshSelections)
                throw new InvalidOperationException("Appearance mesh selection count exceeds protocol maximum.");
            result.meshes = new CharacterMeshSelection[meshCount];
            for (int i = 0; i < meshCount; ++i)
                result.meshes[i] = new CharacterMeshSelection(reader.GetPackedUShort(), reader.GetPackedUShort());

            int morphCount = reader.GetByte();
            if (morphCount > CharacterAppearanceRecipe.MaxMorphSelections)
                throw new InvalidOperationException("Appearance morph selection count exceeds protocol maximum.");
            result.morphs = new CharacterMorphSelection[morphCount];
            for (int i = 0; i < morphCount; ++i)
                result.morphs[i] = new CharacterMorphSelection(reader.GetPackedUShort(), reader.GetByte());

            int colorCount = reader.GetByte();
            if (colorCount > CharacterAppearanceRecipe.MaxColorSelections)
                throw new InvalidOperationException("Appearance color selection count exceeds protocol maximum.");
            result.colors = new CharacterColorSelection[colorCount];
            for (int i = 0; i < colorCount; ++i)
            {
                ushort channelId = reader.GetPackedUShort();
                CharacterColorEncoding encoding = (CharacterColorEncoding)reader.GetByte();
                uint value = reader.GetPackedUInt();
                result.colors[i] = new CharacterColorSelection(channelId, encoding, value);
            }

            if (!result.IsValid(out string error))
                throw new InvalidOperationException("Invalid appearance recipe: " + error);
            return result;
        }
    }


    public static class CharacterPresentationPreferencesNetworkCodec
    {
        public static void Write(NetDataWriter writer, CharacterPresentationPreferences preferences)
        {
            // This rare definition can fan out to many observers. Avoid allocating a clone
            // for every observer write; authoritative/client acceptance validates the value.
            bool writable = preferences != null &&
                preferences.schemaVersion > 0 &&
                preferences.schemaVersion <= CharacterPresentationPreferences.CurrentSchemaVersion;
            if (!writable)
            {
                writer.PutPackedUShort(CharacterPresentationPreferences.CurrentSchemaVersion);
                writer.PutPackedUInt(0);
                writer.Put((byte)0);
                return;
            }

            writer.PutPackedUShort(preferences.schemaVersion);
            writer.PutPackedUInt(preferences.revision);
            writer.Put(preferences.movementStyle);
        }

        public static CharacterPresentationPreferences Read(NetDataReader reader)
        {
            var value = new CharacterPresentationPreferences
            {
                schemaVersion = reader.GetPackedUShort(),
                revision = reader.GetPackedUInt(),
                movementStyle = reader.GetByte(),
            };
            if (!value.IsValid(out string error))
                throw new InvalidOperationException("Invalid character presentation preferences: " + error);
            return value;
        }
    }

    /// <summary>
    /// Compact equipment presentation selection. The authoritative server sends only stable
    /// content presentation ids; the Unity client resolves those ids to meshes/prefabs locally.
    /// This state is rare/event-driven and is never included in movement snapshots.
    /// </summary>
    [Serializable]
    public struct PlayerEquipmentVisualSelection
    {
        public ushort equipmentSlotPresentationId;
        public ushort itemPresentationId;

        public PlayerEquipmentVisualSelection(ushort equipmentSlotPresentationId, ushort itemPresentationId)
        {
            this.equipmentSlotPresentationId = equipmentSlotPresentationId;
            this.itemPresentationId = itemPresentationId;
        }
    }

    /// <summary>
    /// Rare server-to-observer definition state. It is revision-gated by the sync field
    /// and contains no JSON, Unity asset names, blendshape names, or animation details.
    /// </summary>
    [Serializable]
    public struct PlayerEntityAppearance : INetSerializable, IEquatable<PlayerEntityAppearance>
    {
        public const int MaxEquipmentVisualSelections = 16;

        public ushort generation;
        public uint sequence;
        public long characterId;
        public uint equipmentVersion;
        public string displayName;
        public string guildName;
        public PopulationPublicInteractionFlags populationInteractionFlags;
        public PlayerEquipmentVisualSelection[] equipmentVisuals;
        public CharacterAppearanceRecipe appearance;
        public CharacterPresentationPreferences presentation;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(generation);
            writer.PutPackedUInt(sequence);
            writer.PutPackedLong(characterId > 0 ? characterId : 0L);
            writer.PutPackedUInt(equipmentVersion);
            writer.Put(displayName ?? string.Empty);
            writer.Put(guildName ?? string.Empty);
            writer.Put((byte)populationInteractionFlags);

            PlayerEquipmentVisualSelection[] equipment = equipmentVisuals ?? Array.Empty<PlayerEquipmentVisualSelection>();
            int equipmentCount = Math.Min(equipment.Length, MaxEquipmentVisualSelections);
            writer.Put((byte)equipmentCount);
            for (int i = 0; i < equipmentCount; ++i)
            {
                writer.PutPackedUShort(equipment[i].equipmentSlotPresentationId);
                writer.PutPackedUShort(equipment[i].itemPresentationId);
            }

            CharacterAppearanceNetworkCodec.Write(writer, appearance);
            CharacterPresentationPreferencesNetworkCodec.Write(writer, presentation);
        }

        public void Deserialize(NetDataReader reader)
        {
            generation = reader.GetUShort();
            sequence = reader.GetPackedUInt();
            long packedCharacterId = reader.GetPackedLong();
            characterId = packedCharacterId > 0 ? packedCharacterId : 0L;
            equipmentVersion = reader.GetPackedUInt();
            displayName = reader.GetString(128);
            guildName = reader.GetString(128);
            populationInteractionFlags = (PopulationPublicInteractionFlags)reader.GetByte();

            int equipmentCount = reader.GetByte();
            if (equipmentCount > MaxEquipmentVisualSelections)
                throw new InvalidOperationException("Equipment visual selection count exceeds protocol maximum.");
            equipmentVisuals = new PlayerEquipmentVisualSelection[equipmentCount];
            for (int i = 0; i < equipmentCount; ++i)
            {
                equipmentVisuals[i] = new PlayerEquipmentVisualSelection(
                    reader.GetPackedUShort(),
                    reader.GetPackedUShort());
            }

            appearance = CharacterAppearanceNetworkCodec.Read(reader);
            presentation = CharacterPresentationPreferencesNetworkCodec.Read(reader);
        }

        public bool Equals(PlayerEntityAppearance other) =>
            generation == other.generation &&
            sequence == other.sequence &&
            characterId == other.characterId &&
            equipmentVersion == other.equipmentVersion &&
            string.Equals(displayName, other.displayName, StringComparison.Ordinal) &&
            string.Equals(guildName, other.guildName, StringComparison.Ordinal) &&
            populationInteractionFlags == other.populationInteractionFlags;

        public override bool Equals(object obj) => obj is PlayerEntityAppearance other && Equals(other);
        public override int GetHashCode() => unchecked(generation * 397 ^ (int)sequence ^ characterId.GetHashCode());
    }
}
