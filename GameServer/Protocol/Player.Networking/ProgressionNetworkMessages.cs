using System;
using LiteNetLib.Utils;

namespace Player.Networking
{
    public static class ProgressionRequestTypes
    {
        public const ushort Snapshot = 350;
    }

    public static class ProgressionMessageTypes
    {
        public const ushort Snapshot = 59;
        public const ushort Delta = 60;
    }

    public struct ProgressionSnapshotRequestMessage : INetSerializable
    { public void Serialize(NetDataWriter writer) { } public void Deserialize(NetDataReader reader) { } }

    public struct ProgressTrackWire : INetSerializable
    {
        public ushort dataId; public int value;
        public void Serialize(NetDataWriter writer) { writer.Put(dataId); writer.Put(value); }
        public void Deserialize(NetDataReader reader) { dataId = reader.GetUShort(); value = reader.GetInt(); }
    }

    public struct ReputationWire : INetSerializable
    {
        public ushort factionDataId; public int value;
        public void Serialize(NetDataWriter writer) { writer.Put(factionDataId); writer.Put(value); }
        public void Deserialize(NetDataReader reader) { factionDataId = reader.GetUShort(); value = reader.GetInt(); }
    }

    public struct HeatWire : INetSerializable
    {
        public ushort jurisdictionDataId; public int value; public long bounty; public int evidence;
        public void Serialize(NetDataWriter writer) { writer.Put(jurisdictionDataId); writer.Put(value); writer.Put(bounty); writer.Put(evidence); }
        public void Deserialize(NetDataReader reader) { jurisdictionDataId = reader.GetUShort(); value = reader.GetInt(); bounty = reader.GetLong(); evidence = reader.GetInt(); }
    }

    public struct ProgressionSnapshotMessage : INetSerializable
    {
        public const int MaxTracks = 512, MaxReputation = 512, MaxHeat = 256, MaxRecipes = 4096;
        public bool success; public string error; public long contentRevision; public long revision; public long experience; public int level; public ushort factionDataId;
        public ProgressTrackWire[] tracks; public ReputationWire[] reputation; public HeatWire[] heat; public ushort[] knownRecipeDataIds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(success); writer.Put(error ?? string.Empty); writer.Put(contentRevision); writer.Put(revision); writer.Put(experience); writer.Put(level); writer.Put(factionDataId);
            ProgressTrackWire[] t = tracks ?? Array.Empty<ProgressTrackWire>(); int tc = Math.Min(t.Length, MaxTracks); writer.Put((ushort)tc); for (int i=0;i<tc;++i) t[i].Serialize(writer);
            ReputationWire[] r = reputation ?? Array.Empty<ReputationWire>(); int rc = Math.Min(r.Length, MaxReputation); writer.Put((ushort)rc); for (int i=0;i<rc;++i) r[i].Serialize(writer);
            HeatWire[] h = heat ?? Array.Empty<HeatWire>(); int hc = Math.Min(h.Length, MaxHeat); writer.Put((ushort)hc); for (int i=0;i<hc;++i) h[i].Serialize(writer);
            ushort[] k = knownRecipeDataIds ?? Array.Empty<ushort>(); int kc = Math.Min(k.Length, MaxRecipes); writer.Put((ushort)kc); for (int i=0;i<kc;++i) writer.Put(k[i]);
        }
        public void Deserialize(NetDataReader reader)
        {
            success=reader.GetBool(); error=reader.GetString(256); contentRevision=reader.GetLong(); revision=reader.GetLong(); experience=reader.GetLong(); level=reader.GetInt(); factionDataId=reader.GetUShort();
            int tc=reader.GetUShort(); if(tc>MaxTracks) throw new InvalidOperationException("progress track snapshot too large"); tracks=new ProgressTrackWire[tc]; for(int i=0;i<tc;++i){var v=default(ProgressTrackWire);v.Deserialize(reader);tracks[i]=v;}
            int rc=reader.GetUShort(); if(rc>MaxReputation) throw new InvalidOperationException("reputation snapshot too large"); reputation=new ReputationWire[rc]; for(int i=0;i<rc;++i){var v=default(ReputationWire);v.Deserialize(reader);reputation[i]=v;}
            int hc=reader.GetUShort(); if(hc>MaxHeat) throw new InvalidOperationException("heat snapshot too large"); heat=new HeatWire[hc]; for(int i=0;i<hc;++i){var v=default(HeatWire);v.Deserialize(reader);heat[i]=v;}
            int kc=reader.GetUShort(); if(kc>MaxRecipes) throw new InvalidOperationException("known recipe snapshot too large"); knownRecipeDataIds=new ushort[kc]; for(int i=0;i<kc;++i) knownRecipeDataIds[i]=reader.GetUShort();
        }
        public static ProgressionSnapshotMessage Failed(string error) => new ProgressionSnapshotMessage { success=false, error=error ?? string.Empty, tracks=Array.Empty<ProgressTrackWire>(), reputation=Array.Empty<ReputationWire>(), heat=Array.Empty<HeatWire>(), knownRecipeDataIds=Array.Empty<ushort>() };
    }

    public struct ProgressionDeltaMessage : INetSerializable
    {
        public long revision; public byte kind; public ushort dataId; public long value; public long auxiliary; public int extra;
        public void Serialize(NetDataWriter writer) { writer.Put(revision); writer.Put(kind); writer.Put(dataId); writer.Put(value); writer.Put(auxiliary); writer.Put(extra); }
        public void Deserialize(NetDataReader reader) { revision=reader.GetLong(); kind=reader.GetByte(); dataId=reader.GetUShort(); value=reader.GetLong(); auxiliary=reader.GetLong(); extra=reader.GetInt(); }
    }

}
