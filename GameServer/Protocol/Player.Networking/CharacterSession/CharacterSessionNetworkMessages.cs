using System;
using Game.Shared.Characters;
using LiteNetLib.Utils;

namespace Player.Networking
{
    /// <summary>
    /// Request ids owned by the Player.Networking integration layer. These are kept
    /// outside LiteNetLibManager's built-in GameReqTypes so gameplay/session requests
    /// do not modify networking-core protocol ownership.
    /// </summary>
    public static class CharacterSessionRequestTypes
    {
        public const ushort CharacterList = 100;
        public const ushort EnterCharacter = 101;
        public const ushort AuthenticateAdmission = 102;
        // 103 was the temporary direct CreateAccount-over-LiteNetLib request. It is
        // intentionally not reused so old clients cannot reinterpret a new request type.
        public const ushort ReservedLegacyCreateAccount = 103;
        public const ushort CreateCharacter = 104;
        public const ushort DeleteCharacter = 105;
    }


    [Serializable]
    public struct AdmissionAuthenticationRequestMessage : INetSerializable
    {
        public string admissionToken;

        public void Serialize(NetDataWriter writer) =>
            writer.Put(admissionToken ?? string.Empty);

        public void Deserialize(NetDataReader reader) =>
            admissionToken = reader.GetString(512);
    }

    [Serializable]
    public struct AdmissionAuthenticationResponseMessage : INetSerializable
    {
        public bool success;
        public long accountId;
        public byte sessionState;
        public string error;

        // Stable fingerprint of the authoritative character-select roster at admission time.
        // Zero means the sender does not support roster reconciliation or could not resolve
        // the roster, in which case the client safely falls back to one CharacterList request.
        // Kept as an optional trailing field for protocol compatibility with older peers.
        public long rosterRevision;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(accountId);
            writer.Put(sessionState);
            writer.Put(error ?? string.Empty);
            writer.Put(rosterRevision);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            accountId = reader.GetLong();
            sessionState = reader.GetByte();
            error = reader.GetString(256);
            rosterRevision = reader != null && reader.AvailableBytes >= sizeof(long)
                ? reader.GetLong()
                : 0L;
        }

        public static AdmissionAuthenticationResponseMessage Failed(byte state, string errorMessage) =>
            new AdmissionAuthenticationResponseMessage
            {
                success = false,
                accountId = 0,
                sessionState = state,
                error = errorMessage ?? string.Empty,
                rosterRevision = 0L,
            };
    }

    [Serializable]
    public struct CharacterListRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    [Serializable]
    public struct CharacterSessionCharacterSummary : INetSerializable
    {
        public long characterId;
        public string name;
        public string mapId;

        public CharacterSessionCharacterSummary(long characterId, string name, string mapId)
        {
            this.characterId = characterId;
            this.name = name ?? string.Empty;
            this.mapId = mapId ?? string.Empty;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(characterId);
            writer.Put(name ?? string.Empty);
            writer.Put(mapId ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            characterId = reader.GetLong();
            name = reader.GetString(128);
            mapId = reader.GetString(128);
        }
    }


    /// <summary>
    /// Stable order-independent fingerprint of character-select roster state. This is a
    /// cache reconciliation token only, never authority. Membership, display name, and map
    /// are included because all three are rendered by Character Select.
    /// </summary>
    public static class CharacterRosterStateRevision
    {
        public static long Compute(CharacterSessionCharacterSummary[] characters)
        {
            CharacterSessionCharacterSummary[] values = characters ?? Array.Empty<CharacterSessionCharacterSummary>();
            ulong xor = 0UL;
            ulong sum = 0UL;
            int count = 0;

            for (int i = 0; i < values.Length; ++i)
            {
                CharacterSessionCharacterSummary value = values[i];
                if (value.characterId <= 0)
                    continue;

                ulong hash = 1469598103934665603UL;
                unchecked
                {
                    ulong id = (ulong)value.characterId;
                    for (int b = 0; b < 8; ++b)
                    {
                        hash ^= (byte)(id >> (b * 8));
                        hash *= 1099511628211UL;
                    }

                    AddString(ref hash, value.name);
                    AddString(ref hash, value.mapId);

                    xor ^= hash;
                    sum += hash * 0x9E3779B185EBCA87UL;
                }
                count++;
            }

            unchecked
            {
                ulong result = 0xA24BAED4963EE407UL ^ (ulong)count;
                result ^= xor + 0x9E3779B97F4A7C15UL + (result << 6) + (result >> 2);
                result ^= sum + 0xC2B2AE3D27D4EB4FUL + (result << 6) + (result >> 2);
                if (result == 0UL)
                    result = 1UL;
                return (long)result;
            }
        }

        private static void AddString(ref ulong hash, string value)
        {
            string text = value ?? string.Empty;
            unchecked
            {
                for (int i = 0; i < text.Length; ++i)
                {
                    char ch = text[i];
                    hash ^= (byte)ch;
                    hash *= 1099511628211UL;
                    hash ^= (byte)(ch >> 8);
                    hash *= 1099511628211UL;
                }

                // Delimit adjacent string fields so ["ab", "c"] cannot hash the same
                // byte stream as ["a", "bc"].
                hash ^= 0xFF;
                hash *= 1099511628211UL;
            }
        }
    }

    [Serializable]
    public struct CharacterListResponseMessage : INetSerializable
    {
        public const int MaxCharacters = 16;

        public bool success;
        public byte sessionState;
        public string error;
        public CharacterSessionCharacterSummary[] characters;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(sessionState);
            writer.Put(error ?? string.Empty);

            int count = characters == null ? 0 : Math.Min(characters.Length, MaxCharacters);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                characters[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            sessionState = reader.GetByte();
            error = reader.GetString(256);

            int count = reader.GetByte();
            if (count > MaxCharacters)
                throw new InvalidOperationException("Character list exceeds protocol maximum.");

            characters = new CharacterSessionCharacterSummary[count];
            for (int i = 0; i < count; ++i)
            {
                CharacterSessionCharacterSummary summary = default(CharacterSessionCharacterSummary);
                summary.Deserialize(reader);
                characters[i] = summary;
            }
        }

        public static CharacterListResponseMessage Failed(byte state, string errorMessage) =>
            new CharacterListResponseMessage
            {
                success = false,
                sessionState = state,
                error = errorMessage ?? string.Empty,
                characters = new CharacterSessionCharacterSummary[0],
            };
    }

    [Serializable]
    public struct CreateCharacterRequestMessage : INetSerializable
    {
        public string name;
        public CharacterAppearanceRecipe initialAppearance;
        public CharacterPresentationPreferences initialPresentation;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(name ?? string.Empty);
            CharacterAppearanceNetworkCodec.Write(writer, initialAppearance);
            CharacterPresentationPreferencesNetworkCodec.Write(writer, initialPresentation);
        }

        public void Deserialize(NetDataReader reader)
        {
            name = reader.GetString(64);
            initialAppearance = CharacterAppearanceNetworkCodec.Read(reader);
            initialPresentation = CharacterPresentationPreferencesNetworkCodec.Read(reader);
        }
    }

    [Serializable]
    public struct CreateCharacterResponseMessage : INetSerializable
    {
        public bool success;
        public long characterId;
        public string name;
        public byte sessionState;
        public byte failure;
        public string error;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(characterId);
            writer.Put(name ?? string.Empty);
            writer.Put(sessionState);
            writer.Put(failure);
            writer.Put(error ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            characterId = reader.GetLong();
            name = reader.GetString(64);
            sessionState = reader.GetByte();
            failure = reader.GetByte();
            error = reader.GetString(256);
        }

        public static CreateCharacterResponseMessage Failed(
            byte state,
            byte failureCode,
            string errorMessage) =>
            new CreateCharacterResponseMessage
            {
                success = false,
                characterId = 0,
                name = string.Empty,
                sessionState = state,
                failure = failureCode,
                error = errorMessage ?? string.Empty,
            };
    }

    [Serializable]
    public struct DeleteCharacterRequestMessage : INetSerializable
    {
        public long characterId;

        public void Serialize(NetDataWriter writer) => writer.Put(characterId);
        public void Deserialize(NetDataReader reader) => characterId = reader.GetLong();
    }

    [Serializable]
    public struct DeleteCharacterResponseMessage : INetSerializable
    {
        public bool success;
        public long characterId;
        public string name;
        public byte sessionState;
        public byte failure;
        public string error;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(characterId);
            writer.Put(name ?? string.Empty);
            writer.Put(sessionState);
            writer.Put(failure);
            writer.Put(error ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            characterId = reader.GetLong();
            name = reader.GetString(64);
            sessionState = reader.GetByte();
            failure = reader.GetByte();
            error = reader.GetString(256);
        }

        public static DeleteCharacterResponseMessage Failed(
            long characterId,
            byte state,
            byte failureCode,
            string errorMessage) =>
            new DeleteCharacterResponseMessage
            {
                success = false,
                characterId = characterId,
                name = string.Empty,
                sessionState = state,
                failure = failureCode,
                error = errorMessage ?? string.Empty,
            };
    }

    [Serializable]
    public struct EnterCharacterRequestMessage : INetSerializable
    {
        public long characterId;

        public void Serialize(NetDataWriter writer) => writer.Put(characterId);
        public void Deserialize(NetDataReader reader) => characterId = reader.GetLong();
    }

    [Serializable]
    public struct EnterCharacterResponseMessage : INetSerializable
    {
        public bool success;
        public bool worldAdopted;
        public long characterId;
        public byte sessionState;
        public byte failure;
        public string error;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(worldAdopted);
            writer.Put(characterId);
            writer.Put(sessionState);
            writer.Put(failure);
            writer.Put(error ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            worldAdopted = reader.GetBool();
            characterId = reader.GetLong();
            sessionState = reader.GetByte();
            failure = reader.GetByte();
            error = reader.GetString(256);
        }

        public static EnterCharacterResponseMessage Failed(
            long characterId,
            byte state,
            byte failureCode,
            string errorMessage) =>
            new EnterCharacterResponseMessage
            {
                success = false,
                worldAdopted = false,
                characterId = characterId,
                sessionState = state,
                failure = failureCode,
                error = errorMessage ?? string.Empty,
            };
    }
}
