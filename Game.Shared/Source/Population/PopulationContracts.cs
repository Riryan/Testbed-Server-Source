using System;
using System.Runtime.Serialization;
using Game.Shared.Actors;
using Game.Shared.World;

namespace Game.Shared.Population
{
    public enum PopulationNpcType : byte
    {
        Civilian = 0,
        Resident = 1,
        Shopper = 2,
        Worker = 3,
        Homeless = 4,
        Nightlife = 5,
        Criminal = 6,
        GangMember = 7,
        Police = 8,
        Hunter = 9,
        Ghoul = 10,
    }

    [Flags]
    public enum PopulationNpcTypeMask : int
    {
        None = 0,
        Civilian = 1 << 0,
        Resident = 1 << 1,
        Shopper = 1 << 2,
        Worker = 1 << 3,
        Homeless = 1 << 4,
        Nightlife = 1 << 5,
        Criminal = 1 << 6,
        GangMember = 1 << 7,
        Police = 1 << 8,
        Hunter = 1 << 9,
        Ghoul = 1 << 10,
        All = -1,
    }

    [Flags]
    public enum PopulationDestinationTag : int
    {
        None = 0,
        Sidewalk = 1 << 0,
        Alley = 1 << 1,
        Apartment = 1 << 2,
        Shop = 1 << 3,
        Restaurant = 1 << 4,
        Nightclub = 1 << 5,
        Workplace = 1 << 6,
        Hospital = 1 << 7,
        Park = 1 << 8,
        Subway = 1 << 9,
        Shelter = 1 << 10,
        PoliceArea = 1 << 11,
        Restricted = 1 << 12,
        Crosswalk = 1 << 13,
        Interior = 1 << 14,
        Parking = 1 << 15,
        Industrial = 1 << 16,
        Home = 1 << 17,
        Service = 1 << 18,
        Bench = 1 << 19,
        Underpass = 1 << 20,
        AbandonedBuilding = 1 << 21,
    }

    public enum PopulationRouteReason : byte
    {
        Wander = 0,
        Schedule = 1,
        Work = 2,
        Home = 3,
        Shopping = 4,
        Fleeing = 5,
        Pursuing = 6,
        Investigating = 7,
        Scripted = 8,
        Recovery = 9,
        PortalTravel = 10,
    }

    public enum PopulationNodeType : byte
    {
        Regular = 0,
        Intersection = 1,
        StopPoint = 2,
        Destination = 3,
        CrosswalkWait = 4,
        CrosswalkExit = 5,
        PortalApproach = 6,
        PortalThreshold = 7,
        InteriorEntry = 8,
    }

    public enum PopulationSimulationLod : byte
    {
        Dormant = 0,
        Logical = 1,
        CoarseRoute = 2,
        Active = 3,
        Engaged = 4,
    }

    public enum PopulationAiState : byte
    {
        Idle = 0,
        FollowingRoute = 1,
        Waiting = 2,
        PortalDormant = 3,
        Fleeing = 4,
        Fighting = 5,
        Investigating = 6,
        Recovering = 7,
        Dead = 8,
    }

    /// <summary>
    /// Client-safe, observer-visible Population interaction state. This is presentation/preflight
    /// data only; the authoritative GameServer independently revalidates every chosen action.
    /// Keep this compact and event-driven rather than querying the server whenever focus changes.
    /// </summary>
    [Flags]
    public enum PopulationPublicInteractionFlags : byte
    {
        None = 0,
        DeathLootAvailable = 1 << 0,
    }

    public enum PopulationPortalMode : byte
    {
        SpawnOnly = 0,
        DespawnOnly = 1,
        SpawnAndDespawn = 2,
    }

    public enum PopulationPortalType : byte
    {
        GenericBuilding = 0,
        Apartment = 1,
        Shop = 2,
        Workplace = 3,
        Restaurant = 4,
        Nightclub = 5,
        Hospital = 6,
        Subway = 7,
        ParkingGarage = 8,
        Alley = 9,
        Shelter = 10,
        Bench = 11,
        Underpass = 12,
        Park = 13,
        AbandonedBuilding = 14,
        ServiceArea = 15,
    }

    [Serializable, DataContract]
    public sealed class ServerPopulationRouteNode
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "label")] public string label = string.Empty;
        [DataMember(Name = "pose")] public ServerPose pose;
        [DataMember(Name = "pathWidth")] public float pathWidth = 2.5f;
        [DataMember(Name = "routeWeight")] public float routeWeight = 1f;
        [DataMember(Name = "nodeType")] public PopulationNodeType nodeType;
        [DataMember(Name = "isDestination")] public bool isDestination;
        [DataMember(Name = "destinationTags")] public PopulationDestinationTag destinationTags = PopulationDestinationTag.Sidewalk;
        [DataMember(Name = "destinationGroup")] public string destinationGroup = string.Empty;
        [DataMember(Name = "hardRestricted")] public bool hardRestricted;
        [DataMember(Name = "allowedNpcTypes")] public PopulationNpcTypeMask allowedNpcTypes = PopulationNpcTypeMask.All;
        [DataMember(Name = "minimumWaitSeconds")] public float minimumWaitSeconds;
        [DataMember(Name = "maximumWaitSeconds")] public float maximumWaitSeconds;
        [DataMember(Name = "actionId")] public byte actionId;
    }

    [Serializable, DataContract]
    public sealed class ServerPopulationRouteEdge
    {
        [DataMember(Name = "fromNodeId")] public long fromNodeId;
        [DataMember(Name = "toNodeId")] public long toNodeId;
        [DataMember(Name = "oneWay")] public bool oneWay;
        [DataMember(Name = "width")] public float width = 2.5f;
        [DataMember(Name = "weight")] public float weight = 1f;
        [DataMember(Name = "minimumClearance")] public float minimumClearance = 0.5f;
        [DataMember(Name = "validatedClear")] public bool validatedClear = true;
    }

    [Serializable, DataContract]
    public sealed class ServerPopulationPortal
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "label")] public string label = string.Empty;
        [DataMember(Name = "mode")] public PopulationPortalMode mode;
        [DataMember(Name = "portalType")] public PopulationPortalType portalType;
        [DataMember(Name = "tags")] public PopulationDestinationTag tags;
        [DataMember(Name = "allowedNpcTypes")] public PopulationNpcTypeMask allowedNpcTypes = PopulationNpcTypeMask.All;
        [DataMember(Name = "interiorSpawn")] public ServerPose interiorSpawn;
        [DataMember(Name = "approach")] public ServerPose approach;
        [DataMember(Name = "interaction")] public ServerPose interaction;
        [DataMember(Name = "threshold")] public ServerPose threshold;
        [DataMember(Name = "exterior")] public ServerPose exterior;
        [DataMember(Name = "routeNodeId")] public long routeNodeId;
        [DataMember(Name = "doorWorldObjectId")] public long doorWorldObjectId;
        [DataMember(Name = "minimumRespawnDelay")] public float minimumRespawnDelay = 8f;
        [DataMember(Name = "maximumRespawnDelay")] public float maximumRespawnDelay = 20f;
        [DataMember(Name = "blockedRetryDelay")] public float blockedRetryDelay = 2f;
        [DataMember(Name = "maximumActiveNearby")] public int maximumActiveNearby = 12;
        [DataMember(Name = "activeNearbyRadius")] public float activeNearbyRadius = 18f;
        [DataMember(Name = "exitClearanceRadius")] public float exitClearanceRadius = 0.45f;
        [DataMember(Name = "requireDifferentDestination")] public bool requireDifferentDestination = true;
        [DataMember(Name = "suppressWhenVisibleToPlayers")] public bool suppressWhenVisibleToPlayers = true;
    }

    [Serializable, DataContract]
    public sealed class PopulationBehaviorProfileData
    {
        [DataMember(Name = "profileId")] public string profileId = string.Empty;
        [DataMember(Name = "npcType")] public PopulationNpcType npcType;
        [DataMember(Name = "walkSpeed")] public float walkSpeed = 1.8f;
        [DataMember(Name = "runSpeed")] public float runSpeed = 4.5f;
        [DataMember(Name = "aggression")] public float aggression = 0.15f;
        [DataMember(Name = "courage")] public float courage = 0.35f;
        [DataMember(Name = "sociability")] public float sociability = 0.5f;
        [DataMember(Name = "combatSkill")] public float combatSkill = 0.1f;
        [DataMember(Name = "faction")] public ActorFaction faction = ActorFaction.Civilian;
        [DataMember(Name = "weightClass")] public ActorWeightClass weightClass = ActorWeightClass.Normal;
    }
}
