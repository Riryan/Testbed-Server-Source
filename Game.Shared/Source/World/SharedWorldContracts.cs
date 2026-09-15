using System;
using System.Runtime.Serialization;

namespace Game.Shared.World
{
    /// <summary>
    /// Client-safe immutable world-query package. It is baked once from Unity authoring
    /// geometry and can be shipped to both trusted clients and standalone server tooling.
    /// Runtime authority remains server-side; clients use this data only for prediction and
    /// suppressing obviously impossible intent.
    /// </summary>
    public static class SharedWorldFormat
    {
        public const int Version = 1;
    }

    [Serializable, DataContract]
    public sealed class ServerSharedWorldInfo
    {
        [DataMember(Name = "formatVersion")] public int formatVersion = SharedWorldFormat.Version;
        [DataMember(Name = "fileName")] public string fileName = string.Empty;
        [DataMember(Name = "contentHash")] public string contentHash = string.Empty;
    }

    public enum ServerSurfaceEdgeKind : byte
    {
        Boundary = 0,
        Drop = 1,
        Step = 2,
        NonWalkableTransition = 3,
    }

    /// <summary>
    /// Exact pre-agent-erosion support-surface boundary. V1 emits true open boundaries;
    /// later classifiers may refine them into Drop/Step/etc. without changing the client
    /// movement-query contract.
    /// </summary>
    [Serializable, DataContract]
    public struct ServerSurfaceEdge
    {
        [DataMember(Name = "stableId")] public long stableId;
        [DataMember(Name = "ax")] public float ax;
        [DataMember(Name = "ay")] public float ay;
        [DataMember(Name = "az")] public float az;
        [DataMember(Name = "bx")] public float bx;
        [DataMember(Name = "by")] public float by;
        [DataMember(Name = "bz")] public float bz;
        [DataMember(Name = "normalX")] public float normalX;
        [DataMember(Name = "normalY")] public float normalY;
        [DataMember(Name = "normalZ")] public float normalZ;
        [DataMember(Name = "kind")] public ServerSurfaceEdgeKind kind;
        [DataMember(Name = "flags")] public ServerSurfaceFlags flags;
    }

    [Serializable, DataContract]
    public sealed class SharedWorldSnapshot
    {
        [DataMember(Name = "formatVersion")] public int formatVersion = SharedWorldFormat.Version;
        [DataMember(Name = "mapId")] public string mapId = string.Empty;
        [DataMember(Name = "instanceId")] public string instanceId = string.Empty;
        [DataMember(Name = "bakeRevision")] public long bakeRevision;
        [DataMember(Name = "bakedUtcTicks")] public long bakedUtcTicks;
        [DataMember(Name = "contentHash")] public string contentHash = string.Empty;

        // Precise pre-Recast-erosion ground/support geometry used for local prediction,
        // exact height queries, and true-edge detection.
        [DataMember(Name = "supportTriangles")] public ServerCollisionTriangle[] supportTriangles = Array.Empty<ServerCollisionTriangle>();
        [DataMember(Name = "surfaceEdges")] public ServerSurfaceEdge[] surfaceEdges = Array.Empty<ServerSurfaceEdge>();

        // Client-safe physical environment. Dynamic state is NOT authoritative here; live
        // blocker/interactable revisions are still supplied by server AOI/deltas.
        [DataMember(Name = "collisionTriangles")] public ServerCollisionTriangle[] collisionTriangles = Array.Empty<ServerCollisionTriangle>();
        [DataMember(Name = "dynamicBlockers")] public ServerDynamicBlocker[] dynamicBlockers = Array.Empty<ServerDynamicBlocker>();
        [DataMember(Name = "interactables")] public ServerWorldInteractableDefinition[] interactables = Array.Empty<ServerWorldInteractableDefinition>();
    }
}
