using System;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace Player.Networking
{
    public static class PartyMessageTypes
    {
        // Compact owner-only Party state push. No Party polling/request message is added.
        public const ushort State = 67;
    }

    public struct PartyMemberWire : INetSerializable
    {
        public long characterId;
        public string name;
        public bool isLeader;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(characterId);
            writer.Put(name ?? string.Empty);
            writer.Put(isLeader);
        }

        public void Deserialize(NetDataReader reader)
        {
            characterId = reader.GetLong();
            name = reader.GetString();
            isLeader = reader.GetBool();
        }
    }

    public struct PartyStateMessage : INetSerializable
    {
        public long ownerCharacterId;
        public ulong partyId;
        public long leaderCharacterId;
        public PartyMemberWire[] members;
        public long pendingInviterCharacterId;
        public string pendingInviterName;
        public byte pendingInviteSecondsRemaining;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ownerCharacterId);
            writer.Put(partyId);
            writer.Put(leaderCharacterId);

            PartyMemberWire[] values = members ?? Array.Empty<PartyMemberWire>();
            int count = Math.Min(values.Length, byte.MaxValue);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                values[i].Serialize(writer);

            writer.Put(pendingInviterCharacterId);
            writer.Put(pendingInviterName ?? string.Empty);
            writer.Put(pendingInviteSecondsRemaining);
        }

        public void Deserialize(NetDataReader reader)
        {
            ownerCharacterId = reader.GetLong();
            partyId = reader.GetULong();
            leaderCharacterId = reader.GetLong();

            int count = reader.GetByte();
            members = new PartyMemberWire[count];
            for (int i = 0; i < count; ++i)
            {
                PartyMemberWire member = default;
                member.Deserialize(reader);
                members[i] = member;
            }

            pendingInviterCharacterId = reader.GetLong();
            pendingInviterName = reader.GetString();
            pendingInviteSecondsRemaining = reader.GetByte();
        }
    }
}
