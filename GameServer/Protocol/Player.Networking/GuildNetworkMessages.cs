using System;
using LiteNetLib.Utils;

namespace Player.Networking
{
    public static class GuildRequestTypes
    {
        public const ushort Snapshot = 750;
        public const ushort Action = 751;
    }

    public static class GuildMessageTypes
    {
        // Owner-only Guild state. Membership/invites are durable or low-frequency social state.
        public const ushort State = 68;
    }

    public enum GuildActionKind : byte
    {
        Create = 1,
        Accept = 2,
        Decline = 3,
        Leave = 4,
        Kick = 5,
        Disband = 6,
    }

    public struct GuildActionRequestMessage : INetSerializable
    {
        public byte action;
        public long targetCharacterId;
        public string name;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(action);
            writer.Put(targetCharacterId);
            writer.Put(name ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            action = reader.GetByte();
            targetCharacterId = reader.GetLong();
            name = reader.GetString(64);
        }

        public GuildActionKind Action => (GuildActionKind)action;
    }

    public struct GuildMutationResponseMessage : INetSerializable
    {
        public bool success;
        public byte status;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(status);
            writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            status = reader.GetByte();
            detail = reader.GetString(256);
        }

        public static GuildMutationResponseMessage Ok(string detail) =>
            new GuildMutationResponseMessage { success = true, status = 0, detail = detail ?? string.Empty };

        public static GuildMutationResponseMessage Failed(byte status, string detail) =>
            new GuildMutationResponseMessage { success = false, status = status, detail = detail ?? string.Empty };
    }

    public struct GuildMemberWire : INetSerializable
    {
        public long characterId;
        public string name;
        public byte role;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(characterId);
            writer.Put(name ?? string.Empty);
            writer.Put(role);
        }

        public void Deserialize(NetDataReader reader)
        {
            characterId = reader.GetLong();
            name = reader.GetString(64);
            role = reader.GetByte();
        }
    }

    public struct GuildStateMessage : INetSerializable
    {
        public long ownerCharacterId;
        public long guildId;
        public long revision;
        public string guildName;
        public GuildMemberWire[] members;
        public long pendingInviterCharacterId;
        public string pendingInviterName;
        public long pendingGuildId;
        public string pendingGuildName;
        public byte pendingInviteSecondsRemaining;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ownerCharacterId);
            writer.Put(guildId);
            writer.Put(revision);
            writer.Put(guildName ?? string.Empty);

            GuildMemberWire[] values = members ?? Array.Empty<GuildMemberWire>();
            int count = Math.Min(values.Length, 256);
            writer.Put((ushort)count);
            for (int i = 0; i < count; ++i)
                values[i].Serialize(writer);

            writer.Put(pendingInviterCharacterId);
            writer.Put(pendingInviterName ?? string.Empty);
            writer.Put(pendingGuildId);
            writer.Put(pendingGuildName ?? string.Empty);
            writer.Put(pendingInviteSecondsRemaining);
        }

        public void Deserialize(NetDataReader reader)
        {
            ownerCharacterId = reader.GetLong();
            guildId = reader.GetLong();
            revision = reader.GetLong();
            guildName = reader.GetString(64);

            int count = reader.GetUShort();
            if (count > 256)
                throw new InvalidOperationException("guild state contains too many members");
            members = new GuildMemberWire[count];
            for (int i = 0; i < count; ++i)
            {
                GuildMemberWire member = default;
                member.Deserialize(reader);
                members[i] = member;
            }

            pendingInviterCharacterId = reader.GetLong();
            pendingInviterName = reader.GetString(64);
            pendingGuildId = reader.GetLong();
            pendingGuildName = reader.GetString(64);
            pendingInviteSecondsRemaining = reader.GetByte();
        }
    }
}
