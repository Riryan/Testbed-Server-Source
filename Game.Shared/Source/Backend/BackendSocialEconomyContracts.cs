using System;
using System.Runtime.Serialization;

namespace Game.Shared.Backend
{
    [Serializable, DataContract]
    public sealed class BackendFriendEntryDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "name")] public string name;
    }

    [Serializable, DataContract]
    public sealed class BackendFriendLoadRequest
    {
        [DataMember(Name = "characterId")] public long characterId;
    }

    [Serializable, DataContract]
    public sealed class BackendFriendMutationRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "otherCharacterId")] public long otherCharacterId;
    }

    [Serializable, DataContract]
    public sealed class BackendFriendResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "friends")] public BackendFriendEntryDto[] friends = Array.Empty<BackendFriendEntryDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendTradeOfferDto
    {
        [DataMember(Name = "itemInstanceId")] public long itemInstanceId;
        [DataMember(Name = "quantity")] public int quantity;
    }

    [Serializable, DataContract]
    public sealed class BackendTradeCommitRequest
    {
        [DataMember(Name = "leftAccountId")] public long leftAccountId;
        [DataMember(Name = "leftCharacterId")] public long leftCharacterId;
        [DataMember(Name = "leftLeaseOwnerToken")] public string leftLeaseOwnerToken;
        [DataMember(Name = "leftExpectedInventoryRevision")] public long leftExpectedInventoryRevision;
        [DataMember(Name = "leftExpectedEquipmentRevision")] public long leftExpectedEquipmentRevision;
        [DataMember(Name = "leftOffers")] public BackendTradeOfferDto[] leftOffers = Array.Empty<BackendTradeOfferDto>();
        [DataMember(Name = "rightAccountId")] public long rightAccountId;
        [DataMember(Name = "rightCharacterId")] public long rightCharacterId;
        [DataMember(Name = "rightLeaseOwnerToken")] public string rightLeaseOwnerToken;
        [DataMember(Name = "rightExpectedInventoryRevision")] public long rightExpectedInventoryRevision;
        [DataMember(Name = "rightExpectedEquipmentRevision")] public long rightExpectedEquipmentRevision;
        [DataMember(Name = "rightOffers")] public BackendTradeOfferDto[] rightOffers = Array.Empty<BackendTradeOfferDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendTradeCommitResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "leftState")] public BackendPlayerSystemsSnapshotDto leftState;
        [DataMember(Name = "rightState")] public BackendPlayerSystemsSnapshotDto rightState;
    }

    [Serializable, DataContract]
    public sealed class BackendStorageSnapshotDto
    {
        [DataMember(Name = "capacity")] public int capacity;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "items")] public BackendPersistedItemDto[] items = Array.Empty<BackendPersistedItemDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendStorageLoadRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
    }

    [Serializable, DataContract]
    public sealed class BackendStorageLoadResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "storage")] public BackendStorageSnapshotDto storage;
    }

    [Serializable, DataContract]
    public sealed class BackendStorageTransferRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "expectedStorageRevision")] public long expectedStorageRevision;
        [DataMember(Name = "deposit")] public bool deposit;
        [DataMember(Name = "sourceItemInstanceId")] public long sourceItemInstanceId;
        [DataMember(Name = "quantity")] public int quantity;
    }

    [Serializable, DataContract]
    public sealed class BackendStorageTransferResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "playerState")] public BackendPlayerSystemsSnapshotDto playerState;
        [DataMember(Name = "storage")] public BackendStorageSnapshotDto storage;
    }
}
