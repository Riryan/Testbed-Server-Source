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
    public enum FriendsStateUpdateKind : byte { Full = 0, PresenceDelta = 1 }
    public enum StorageStateUpdateKind : byte { Full = 0, Delta = 1 }

    [Flags]
    public enum TradeStateChangeMask : byte
    {
        Full = 0,
        Metadata = 1 << 0,
        OwnOffers = 1 << 1,
        PartnerOffers = 1 << 2,
        Detail = 1 << 3,
    }

    public struct EmptySocialRequestMessage : INetSerializable
    {
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
        public long expectedItemInstanceId;
        public long knownStorageRevision;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(action);
            writer.Put(sourceSlot);
            writer.Put(quantity);
            writer.Put(expectedItemInstanceId);
            writer.Put(knownStorageRevision);
        }

        public void Deserialize(NetDataReader reader)
        {
            action = reader.GetByte();
            sourceSlot = reader.GetInt();
            quantity = reader.GetInt();
            expectedItemInstanceId = reader != null && reader.AvailableBytes >= sizeof(long) ? reader.GetLong() : 0L;
            knownStorageRevision = reader != null && reader.AvailableBytes >= sizeof(long) ? reader.GetLong() : 0L;
        }
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

    public struct FriendPresenceWire : INetSerializable
    {
        public long characterId;
        public bool online;
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); writer.Put(online); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetLong(); online = reader.GetBool(); }
    }

    public static class FriendsStateRevision
    {
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
        public FriendsStateUpdateKind updateKind;
        public FriendEntryWire[] friends;
        public FriendPresenceWire[] presence;
        public long pendingInviterCharacterId;
        public string pendingInviterName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)updateKind);
            if (updateKind == FriendsStateUpdateKind.PresenceDelta)
            {
                FriendPresenceWire[] values = presence ?? Array.Empty<FriendPresenceWire>();
                int count = Math.Min(values.Length, ushort.MaxValue);
                writer.Put((ushort)count);
                for (int i = 0; i < count; ++i) values[i].Serialize(writer);
                return;
            }

            FriendEntryWire[] entries = friends ?? Array.Empty<FriendEntryWire>();
            int entryCount = Math.Min(entries.Length, ushort.MaxValue);
            writer.Put((ushort)entryCount);
            for (int i = 0; i < entryCount; ++i) entries[i].Serialize(writer);
            writer.Put(pendingInviterCharacterId);
            writer.Put(pendingInviterName ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            updateKind = (FriendsStateUpdateKind)reader.GetByte();
            if (updateKind == FriendsStateUpdateKind.PresenceDelta)
            {
                int count = reader.GetUShort();
                presence = new FriendPresenceWire[count];
                friends = Array.Empty<FriendEntryWire>();
                for (int i = 0; i < count; ++i)
                {
                    FriendPresenceWire value = default;
                    value.Deserialize(reader);
                    presence[i] = value;
                }
                pendingInviterCharacterId = 0;
                pendingInviterName = string.Empty;
                return;
            }

            int fullCount = reader.GetUShort();
            friends = new FriendEntryWire[fullCount];
            presence = Array.Empty<FriendPresenceWire>();
            for (int i = 0; i < fullCount; ++i)
            {
                FriendEntryWire value = default;
                value.Deserialize(reader);
                friends[i] = value;
            }
            pendingInviterCharacterId = reader.GetLong();
            pendingInviterName = reader.GetString();
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
        public TradeStateChangeMask changeMask;
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

        public bool IsFull => changeMask == TradeStateChangeMask.Full;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)changeMask);
            writer.Put(sessionId);
            bool full = IsFull;
            if (full || (changeMask & TradeStateChangeMask.Metadata) != 0)
            {
                writer.Put(partnerCharacterId);
                writer.Put(partnerName ?? string.Empty);
                writer.Put(phase);
                writer.Put(ownLocked);
                writer.Put(partnerLocked);
                writer.Put(ownConfirmed);
                writer.Put(partnerConfirmed);
            }
            if (full || (changeMask & TradeStateChangeMask.OwnOffers) != 0) WriteOffers(writer, ownOffers);
            if (full || (changeMask & TradeStateChangeMask.PartnerOffers) != 0) WriteOffers(writer, partnerOffers);
            if (full || (changeMask & TradeStateChangeMask.Detail) != 0) writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            changeMask = (TradeStateChangeMask)reader.GetByte();
            sessionId = reader.GetULong();
            bool full = IsFull;
            if (full || (changeMask & TradeStateChangeMask.Metadata) != 0)
            {
                partnerCharacterId = reader.GetLong();
                partnerName = reader.GetString();
                phase = reader.GetByte();
                ownLocked = reader.GetBool();
                partnerLocked = reader.GetBool();
                ownConfirmed = reader.GetBool();
                partnerConfirmed = reader.GetBool();
            }
            else
            {
                partnerName = string.Empty;
            }
            ownOffers = full || (changeMask & TradeStateChangeMask.OwnOffers) != 0 ? ReadOffers(reader) : null;
            partnerOffers = full || (changeMask & TradeStateChangeMask.PartnerOffers) != 0 ? ReadOffers(reader) : null;
            detail = full || (changeMask & TradeStateChangeMask.Detail) != 0 ? reader.GetString() : null;
        }

        private static void WriteOffers(NetDataWriter writer, TradeOfferWire[] offers)
        {
            TradeOfferWire[] values = offers ?? Array.Empty<TradeOfferWire>();
            int count = Math.Min(values.Length, 64);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i) values[i].Serialize(writer);
        }

        private static TradeOfferWire[] ReadOffers(NetDataReader reader)
        {
            int count = reader.GetByte();
            var values = new TradeOfferWire[count];
            for (int i = 0; i < count; ++i)
            {
                TradeOfferWire value = default;
                value.Deserialize(reader);
                values[i] = value;
            }
            return values;
        }
    }

    public struct StorageStateMessage : INetSerializable
    {
        public StorageStateUpdateKind updateKind;
        public int capacity;
        public long revision;
        public long baseRevision;
        public PlayerItemWire[] items;
        public long[] removedItemInstanceIds;
        public string detail;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)updateKind);
            writer.Put(capacity);
            writer.Put(revision);
            if (updateKind == StorageStateUpdateKind.Delta)
                writer.Put(baseRevision);

            PlayerItemWire[] values = items ?? Array.Empty<PlayerItemWire>();
            int count = Math.Min(values.Length, ushort.MaxValue);
            writer.Put((ushort)count);
            for (int i = 0; i < count; ++i) values[i].Serialize(writer);

            if (updateKind == StorageStateUpdateKind.Delta)
            {
                long[] removed = removedItemInstanceIds ?? Array.Empty<long>();
                int removedCount = Math.Min(removed.Length, ushort.MaxValue);
                writer.Put((ushort)removedCount);
                for (int i = 0; i < removedCount; ++i) writer.Put(removed[i]);
            }

            writer.Put(detail ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            updateKind = (StorageStateUpdateKind)reader.GetByte();
            capacity = reader.GetInt();
            revision = reader.GetLong();
            baseRevision = updateKind == StorageStateUpdateKind.Delta ? reader.GetLong() : 0L;

            int count = reader.GetUShort();
            items = new PlayerItemWire[count];
            for (int i = 0; i < count; ++i)
            {
                PlayerItemWire value = default;
                value.Deserialize(reader);
                items[i] = value;
            }

            if (updateKind == StorageStateUpdateKind.Delta)
            {
                int removedCount = reader.GetUShort();
                removedItemInstanceIds = new long[removedCount];
                for (int i = 0; i < removedCount; ++i) removedItemInstanceIds[i] = reader.GetLong();
            }
            else
            {
                removedItemInstanceIds = Array.Empty<long>();
            }

            detail = reader.GetString();
        }
    }
}
