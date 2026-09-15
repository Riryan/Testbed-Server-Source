using System;
using Game.Shared.StatusEffects;
using LiteNetLib.Utils;

namespace Player.Networking
{
    public static class PlayerStatusEffectRequestTypes
    {
        public const ushort Snapshot = 400;
    }

    public static class PlayerStatusEffectMessageTypes
    {
        // Top-level LiteNetLib message type; request IDs use a separate namespace.
        public const ushort Delta = 41;
        public const ushort Snapshot = 58;
    }

    public struct PlayerStatusEffectsSnapshotRequestMessage : INetSerializable
    {
        public void Serialize(NetDataWriter writer) { }
        public void Deserialize(NetDataReader reader) { }
    }

    public struct StatusEffectWire : INetSerializable
    {
        public ushort wireId;
        public string definitionId;
        public string displayName;
        public byte classification;
        public byte stacks;
        public double endTime;
        public ushort presentationId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(wireId);
            writer.Put(stacks);
            writer.Put(endTime);
        }

        public void Deserialize(NetDataReader reader)
        {
            wireId = reader.GetUShort();
            stacks = reader.GetByte();
            endTime = reader.GetDouble();

            definitionId = string.Empty;
            displayName = string.Empty;
            classification = 0;
            presentationId = 0;
            if (wireId != 0 &&
                PlayerGameplaySettingsRuntime.TryGetStatus(wireId, out GameplayStatusReferenceWire status))
            {
                definitionId = status.definitionId ?? string.Empty;
                displayName = status.displayName ?? definitionId;
                classification = status.classification;
                presentationId = status.presentationId;
            }
            else
            {
                definitionId = wireId == 0 ? string.Empty : $"status#{wireId}";
                displayName = definitionId;
            }
        }
    }

    public struct PlayerStatusEffectsResponseMessage : INetSerializable
    {
        public const int MaxEffects = 128;
        public bool success;
        public byte status;
        public string error;
        public long contentRevision;
        public long statusRevision;
        public StatusEffectWire[] effects;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success);
            writer.Put(status);
            writer.Put(error ?? string.Empty);
            writer.Put(contentRevision);
            writer.Put(statusRevision);
            StatusEffectWire[] source = effects ?? Array.Empty<StatusEffectWire>();
            int count = Math.Min(source.Length, MaxEffects);
            writer.Put((byte)count);
            for (int i = 0; i < count; ++i)
                source[i].Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            success = reader.GetBool();
            status = reader.GetByte();
            error = reader.GetString(256);
            contentRevision = reader.GetLong();
            statusRevision = reader.GetLong();
            int count = reader.GetByte();
            if (count > MaxEffects)
                throw new InvalidOperationException("status effect snapshot too large");
            effects = new StatusEffectWire[count];
            for (int i = 0; i < count; ++i)
            {
                StatusEffectWire effect = default;
                effect.Deserialize(reader);
                effects[i] = effect;
            }
        }

        public static PlayerStatusEffectsResponseMessage Failed(byte status, string error) =>
            new PlayerStatusEffectsResponseMessage
            {
                success = false,
                status = status,
                error = error ?? string.Empty,
                effects = Array.Empty<StatusEffectWire>(),
            };
    }

    public struct PlayerStatusEffectDeltaMessage : INetSerializable
    {
        public long statusRevision;
        public byte kind;
        public ushort statusWireId;
        public byte stacks;
        public uint remainingMilliseconds;
        public byte reason;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(statusRevision);
            writer.Put(kind);
            writer.Put(statusWireId);
            writer.Put(stacks);
            writer.Put(remainingMilliseconds);
            writer.Put(reason);
        }

        public void Deserialize(NetDataReader reader)
        {
            statusRevision = reader.GetLong();
            kind = reader.GetByte();
            statusWireId = reader.GetUShort();
            stacks = reader.GetByte();
            remainingMilliseconds = reader.GetUInt();
            reason = reader.GetByte();
        }

        public StatusEffectChangeKind Kind => (StatusEffectChangeKind)kind;
        public StatusEffectChangeReason Reason => (StatusEffectChangeReason)reason;
    }
}
