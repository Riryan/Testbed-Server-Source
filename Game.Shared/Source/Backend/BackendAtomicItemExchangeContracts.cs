using System;
using System.Runtime.Serialization;

namespace Game.Shared.Backend
{
    [Serializable, DataContract]
    public sealed class BackendPlayerItemExchangeRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "costItemDataId")] public ushort costItemDataId;
        [DataMember(Name = "costQuantity")] public int costQuantity;
        [DataMember(Name = "items")] public BackendRewardItemDto[] items = Array.Empty<BackendRewardItemDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemExchangeResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }
}
