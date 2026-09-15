using System;
using LiteNetLib.Utils;

namespace Player.Networking
{
    /// <summary>
    /// Harvest-only top-level message IDs. Kept out of the Combat/gameplay-action
    /// protocol file so Harvesting can evolve without replacing Combat contracts.
    /// </summary>
    public static class PlayerHarvestMessageTypes
    {
        public const ushort HarvestEvent = 63;
    }

    public enum PlayerHarvestEventPhase : byte
    {
        Started = 1,
        Succeeded = 2,
        Failed = 3,
        Cancelled = 4,
    }

    [Flags]
    public enum PlayerHarvestEventFlags : byte
    {
        None = 0,
        Depleted = 1 << 0,
    }

    /// <summary>
    /// Compact owner-only Harvest presentation event. Loot tables, odds, tool state,
    /// item rewards and profession XP stay server-authoritative and are not duplicated
    /// here. Inventory/progression use their existing canonical deltas.
    /// </summary>
    public struct PlayerHarvestEventMessage : INetSerializable
    {
        public byte phase;
        public long stableId;
        public ushort presentationId;
        public uint durationCentiseconds;
        public byte rewardTier;
        public byte flags;

        public PlayerHarvestEventPhase Phase => (PlayerHarvestEventPhase)phase;
        public bool Depleted => (((PlayerHarvestEventFlags)flags) & PlayerHarvestEventFlags.Depleted) != 0;
        public float DurationSeconds => durationCentiseconds * 0.01f;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(phase);
            writer.PutPackedLong(stableId);
            writer.PutPackedUShort(presentationId);
            writer.PutPackedUInt(durationCentiseconds);
            writer.Put(rewardTier);
            writer.Put(flags);
        }

        public void Deserialize(NetDataReader reader)
        {
            phase = reader.GetByte();
            stableId = reader.GetPackedLong();
            presentationId = reader.GetPackedUShort();
            durationCentiseconds = reader.GetPackedUInt();
            rewardTier = reader.GetByte();
            flags = reader.GetByte();
        }
    }
}
