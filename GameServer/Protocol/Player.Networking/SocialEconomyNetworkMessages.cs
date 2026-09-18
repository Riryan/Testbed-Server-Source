using System;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace Player.Networking
{
    public static class FriendRequestTypes
    {
        public const ushort Snapshot = 700;
        public const ushort Action = 701;
        public const ushort OwnerLiveInterest = 702;
    }

    public static class EconomyRequestTypes
    {
        public const ushort TradeAction = 850;
        public const ushort TradeSnapshot = 851;
        public const ushort StorageSnapshot = 852;
        public const ushort StorageTransfer = 853;
    }

    public static class SocialEconomyMessageTypes
    {
        public const ushort FriendsState = 64;
        public const ushort TradeState = 65;
        public const ushort StorageState = 66;
    }

    [Flags]
    public enum OwnerLiveInterestKind : uint
    {
        None = 0,
        FriendsPresence = 1u << 0,
        GuildPresence = 1u << 1,
    }

    public enum FriendActionKind : byte { Accept = 1, Decline = 2, Remove = 3 }
    public enum TradeActionKind : byte { Accept = 1, Decline = 2, Offer = 3, RemoveOffer = 4, Lock = 5, Unlock = 6, Confirm = 7, Cancel = 8 }
    public enum StorageTransferKind : byte { Deposit = 1, Withdraw = 2 }

    public struct EmptySocialRequestMessage : INetSerializable
    {
        // Optional reconnect/cache hint. Zero preserves the legacy empty request.
        public long knownRevision;
        public void Serialize(NetDataWriter writer)
        {
            if (knownRevision != 0) writer.Put(knownRevision);
        }
        public void Deserialize(NetDataReader reader)
        {
            knownRevision = reader != null && reader.AvailableBytes >= sizeof(long)
                ? reader.GetLong()
                : 0L;
        }
    }

    public struct OwnerLiveInterestRequestMessage : INetSerializable
    {
        public uint interests;
        public void Serialize(NetDataWriter writer) { writer.Put(interests); }
        public void Deserialize(NetDataReader reader) { interests = reader.GetUInt(); }
    }

    public struct FriendActionRequestMessage : INetSerializable
    {
        public byte action;
        public long targetCharacterId;
        public void Serialize(NetDataWriter writer) { writer.Put(action); writer.Put(targetCharacterId); }
        public void Deserialize(NetDataReader reader) { action = reader.GetByte(); targetCharacterId = reader.GetLong(); }
    }

    public struct TradeActionRequestMessage : INetSerializable
    {
        public byte action;
        public long partnerCharacterId;
        public int inventorySlot;
        public int quantity;
        public void Serialize(NetDataWriter writer) { writer.Put(action); writer.Put(partnerCharacterId); writer.Put(inventorySlot); writer.Put(quantity); }
        public void Deserialize(NetDataReader reader) { action = reader.GetByte(); partnerCharacterId = reader.GetLong(); inventorySlot = reader.GetInt(); quantity = reader.GetInt(); }
    }

    public struct StorageTransferRequestMessage : INetSerializable
    {
        public byte action;
        public int sourceSlot;
        public int quantity;
        public void Serialize(NetDataWriter writer) { writer.Put(action); writer.Put(sourceSlot); writer.Put(quantity); }
        public void Deserialize(NetDataReader reader) { action = reader.GetByte(); sourceSlot = reader.GetInt(); quantity = reader.GetInt(); }
    }

    public struct SocialEconomyMutationResponseMessage : INetSerializable
    {
        public bool success;
        public byte status;
        public string error;
        public void Serialize(NetDataWriter writer) { writer.Put(success); writer.Put(status); writer.Put(error ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { success = reader.GetBool(); status = reader.GetByte(); error = reader.GetString(); }
        public static SocialEconomyMutationResponseMessage Ok() => new SocialEconomyMutationResponseMessage { success = true, error = string.Empty };
        public static SocialEconomyMutationResponseMessage Failed(byte status, string error) => new SocialEconomyMutationResponseMessage { success = false, status = status, error = error ?? string.Empty };
    }

    public struct FriendEntryWire : INetSerializable
    {
        public long characterId;
        public string name;
        public bool online;
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); writer.Put(name ?? string.Empty); writer.Put(online); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetLong(); name = reader.GetString(); online = reader.GetBool(); }
    }


    public static class FriendsStateRevision
    {
        // Stable order-independent fingerprint of durable membership only. Presence and
        // pending invites are live/session state and deliberately do not affect the cache key.
        public static long Compute(FriendEntryWire[] friends)
        {
            FriendEntryWire[] values = friends ?? Array.Empty<FriendEntryWire>();
            ulong xor = 0UL;
            ulong sum = 0UL;
            int count = 0;
            for (int i = 0; i < values.Length; ++i)
            {
                if (values[i].characterId <= 0) continue;
                ulong hash = 1469598103934665603UL;
                unchecked
                {
                    ulong id = (ulong)values[i].characterId;
                    for (int b = 0; b < 8; ++b)
                    {
                        hash ^= (byte)(id >> (b * 8));
                        hash *= 1099511628211UL;
                    }
                    string name = values[i].name ?? string.Empty;
                    for (int c = 0; c < name.Length; ++c)
                    {
                        char ch = name[c];
                        hash ^= (byte)ch; hash *= 1099511628211UL;
                        hash ^= (byte)(ch >> 8); hash *= 1099511628211UL;
                    }
                    xor ^= hash;
                    sum += hash * 0x9E3779B185EBCA87UL;
                }
                count++;
            }
            unchecked
            {
                ulong result = 0xD6E8FEB86659FD93UL ^ (ulong)count;
                result ^= xor + 0x9E3779B97F4A7C15UL + (result << 6) + (result >> 2);
                result ^= sum + 0xC2B2AE3D27D4EB4FUL + (result << 6) + (result >> 2);
                if (result == 0) result = 1;
                return (long)result;
            }
        }
    }

    public struct FriendsStateMessage : INetSerializable
    {
        public FriendEntryWire[] friends;
        public long pendingInviterCharacterId;
        public string pendingInviterName;
        public void Serialize(NetDataWriter writer)
        {
            FriendEntryWire[] values = friends ?? Array.Empty<FriendEntryWire>();
            writer.Put((ushort)Math.Min(values.Length, ushort.MaxValue));
            for (int i = 0; i < values.Length && i < ushort.MaxValue; ++i) values[i].Serialize(writer);
            writer.Put(pendingInviterCharacterId); writer.Put(pendingInviterName ?? string.Empty);
        }
        public void Deserialize(NetDataReader reader)
        {
            int count = reader.GetUShort(); friends = new FriendEntryWire[count];
            for (int i = 0; i < count; ++i) { FriendEntryWire value = default; value.Deserialize(reader); friends[i] = value; }
            pendingInviterCharacterId = reader.GetLong(); pendingInviterName = reader.GetString();
        }
    }

    public struct TradeOfferWire : INetSerializable
    {
        public int sourceSlot;
        public PlayerItemWire item;
        public int quantity;
        public void Serialize(NetDataWriter writer) { writer.Put(sourceSlot); item.Serialize(writer); writer.Put(quantity); }
        public void Deserialize(NetDataReader reader) { sourceSlot = reader.GetInt(); item.Deserialize(reader); quantity = reader.GetInt(); }
    }

    public struct TradeStateMessage : INetSerializable
    {
        public ulong sessionId;
        public long partnerCharacterId;
        public string partnerName;
        public byte phase;
        public bool ownLocked;
        public bool partnerLocked;
        public bool ownConfirmed;
        public bool partnerConfirmed;
        public TradeOfferWire[] ownOffers;
        public TradeOfferWire[] partnerOffers;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(sessionId); writer.Put(partnerCharacterId); writer.Put(partnerName ?? string.Empty); writer.Put(phase);
            writer.Put(ownLocked); writer.Put(partnerLocked); writer.Put(ownConfirmed); writer.Put(partnerConfirmed);
            WriteOffers(writer, ownOffers); WriteOffers(writer, partnerOffers); writer.Put(detail ?? string.Empty);
        }
        public void Deserialize(NetDataReader reader)
        {
            sessionId = reader.GetULong(); partnerCharacterId = reader.GetLong(); partnerName = reader.GetString(); phase = reader.GetByte();
            ownLocked = reader.GetBool(); partnerLocked = reader.GetBool(); ownConfirmed = reader.GetBool(); partnerConfirmed = reader.GetBool();
            ownOffers = ReadOffers(reader); partnerOffers = ReadOffers(reader); detail = reader.GetString();
        }
        private static void WriteOffers(NetDataWriter writer, TradeOfferWire[] offers)
        {
            TradeOfferWire[] values = offers ?? Array.Empty<TradeOfferWire>(); int count = Math.Min(values.Length, 64); writer.Put((byte)count);
            for (int i = 0; i < count; ++i) values[i].Serialize(writer);
        }
        private static TradeOfferWire[] ReadOffers(NetDataReader reader)
        {
            int count = reader.GetByte(); var values = new TradeOfferWire[count];
            for (int i = 0; i < count; ++i) { TradeOfferWire value = default; value.Deserialize(reader); values[i] = value; }
            return values;
        }
    }

    public struct StorageStateMessage : INetSerializable
    {
        public int capacity;
        public long revision;
        public PlayerItemWire[] items;
        public string detail;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(capacity); writer.Put(revision); PlayerItemWire[] values = items ?? Array.Empty<PlayerItemWire>();
            writer.Put((ushort)Math.Min(values.Length, ushort.MaxValue));
            for (int i = 0; i < values.Length && i < ushort.MaxValue; ++i) values[i].Serialize(writer);
            writer.Put(detail ?? string.Empty);
        }
        public void Deserialize(NetDataReader reader)
        {
            capacity = reader.GetInt(); revision = reader.GetLong(); int count = reader.GetUShort(); items = new PlayerItemWire[count];
            for (int i = 0; i < count; ++i) { PlayerItemWire value = default; value.Deserialize(reader); items[i] = value; }
            detail = reader.GetString();
        }
    }
}
