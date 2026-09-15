using System;
using System.Runtime.Serialization;
using Game.Shared.Actors;
using Game.Shared.Interactions;
using Game.Shared.Population;

namespace Game.Shared.World
{
    /// <summary>
    /// Data-only authoritative map format emitted by the Unity Editor baker and consumed by
    /// the standalone .NET GameServer. It intentionally contains no Unity/Mirror types.
    /// </summary>
    public static class ServerMapFormat
    {
        public const int Version = 5;
    }

    /// <summary>
    /// Canonical identifier normalization shared by world authoring, persistence, routing,
    /// and standalone server map lookup. Map IDs are intentionally case-insensitive at the
    /// authoring boundary and are stored in one stable lowercase/safe form.
    /// </summary>
    public static class ServerMapId
    {
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim().ToLowerInvariant();
            char[] buffer = new char[value.Length];
            int length = 0;
            bool underscore = false;

            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                bool valid = (c >= 'a' && c <= 'z') ||
                             (c >= '0' && c <= '9') ||
                             c == '-';

                if (valid)
                {
                    buffer[length++] = c;
                    underscore = false;
                }
                else if (!underscore && length > 0)
                {
                    buffer[length++] = '_';
                    underscore = true;
                }
            }

            while (length > 0 && buffer[length - 1] == '_')
                --length;

            return new string(buffer, 0, length);
        }
    }

    [Serializable, DataContract]
    public sealed class ServerMapSnapshot
    {
        [DataMember(Name = "formatVersion")] public int formatVersion = ServerMapFormat.Version;
        [DataMember(Name = "mapId")] public string mapId = string.Empty;
        [DataMember(Name = "instanceId")] public string instanceId = string.Empty;
        [DataMember(Name = "bakeRevision")] public long bakeRevision;
        [DataMember(Name = "bakedUtcTicks")] public long bakedUtcTicks;
        [DataMember(Name = "contentHash")] public string contentHash = string.Empty;
        [DataMember(Name = "sourceScene")] public string sourceScene = string.Empty;
        [DataMember(Name = "mapKind")] public ServerMapKind mapKind = ServerMapKind.World;
        [DataMember(Name = "worldRules")] public ServerWorldRules worldRules = new ServerWorldRules();
        [DataMember(Name = "propertyTemplate")] public ServerPropertyTemplateDefinition propertyTemplate;
        [DataMember(Name = "navMesh")] public ServerNavMeshInfo navMesh;
        [DataMember(Name = "sharedWorld")] public ServerSharedWorldInfo sharedWorld;
        [DataMember(Name = "collisionTriangles")] public ServerCollisionTriangle[] collisionTriangles = Array.Empty<ServerCollisionTriangle>();
        [DataMember(Name = "dynamicBlockers")] public ServerDynamicBlocker[] dynamicBlockers = Array.Empty<ServerDynamicBlocker>();
        [DataMember(Name = "traversalLinks")] public ServerTraversalLink[] traversalLinks = Array.Empty<ServerTraversalLink>();
        [DataMember(Name = "spawnAnchors")] public ServerSpawnAnchor[] spawnAnchors = Array.Empty<ServerSpawnAnchor>();
        [DataMember(Name = "interactables")] public ServerWorldInteractableDefinition[] interactables = Array.Empty<ServerWorldInteractableDefinition>();
        [DataMember(Name = "populationNodes")] public ServerPopulationRouteNode[] populationNodes = Array.Empty<ServerPopulationRouteNode>();
        [DataMember(Name = "populationEdges")] public ServerPopulationRouteEdge[] populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        [DataMember(Name = "populationPortals")] public ServerPopulationPortal[] populationPortals = Array.Empty<ServerPopulationPortal>();
    }


    /// <summary>
    /// Manifest entry for the tiled Detour navigation mesh baked from Unity-side
    /// authoritative collider geometry. The binary itself remains outside JSON.
    /// </summary>
    [Serializable, DataContract]
    public sealed class ServerNavMeshInfo
    {
        public const int CurrentFormatVersion = 1;

        [DataMember(Name = "formatVersion")] public int formatVersion = CurrentFormatVersion;
        [DataMember(Name = "fileName")] public string fileName = string.Empty;
        [DataMember(Name = "contentHash")] public string contentHash = string.Empty;
        [DataMember(Name = "builder")] public string builder = "DotRecast";
        [DataMember(Name = "builderVersion")] public string builderVersion = "2026.3.1";

        [DataMember(Name = "cellSize")] public float cellSize = 0.20f;
        [DataMember(Name = "cellHeight")] public float cellHeight = 0.10f;
        [DataMember(Name = "tileSize")] public int tileSize = 128;
        [DataMember(Name = "agentHeight")] public float agentHeight = 1.80f;
        [DataMember(Name = "agentRadius")] public float agentRadius = 0.35f;
        [DataMember(Name = "agentMaxClimb")] public float agentMaxClimb = 0.40f;
        [DataMember(Name = "agentMaxSlope")] public float agentMaxSlope = 50f;
        [DataMember(Name = "maxVertsPerPoly")] public int maxVertsPerPoly = 6;
    }


    public enum ServerMapKind : byte
    {
        World = 0,
        PropertyTemplate = 1,
        VenueTemplate = 2,
        InstanceTemplate = 3,
    }

    public enum ServerMovementAuthorityProfile : byte
    {
        StrictWorld = 0,
        SafeInterior = 1,
    }

    [Serializable, DataContract]
    public sealed class ServerWorldRules
    {
        [DataMember(Name = "movementProfile")] public ServerMovementAuthorityProfile movementProfile = ServerMovementAuthorityProfile.StrictWorld;
        [DataMember(Name = "combatAllowed")] public bool combatAllowed = true;
        [DataMember(Name = "pvpAllowed")] public bool pvpAllowed = true;
        [DataMember(Name = "vehiclesAllowed")] public bool vehiclesAllowed = true;
    }

    [Serializable, DataContract]
    public sealed class ServerPlaceableCategoryLimit
    {
        [DataMember(Name = "category")] public string category = string.Empty;
        [DataMember(Name = "maximum")] public int maximum;
    }

    [Serializable, DataContract]
    public sealed class ServerPropertyTemplateDefinition
    {
        [DataMember(Name = "templateId")] public string templateId = string.Empty;
        [DataMember(Name = "maximumPlaceables")] public int maximumPlaceables = 150;
        [DataMember(Name = "maximumCollisionPlaceables")] public int maximumCollisionPlaceables = 100;
        [DataMember(Name = "maximumInteractivePlaceables")] public int maximumInteractivePlaceables = 50;
        [DataMember(Name = "maximumDynamicStatePlaceables")] public int maximumDynamicStatePlaceables = 50;
        [DataMember(Name = "maximumStorageContainers")] public int maximumStorageContainers = 12;
        [DataMember(Name = "categoryLimits")] public ServerPlaceableCategoryLimit[] categoryLimits = Array.Empty<ServerPlaceableCategoryLimit>();
    }

    [Serializable, DataContract]
    public struct ServerPose
    {
        [DataMember(Name = "x")] public float x;
        [DataMember(Name = "y")] public float y;
        [DataMember(Name = "z")] public float z;
        [DataMember(Name = "yaw")] public float yaw;

        public ServerPose(float x, float y, float z, float yaw = 0f)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.yaw = yaw;
        }

        public WorldPosition ToWorldPosition() => new WorldPosition(x, y, z);
    }

    [Flags]
    public enum ServerSurfaceFlags : ushort
    {
        None = 0,
        Walkable = 1 << 0,
        Ground = 1 << 1,
        Road = 1 << 2,
        Sidewalk = 1 << 3,
        Interior = 1 << 4,
        Roof = 1 << 5,
        Water = 1 << 6,
        NoSpawn = 1 << 7,
        PlacementFloor = 1 << 8,
        PlacementWall = 1 << 9,
        PlacementCeiling = 1 << 10,
        VehicleDrivable = 1 << 11,
    }

    [Serializable, DataContract]
    public struct ServerCollisionTriangle
    {
        [DataMember(Name = "surfaceId")] public long surfaceId;
        [DataMember(Name = "ax")] public float ax;
        [DataMember(Name = "ay")] public float ay;
        [DataMember(Name = "az")] public float az;
        [DataMember(Name = "bx")] public float bx;
        [DataMember(Name = "by")] public float by;
        [DataMember(Name = "bz")] public float bz;
        [DataMember(Name = "cx")] public float cx;
        [DataMember(Name = "cy")] public float cy;
        [DataMember(Name = "cz")] public float cz;
        [DataMember(Name = "normalX")] public float normalX;
        [DataMember(Name = "normalY")] public float normalY;
        [DataMember(Name = "normalZ")] public float normalZ;
        [DataMember(Name = "flags")] public ServerSurfaceFlags flags;
    }

    public enum ServerDynamicBlockerKind : byte
    {
        Generic = 0,
        Door = 1,
        Gate = 2,
        Elevator = 3,
        MovingPlatform = 4,
    }

    [Serializable, DataContract]
    public sealed class ServerDynamicBlocker
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "kind")] public ServerDynamicBlockerKind kind;
        [DataMember(Name = "pose")] public ServerPose pose;
        [DataMember(Name = "sizeX")] public float sizeX = 1f;
        [DataMember(Name = "sizeY")] public float sizeY = 2f;
        [DataMember(Name = "sizeZ")] public float sizeZ = 0.2f;
        [DataMember(Name = "enabledByDefault")] public bool enabledByDefault = true;
    }

    public enum ServerTraversalKind : byte
    {
        None = 0,
        Vault = 1,
        Ladder = 2,
        StepLink = 3,
        MovingPlatform = 4,
        Elevator = 5,
    }

    [Serializable, DataContract]
    public sealed class ServerTraversalLink
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "kind")] public ServerTraversalKind kind;
        [DataMember(Name = "entry")] public ServerPose entry;
        [DataMember(Name = "exit")] public ServerPose exit;
        [DataMember(Name = "alternateEntry")] public ServerPose alternateEntry;
        [DataMember(Name = "hasAlternateEntry")] public bool hasAlternateEntry;
        [DataMember(Name = "durationSeconds")] public float durationSeconds = 0.8f;
        [DataMember(Name = "width")] public float width = 0.8f;
        [DataMember(Name = "enabledByDefault")] public bool enabledByDefault = true;
        [DataMember(Name = "relatedWorldObjectId")] public long relatedWorldObjectId;
    }

    public enum ServerSpawnKind : byte
    {
        None = 0,
        PlayerFirstSpawn = 1,
        PlayerRespawn = 2,
        EmergencyFallback = 3,
        Monster = 10,
        Npc = 11,
        Population = 12,
    }

    [Serializable, DataContract]
    public sealed class ServerSpawnAnchor
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "label")] public string label = string.Empty;
        [DataMember(Name = "kind")] public ServerSpawnKind kind;
        [DataMember(Name = "actorKind")] public AuthoritativeActorKind actorKind;
        [DataMember(Name = "archetypeId")] public string archetypeId = string.Empty;
        [DataMember(Name = "deathLootTableId")] public string deathLootTableId = string.Empty;
        [DataMember(Name = "pose")] public ServerPose pose;
        [DataMember(Name = "priority")] public int priority;
        [DataMember(Name = "enabled")] public bool enabled = true;
        [DataMember(Name = "capsuleRadius")] public float capsuleRadius = 0.35f;
        [DataMember(Name = "capsuleHeight")] public float capsuleHeight = 1.8f;
        [DataMember(Name = "maximumGroundSnap")] public float maximumGroundSnap = 0.65f;
        [DataMember(Name = "tags")] public string[] tags = Array.Empty<string>();
        [DataMember(Name = "routeNodeId")] public long routeNodeId;
        [DataMember(Name = "portalId")] public long portalId;
    }

    public enum ServerInteractionPresentationRole : byte
    {
        None = 0,
        Primary = 1,
        Seat = 2,
        Performer = 3,
        ComputerUser = 4,
        CorpseSearcher = 5,
        VehicleDriver = 10,
        VehiclePassenger = 11,
        MotorcycleDriver = 12,
        MotorcyclePassenger = 13,
        BicycleRider = 14,
        PartnerInitiator = 20,
        PartnerReceiver = 21,
    }

    [Serializable, DataContract]
    public sealed class ServerInteractionSlotDefinition
    {
        [DataMember(Name = "slotId")] public string slotId = string.Empty;
        [DataMember(Name = "roleId")] public string roleId = string.Empty;
        [DataMember(Name = "presentationRole")] public ServerInteractionPresentationRole presentationRole;
        [DataMember(Name = "anchor")] public ServerPose anchor;
        [DataMember(Name = "approach")] public ServerPose approach;
        [DataMember(Name = "exit")] public ServerPose exit;
        [DataMember(Name = "hasApproach")] public bool hasApproach;
        [DataMember(Name = "hasExit")] public bool hasExit;
        [DataMember(Name = "leftHandTarget")] public ServerPose leftHandTarget;
        [DataMember(Name = "rightHandTarget")] public ServerPose rightHandTarget;
        [DataMember(Name = "leftFootTarget")] public ServerPose leftFootTarget;
        [DataMember(Name = "rightFootTarget")] public ServerPose rightFootTarget;
        [DataMember(Name = "hasLeftHandTarget")] public bool hasLeftHandTarget;
        [DataMember(Name = "hasRightHandTarget")] public bool hasRightHandTarget;
        [DataMember(Name = "hasLeftFootTarget")] public bool hasLeftFootTarget;
        [DataMember(Name = "hasRightFootTarget")] public bool hasRightFootTarget;
    }

    [Serializable, DataContract]
    public sealed class ServerInteractionParticipantRoleDefinition
    {
        [DataMember(Name = "roleId")] public string roleId = string.Empty;
        [DataMember(Name = "displayLabel")] public string displayLabel = string.Empty;
        [DataMember(Name = "presentationRole")] public ServerInteractionPresentationRole presentationRole;
        [DataMember(Name = "required")] public bool required = true;
    }

    [Serializable, DataContract]
    public sealed class ServerContextualInteractionDefinition
    {
        [DataMember(Name = "definitionId")] public string definitionId = string.Empty;
        [DataMember(Name = "categoryId")] public InteractionCategoryId categoryId = InteractionCategoryId.Use;
        [DataMember(Name = "actionId")] public InteractionActionId actionId = InteractionActionId.Use;
        [DataMember(Name = "displayLabel")] public string displayLabel = "Use";
        [DataMember(Name = "maximumUseDistance")] public float maximumUseDistance = InteractionRangePolicy.WorldObjectUseRange;
        [DataMember(Name = "maximumFacingAngle")] public float maximumFacingAngle = 180f;
        [DataMember(Name = "exclusiveOccupancy")] public bool exclusiveOccupancy;
        [DataMember(Name = "looping")] public bool looping;
        [DataMember(Name = "fixedDurationSeconds")] public float fixedDurationSeconds;
        [DataMember(Name = "lockMovement")] public bool lockMovement;
        [DataMember(Name = "lockRotation")] public bool lockRotation;
        [DataMember(Name = "cancelOnDamage")] public bool cancelOnDamage = true;
        [DataMember(Name = "cancelOnMovement")] public bool cancelOnMovement = true;
        [DataMember(Name = "cancelOnTargetUnavailable")] public bool cancelOnTargetUnavailable = true;
        [DataMember(Name = "consentMode")] public InteractionConsentMode consentMode;
        [DataMember(Name = "contentLevel")] public InteractionContentLevel contentLevel;
        [DataMember(Name = "feature")] public InteractionFeature feature = InteractionFeature.WorldObjects;
        [DataMember(Name = "participantRoles")] public ServerInteractionParticipantRoleDefinition[] participantRoles = Array.Empty<ServerInteractionParticipantRoleDefinition>();
    }



    public enum ServerWorldInteractableKind : byte
    {
        Generic = 0,
        Door = 1,
        Gate = 2,
        Searchable = 3,
        HarvestNode = 4,
        Storage = 5,
    }

    [Serializable, DataContract]
    public sealed class ServerSurveillanceCameraDefinition
    {
        [DataMember(Name = "enabled")] public bool enabled;
        [DataMember(Name = "radius")] public float radius = 12f;
        [DataMember(Name = "evidence")] public int evidence = 1;
    }

    [Serializable, DataContract]
    public sealed class ServerCombatTestDummyDefinition
    {
        [DataMember(Name = "enabled")] public bool enabled = true;
        [DataMember(Name = "healthMaximum")] public int healthMaximum = 500;
        [DataMember(Name = "armor")] public float armor;
        [DataMember(Name = "resetOnDefeat")] public bool resetOnDefeat;
        [DataMember(Name = "fireDamageTypeDefinitionId")] public string fireDamageTypeDefinitionId = "damage.fire";
        [DataMember(Name = "electricDamageTypeDefinitionId")] public string electricDamageTypeDefinitionId = "damage.electric";
        [DataMember(Name = "poisonDamageTypeDefinitionId")] public string poisonDamageTypeDefinitionId = "damage.poison";
    }

    [Serializable, DataContract]
    public sealed class ServerWorldInteractableDefinition
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "label")] public string label = string.Empty;
        [DataMember(Name = "pose")] public ServerPose pose;
        [DataMember(Name = "kind")] public ServerWorldInteractableKind kind = ServerWorldInteractableKind.Generic;
        [DataMember(Name = "gameplayProfileId")] public string gameplayProfileId = string.Empty;
        [DataMember(Name = "lootTableId")] public string lootTableId = string.Empty;
        [DataMember(Name = "craftingStationId")] public string craftingStationId = string.Empty;
        [DataMember(Name = "factionDefinitionId")] public string factionDefinitionId = string.Empty;
        [DataMember(Name = "surveillanceCamera")] public ServerSurveillanceCameraDefinition surveillanceCamera;
        [DataMember(Name = "combatTestDummy")] public ServerCombatTestDummyDefinition combatTestDummy;
        [DataMember(Name = "interactionDefinitions")] public ServerContextualInteractionDefinition[] interactionDefinitions = Array.Empty<ServerContextualInteractionDefinition>();
        [DataMember(Name = "slots")] public ServerInteractionSlotDefinition[] slots = Array.Empty<ServerInteractionSlotDefinition>();
        [DataMember(Name = "dynamicBlockerId")] public long dynamicBlockerId;
        [DataMember(Name = "persistentState")] public bool persistentState;
        [DataMember(Name = "enabledByDefault")] public bool enabledByDefault = true;
    }
}
