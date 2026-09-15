using System;
using System.Runtime.Serialization;
using Game.Shared.Content;
using Game.Shared.Characters;
using Game.Shared.Resources;
using Game.Shared.Progression;

namespace Game.Shared.Backend
{
    /// <summary>
    /// JSON contracts shared by the Unity client/game server and the standalone MMO backend.
    /// They intentionally contain primitives only: no Unity, LiteNetLib, database, or server-domain types.
    /// </summary>
    public static class BackendServiceContracts
    {
        public const string GameServerKeyHeader = "X-Game-Server-Key";
        public const int MaxCharacterBatchSize = 512;
        public const int MaxCharacterLeaseBatchSize = 8192;
        // GameServer-owned runtime-only world item ids live in the high positive range.
        // Gateway durable item allocation is explicitly kept below this floor.
        public const long TransientWorldItemIdFloor = 8_000_000_000_000_000_000L;
    }

    [Serializable, DataContract]
    public sealed class BackendAccountRequest
    {
        [DataMember(Name = "account")] public string account;
        [DataMember(Name = "password")] public string password;
    }

    [Serializable, DataContract]
    public sealed class BackendAuthResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "admissionToken")] public string admissionToken;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
    }

    [Serializable, DataContract]
    public sealed class BackendAdmissionRedeemRequest
    {
        [DataMember(Name = "token")] public string token;
    }

    [Serializable, DataContract]
    public sealed class BackendAdmissionRedeemResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterListRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterSummaryDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "mapId")] public string mapId;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterListResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "characters")] public BackendCharacterSummaryDto[] characters;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLoadRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLocationDto
    {
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "positionX")] public float positionX;
        [DataMember(Name = "positionY")] public float positionY;
        [DataMember(Name = "positionZ")] public float positionZ;
        [DataMember(Name = "yawDegrees")] public float yawDegrees;
    }


    [Serializable, DataContract]
    public sealed class BackendCharacterResourceDto
    {
        [DataMember(Name = "id")] public CharacterResourceId id;
        [DataMember(Name = "current")] public int current;
        [DataMember(Name = "maximum")] public int maximum;
    }

    [Serializable, DataContract]
    public sealed class BackendMagazineStateDto
    {
        [DataMember(Name = "itemInstanceId")] public long itemInstanceId;
        [DataMember(Name = "loadedAmmoDefinitionId")] public string loadedAmmoDefinitionId;
        [DataMember(Name = "loadedRounds")] public int loadedRounds;
        [DataMember(Name = "magazineRevision")] public long magazineRevision;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterRecordDto
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "location")] public BackendCharacterLocationDto location;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "schemaVersion")] public int schemaVersion;
        [DataMember(Name = "resourceRevision")] public long resourceRevision;
        [DataMember(Name = "resources")] public BackendCharacterResourceDto[] resources;
        [DataMember(Name = "appearance")] public CharacterAppearanceRecipe appearance;
        [DataMember(Name = "presentation")] public CharacterPresentationPreferences presentation;
        // Optional soft-state piggyback. Null means this checkpoint does not own magazine
        // state; an explicit empty array means it does and there are no magazine records.
        [DataMember(Name = "magazines")] public BackendMagazineStateDto[] magazines;
        // Null on save means this checkpoint does not own progression state.
        [DataMember(Name = "progression")] public CharacterProgressionState progression;

        // Save-only internal authority proof. Load responses leave this empty.
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLoadResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "found")] public bool found;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "character")] public BackendCharacterRecordDto character;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterCreateRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "initialLocation")] public BackendCharacterLocationDto initialLocation;
        [DataMember(Name = "initialAppearance")] public CharacterAppearanceRecipe initialAppearance;
        [DataMember(Name = "initialPresentation")] public CharacterPresentationPreferences initialPresentation;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterCreateResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "failure")] public byte failure;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterDeleteRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterDeleteResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "failure")] public byte failure;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "name")] public string name;
        [DataMember(Name = "deletedUtcTicks")] public long deletedUtcTicks;
        [DataMember(Name = "purgeAfterUtcTicks")] public long purgeAfterUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterSaveBatchRequest
    {
        [DataMember(Name = "characters")] public BackendCharacterRecordDto[] characters;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterSaveAckDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "requestedRevision")] public long requestedRevision;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "storedRevision")] public long storedRevision;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterSaveBatchResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "acknowledgements")] public BackendCharacterSaveAckDto[] acknowledgements;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseAcquireRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "ownerToken")] public string ownerToken;
        [DataMember(Name = "leaseSeconds")] public int leaseSeconds;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseAcquireResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "acquired")] public bool acquired;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseRenewDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "ownerToken")] public string ownerToken;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseRenewBatchRequest
    {
        [DataMember(Name = "leaseSeconds")] public int leaseSeconds;
        [DataMember(Name = "leases")] public BackendCharacterLeaseRenewDto[] leases;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseRenewAckDto
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseRenewBatchResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "acknowledgements")] public BackendCharacterLeaseRenewAckDto[] acknowledgements;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseReleaseRequest
    {
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "ownerToken")] public string ownerToken;
    }

    [Serializable, DataContract]
    public sealed class BackendCharacterLeaseReleaseResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "released")] public bool released;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerMapDto
    {
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerRegisterRequest
    {
        [DataMember(Name = "serverId")] public string serverId;
        [DataMember(Name = "advertiseHost")] public string advertiseHost;
        [DataMember(Name = "advertisePort")] public int advertisePort;
        [DataMember(Name = "maxConnections")] public int maxConnections;
        [DataMember(Name = "connectedPlayers")] public int connectedPlayers;
        [DataMember(Name = "leaseSeconds")] public int leaseSeconds;
        [DataMember(Name = "maps")] public BackendGameServerMapDto[] maps;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerRegisterResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "leaseToken")] public string leaseToken;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerHeartbeatRequest
    {
        [DataMember(Name = "serverId")] public string serverId;
        [DataMember(Name = "leaseToken")] public string leaseToken;
        [DataMember(Name = "connectedPlayers")] public int connectedPlayers;
        [DataMember(Name = "leaseSeconds")] public int leaseSeconds;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerHeartbeatResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerUnregisterRequest
    {
        [DataMember(Name = "serverId")] public string serverId;
        [DataMember(Name = "leaseToken")] public string leaseToken;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerUnregisterResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "removed")] public bool removed;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerResolveMapRequest
    {
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
    }

    [Serializable, DataContract]
    public sealed class BackendGameServerResolveMapResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "serverId")] public string serverId;
        [DataMember(Name = "advertiseHost")] public string advertiseHost;
        [DataMember(Name = "advertisePort")] public int advertisePort;
        [DataMember(Name = "connectedPlayers")] public int connectedPlayers;
        [DataMember(Name = "maxConnections")] public int maxConnections;
        [DataMember(Name = "expiresUtcTicks")] public long expiresUtcTicks;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendGameplayContentResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "content")] public GameplayContentSnapshot content;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerSystemsLoadRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        // Reuses the existing load request as a database-queue barrier after an uncertain
        // mutation outcome. This is backend-internal and adds no client/network stream.
        [DataMember(Name = "mutationBarrier")] public bool mutationBarrier;
    }

    [Serializable, DataContract]
    public sealed class BackendPersistedItemDto
    {
        [DataMember(Name = "itemInstanceId")] public long itemInstanceId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "quantity")] public int quantity;
        [DataMember(Name = "durability")] public int durability;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "inventorySlot")] public int inventorySlot;
        [DataMember(Name = "equipmentSlotId")] public string equipmentSlotId;
        [DataMember(Name = "loadedAmmoDefinitionId")] public string loadedAmmoDefinitionId;
        [DataMember(Name = "loadedRounds")] public int loadedRounds;
        [DataMember(Name = "magazineRevision")] public long magazineRevision;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerSystemsSnapshotDto
    {
        [DataMember(Name = "inventoryCapacity")] public int inventoryCapacity;
        [DataMember(Name = "inventoryRevision")] public long inventoryRevision;
        [DataMember(Name = "equipmentRevision")] public long equipmentRevision;
        [DataMember(Name = "inventoryItems")] public BackendPersistedItemDto[] inventoryItems;
        [DataMember(Name = "equipmentItems")] public BackendPersistedItemDto[] equipmentItems;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerSystemsLoadResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "found")] public bool found;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerSystemsCommitRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerSystemsCommitResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemConsumeRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "itemInstanceId")] public long itemInstanceId;
        [DataMember(Name = "consumeQuantity")] public int consumeQuantity;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemConsumeResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerAmmoReloadRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "ammoFamily")] public string ammoFamily;
        [DataMember(Name = "preferredAmmoDefinitionId")] public string preferredAmmoDefinitionId;
        [DataMember(Name = "maximumRounds")] public int maximumRounds;
        [DataMember(Name = "weaponItemInstanceId")] public long weaponItemInstanceId;
        [DataMember(Name = "expectedMagazineRevision")] public long expectedMagazineRevision;
        [DataMember(Name = "currentLoadedAmmoDefinitionId")] public string currentLoadedAmmoDefinitionId;
        [DataMember(Name = "currentLoadedRounds")] public int currentLoadedRounds;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerAmmoReloadResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "ammoUnavailable")] public bool ammoUnavailable;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "ammoDefinitionId")] public string ammoDefinitionId;
        [DataMember(Name = "consumedRounds")] public int consumedRounds;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendWorldItemDto
    {
        [DataMember(Name = "itemInstanceId")] public long itemInstanceId;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "quantity")] public int quantity;
        [DataMember(Name = "durability")] public int durability;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "loadedAmmoDefinitionId")] public string loadedAmmoDefinitionId;
        [DataMember(Name = "loadedRounds")] public int loadedRounds;
        [DataMember(Name = "magazineRevision")] public long magazineRevision;
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "positionX")] public float positionX;
        [DataMember(Name = "positionY")] public float positionY;
        [DataMember(Name = "positionZ")] public float positionZ;
    }

    [Serializable, DataContract]
    public sealed class BackendWorldItemWorldDto
    {
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "revision")] public long revision;
        [DataMember(Name = "items")] public BackendWorldItemDto[] items;
    }

    [Serializable, DataContract]
    public sealed class BackendWorldItemsLoadResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "worlds")] public BackendWorldItemWorldDto[] worlds;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemDropRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "sourceItemInstanceId")] public long sourceItemInstanceId;
        // Required only for split-stack drops. GameServer allocates this runtime-only id
        // before persistence so a lost response cannot make the ground item unrecoverable.
        [DataMember(Name = "splitWorldItemInstanceId")] public long splitWorldItemInstanceId;
        [DataMember(Name = "dropQuantity")] public int dropQuantity;
        [DataMember(Name = "sourceLoadedAmmoDefinitionId")] public string sourceLoadedAmmoDefinitionId;
        [DataMember(Name = "sourceLoadedRounds")] public int sourceLoadedRounds;
        [DataMember(Name = "sourceMagazineRevision")] public long sourceMagazineRevision;
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "positionX")] public float positionX;
        [DataMember(Name = "positionY")] public float positionY;
        [DataMember(Name = "positionZ")] public float positionZ;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemDropResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "worldRevision")] public long worldRevision;
        [DataMember(Name = "worldItem")] public BackendWorldItemDto worldItem;
        [DataMember(Name = "error")] public string error;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemGrantRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "definitionId")] public string definitionId;
        [DataMember(Name = "quantity")] public int quantity;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemGrantResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }


    [Serializable, DataContract]
    public sealed class BackendRewardItemDto
    {
        [DataMember(Name = "itemDataId")] public ushort itemDataId;
        [DataMember(Name = "quantity")] public int quantity;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemBundleGrantRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "items")] public BackendRewardItemDto[] items = Array.Empty<BackendRewardItemDto>();
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemBundleGrantResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerCraftRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "recipeDataId")] public ushort recipeDataId;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerCraftResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "error")] public string error;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemPickupRequest
    {
        [DataMember(Name = "accountId")] public long accountId;
        [DataMember(Name = "characterId")] public long characterId;
        [DataMember(Name = "leaseOwnerToken")] public string leaseOwnerToken;
        [DataMember(Name = "expectedInventoryRevision")] public long expectedInventoryRevision;
        [DataMember(Name = "expectedEquipmentRevision")] public long expectedEquipmentRevision;
        [DataMember(Name = "worldItemInstanceId")] public long worldItemInstanceId;
        [DataMember(Name = "expectedWorldItemRevision")] public long expectedWorldItemRevision;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
    }

    [Serializable, DataContract]
    public sealed class BackendPlayerItemPickupResponse
    {
        [DataMember(Name = "success")] public bool success;
        [DataMember(Name = "accepted")] public bool accepted;
        [DataMember(Name = "stale")] public bool stale;
        [DataMember(Name = "storedInventoryRevision")] public long storedInventoryRevision;
        [DataMember(Name = "storedEquipmentRevision")] public long storedEquipmentRevision;
        [DataMember(Name = "worldRevision")] public long worldRevision;
        [DataMember(Name = "mapId")] public string mapId;
        [DataMember(Name = "instanceId")] public string instanceId;
        [DataMember(Name = "state")] public BackendPlayerSystemsSnapshotDto state;
        [DataMember(Name = "error")] public string error;
    }

}
