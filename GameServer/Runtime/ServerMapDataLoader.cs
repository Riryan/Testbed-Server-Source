using System.Security.Cryptography;
using System.Text.Json;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.GameServer.Runtime;

/// <summary>
/// Loads one authoritative baked world. The existing DotRecast .servernavmesh.bin is
/// the sole source of the server's fast grounded-movement certificate. The old,
/// separately Unity-generated safeMovementTriangles payload is deliberately ignored.
/// The regular movementTriangles/collisionTriangles remain unchanged for exact
/// support/fallback and combat LOS, respectively.
/// </summary>
internal static class ServerMapDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<ServerMapSnapshot> LoadDirectory(string directory, bool required)
    {
        string fullPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(directory) ? "../Content/Maps" : directory,
            Environment.CurrentDirectory);

        if (!Directory.Exists(fullPath))
        {
            if (required)
                throw new DirectoryNotFoundException($"Required server-map directory was not found: {fullPath}");
            Console.Error.WriteLine($"Server map data directory missing: {fullPath}; legacy movement compatibility active.");
            return Array.Empty<ServerMapSnapshot>();
        }

        var maps = new List<ServerMapSnapshot>();
        foreach (string file in Directory.EnumerateFiles(fullPath, "*.servermap.json", SearchOption.TopDirectoryOnly))
        {
            string json = File.ReadAllText(file);
            ServerMapSnapshot snapshot = JsonSerializer.Deserialize<ServerMapSnapshot>(json, JsonOptions);
            if (snapshot == null)
                throw new InvalidDataException($"Server map file returned no data: {file}");
            if (snapshot.formatVersion != ServerMapFormat.Version)
                throw new InvalidDataException(
                    $"Server map '{file}' format {snapshot.formatVersion} is incompatible with runtime format {ServerMapFormat.Version}. " +
                    "Re-bake this Unity scene with Server World Bake V2 before starting the GameServer.");
            if (string.IsNullOrWhiteSpace(snapshot.mapId))
                throw new InvalidDataException($"Server map '{file}' has no mapId.");

            if (snapshot.layerFormatVersion == 1)
                LoadAuthoringLayers(fullPath, file, snapshot);
            else if (snapshot.layerFormatVersion != 0)
                throw new InvalidDataException($"Unsupported map layer format {snapshot.layerFormatVersion} in {file}.");

            ValidateSpawnAnchors(file, snapshot);
            // Read the already-published DotRecast binary ONCE. Populate the existing
            // safe-movement triangle contract in memory, and never use an independent
            // Unity NavMesh safety bake for authoritative runtime movement.
            LoadNavMeshMovementCertificate(fullPath, file, snapshot);
            ValidateSharedWorld(fullPath, file, snapshot);
            maps.Add(snapshot);
        }

        if (required && maps.Count == 0)
            throw new InvalidOperationException($"No *.servermap.json files were found in required map directory: {fullPath}");
        return maps;
    }

    private sealed class RouteLayerFile
    {
        public int formatVersion = 0;
        public string mapId = string.Empty;
        public string instanceId = string.Empty;
        public string coreContentHash = string.Empty;
        public ServerPopulationRouteNode[] populationNodes = Array.Empty<ServerPopulationRouteNode>();
        public ServerPopulationRouteEdge[] populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        public ServerPopulationPortal[] populationPortals = Array.Empty<ServerPopulationPortal>();
    }

    private sealed class SpawnLayerFile
    {
        public int formatVersion = 0;
        public string mapId = string.Empty;
        public string instanceId = string.Empty;
        public string coreContentHash = string.Empty;
        public ServerSpawnAnchor[] spawnAnchors = Array.Empty<ServerSpawnAnchor>();
    }

    private static void LoadAuthoringLayers(string folder, string coreFile, ServerMapSnapshot snapshot)
    {
        const string coreSuffix = ".servermap.json";
        string fileName = Path.GetFileName(coreFile);
        if (!fileName.EndsWith(coreSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unexpected core world map filename: {coreFile}");
        string stem = fileName[..^coreSuffix.Length];
        string spawnPath = Path.Combine(folder, stem + ".spawns.json");
        if (!File.Exists(spawnPath))
            throw new FileNotFoundException(
                $"Core world '{snapshot.mapId}' published without a valid spawns layer. Re-bake/fix ServerSpawn authoring.",
                spawnPath);
        SpawnLayerFile spawns = JsonSerializer.Deserialize<SpawnLayerFile>(
            File.ReadAllText(spawnPath), JsonOptions) ??
            throw new InvalidDataException($"Spawns layer contained no data: {spawnPath}");
        ValidateLayer(spawnPath, snapshot, spawns.formatVersion,
            spawns.mapId, spawns.instanceId, spawns.coreContentHash);
        snapshot.spawnAnchors = spawns.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();

        string routesPath = Path.Combine(folder, stem + ".routes.json");
        if (File.Exists(routesPath))
        {
            RouteLayerFile routes = JsonSerializer.Deserialize<RouteLayerFile>(
                File.ReadAllText(routesPath), JsonOptions) ??
                throw new InvalidDataException($"Routes layer contained no data: {routesPath}");
            ValidateLayer(routesPath, snapshot, routes.formatVersion,
                routes.mapId, routes.instanceId, routes.coreContentHash);
            snapshot.populationNodes = routes.populationNodes ?? Array.Empty<ServerPopulationRouteNode>();
            snapshot.populationEdges = routes.populationEdges ?? Array.Empty<ServerPopulationRouteEdge>();
            snapshot.populationPortals = routes.populationPortals ?? Array.Empty<ServerPopulationPortal>();
        }
        else
        {
            snapshot.populationNodes = Array.Empty<ServerPopulationRouteNode>();
            snapshot.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
            snapshot.populationPortals = Array.Empty<ServerPopulationPortal>();
            Console.Error.WriteLine($"World route layer absent for map '{snapshot.mapId}'; no baked Population routes loaded.");
        }
    }

    private static void ValidateLayer(string path, ServerMapSnapshot core, int version,
        string mapId, string instanceId, string coreHash)
    {
        if (version != 1 ||
            !string.Equals(mapId, core.mapId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(instanceId ?? string.Empty, core.instanceId ?? string.Empty, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(coreHash) ||
            !string.Equals(coreHash, core.contentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Authoring layer mismatch/stale for '{core.mapId}' at {path}; re-bake the affected layer against current core geometry.");
    }

    private static void ValidateSpawnAnchors(string manifestPath, ServerMapSnapshot snapshot)
    {
        ServerSpawnAnchor[] anchors = snapshot.spawnAnchors ?? Array.Empty<ServerSpawnAnchor>();
        var ids = new HashSet<long>();
        for (int i = 0; i < anchors.Length; ++i)
        {
            ServerSpawnAnchor anchor = anchors[i];
            if (anchor == null)
                throw new InvalidDataException($"Server map '{manifestPath}' spawnAnchors[{i}] is null.");
            if (anchor.stableId <= 0 || !ids.Add(anchor.stableId))
                throw new InvalidDataException($"Server map '{manifestPath}' has invalid/duplicate spawn stableId {anchor.stableId} at index {i}.");
            if (!IsFinite(anchor.pose.x) || !IsFinite(anchor.pose.y) || !IsFinite(anchor.pose.z) || !IsFinite(anchor.pose.yaw))
                throw new InvalidDataException($"Server map '{manifestPath}' spawn '{SpawnLabel(anchor)}' has a non-finite pose.");
            if (!IsFinite(anchor.capsuleRadius) || anchor.capsuleRadius < 0.05f ||
                !IsFinite(anchor.capsuleHeight) || anchor.capsuleHeight < anchor.capsuleRadius * 2f ||
                !IsFinite(anchor.maximumGroundSnap) || anchor.maximumGroundSnap < 0.05f)
            {
                throw new InvalidDataException($"Server map '{manifestPath}' spawn '{SpawnLabel(anchor)}' has invalid capsule/ground-snap settings.");
            }
            if (!IsFinite(anchor.minimumRespawnDelay) || anchor.minimumRespawnDelay < 0f ||
                !IsFinite(anchor.maximumRespawnDelay) || anchor.maximumRespawnDelay < anchor.minimumRespawnDelay)
            {
                throw new InvalidDataException(
                    $"Server map '{manifestPath}' spawn '{SpawnLabel(anchor)}' has invalid respawn delay settings. " +
                    "Expected 0 <= minimumRespawnDelay <= maximumRespawnDelay.");
            }
            if (IsPlayerSpawnKind(anchor.kind) && anchor.actorKind != AuthoritativeActorKind.Player)
            {
                throw new InvalidDataException(
                    $"Server map '{manifestPath}' spawn '{SpawnLabel(anchor)}' is {anchor.kind} but actorKind is {anchor.actorKind}; Player is required.");
            }
        }
    }

    private static bool IsPlayerSpawnKind(ServerSpawnKind kind) =>
        kind == ServerSpawnKind.PlayerFirstSpawn ||
        kind == ServerSpawnKind.PlayerRespawn ||
        kind == ServerSpawnKind.EmergencyFallback;

    private static string SpawnLabel(ServerSpawnAnchor anchor) =>
        string.IsNullOrWhiteSpace(anchor?.label) ? $"id={anchor?.stableId ?? 0}" : anchor.label.Trim();

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private static void ValidateSharedWorld(string mapDirectory, string manifestPath, ServerMapSnapshot snapshot)
    {
        ServerSharedWorldInfo info = snapshot.sharedWorld;
        if (info == null)
            throw new InvalidDataException($"Server map '{manifestPath}' has no shared-world manifest entry.");
        if (info.formatVersion != SharedWorldFormat.Version)
            throw new InvalidDataException($"Server map '{manifestPath}' shared-world format {info.formatVersion} is incompatible with runtime format {SharedWorldFormat.Version}.");
        if (string.IsNullOrWhiteSpace(info.fileName))
            throw new InvalidDataException($"Server map '{manifestPath}' has an empty shared-world filename.");
        string fileName = Path.GetFileName(info.fileName);
        if (!string.Equals(fileName, info.fileName, StringComparison.Ordinal))
            throw new InvalidDataException($"Server map '{manifestPath}' has an unsafe shared-world filename '{info.fileName}'.");
        string sharedPath = Path.Combine(mapDirectory, fileName);
        if (!File.Exists(sharedPath))
            throw new FileNotFoundException($"Shared world data for map '{snapshot.mapId}' was not found.", sharedPath);
        string actualHash = Sha256(sharedPath);
        if (string.IsNullOrWhiteSpace(info.contentHash) ||
            !string.Equals(actualHash, info.contentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Shared world hash mismatch for map '{snapshot.mapId}'. Re-bake the Unity scene.");
    }

    private static void LoadNavMeshMovementCertificate(string mapDirectory, string manifestPath, ServerMapSnapshot snapshot)
    {
        ServerNavMeshInfo info = snapshot.navMesh;
        if (info == null)
            throw new InvalidDataException($"Server map '{manifestPath}' has no server NavMesh manifest entry.");
        if (info.formatVersion != ServerNavMeshInfo.CurrentFormatVersion)
            throw new InvalidDataException($"Server map '{manifestPath}' navmesh format {info.formatVersion} is incompatible with runtime format {ServerNavMeshInfo.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(info.fileName))
            throw new InvalidDataException($"Server map '{manifestPath}' has an empty server NavMesh filename.");
        string fileName = Path.GetFileName(info.fileName);
        if (!string.Equals(fileName, info.fileName, StringComparison.Ordinal))
            throw new InvalidDataException($"Server map '{manifestPath}' has an unsafe server NavMesh filename '{info.fileName}'.");
        string navPath = Path.Combine(mapDirectory, fileName);
        if (!File.Exists(navPath))
            throw new FileNotFoundException($"Server NavMesh for map '{snapshot.mapId}' was not found.", navPath);
        string actualHash = Sha256(navPath);
        if (string.IsNullOrWhiteSpace(info.contentHash) ||
            !string.Equals(actualHash, info.contentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Server NavMesh hash mismatch for map '{snapshot.mapId}'. Re-bake the Unity scene.");

        if (!IsFinite(info.agentRadius) || info.agentRadius < 0.35f - 0.00001f ||
            !IsFinite(info.agentHeight) || info.agentHeight < 1.8f - 0.00001f ||
            !IsFinite(info.agentMaxSlope) || info.agentMaxSlope <= 0f)
            throw new InvalidDataException(
                $"Server NavMesh for map '{snapshot.mapId}' was baked for an undersized player. " +
                "Expected radius >= 0.35 m and height >= 1.80 m. Re-bake this scene.");

        int populatedTiles = 0;
        int groundPolygons = 0;
        var certified = new List<ServerCollisionTriangle>();
        float minimumUp = MathF.Cos(MathF.Min(89f, info.agentMaxSlope) * (MathF.PI / 180f));

        // This is STARTUP-ONLY. The Detour tiles are never queried or searched in the
        // simulation loop. Convert each agent-eroded ground polygon directly into
        // the existing grid-indexed safeMovementTriangles fast path.
        using (FileStream stream = File.OpenRead(navPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            DtNavMesh nav = new DtMeshSetReader().Read(reader);
            for (int tileIndex = 0; tileIndex < nav.GetMaxTiles(); ++tileIndex)
            {
                DtMeshTile tile = nav.GetTile(tileIndex);
                DtMeshData data = tile?.data;
                if (data?.header == null)
                    continue;
                populatedTiles++;
                if (data.polys == null || data.verts == null)
                    continue;

                for (int polyIndex = 0; polyIndex < data.polys.Length; ++polyIndex)
                {
                    DtPoly poly = data.polys[polyIndex];
                    if (poly == null || poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND ||
                        poly.vertCount < 3 || poly.verts == null || poly.vertCount > poly.verts.Length)
                        continue;
                    groundPolygons++;
                    // A Recast ground polygon is convex and already inset for the
                    // baked agent radius. Fan-triangulate it without extending its
                    // footprint outside that certified polygon.
                    for (int vertex = 1; vertex + 1 < poly.vertCount; ++vertex)
                    {
                        if (!TryCreateCertifiedTriangle(data.verts,
                            poly.verts[0], poly.verts[vertex], poly.verts[vertex + 1],
                            minimumUp, out ServerCollisionTriangle triangle))
                            continue;
                        certified.Add(triangle);
                    }
                }
            }
        }

        if (populatedTiles == 0 || groundPolygons == 0 || certified.Count == 0)
            throw new InvalidDataException(
                $"Server NavMesh for map '{snapshot.mapId}' has no valid ground polygons for movement. " +
                "Re-bake the world; do not silently run the expensive fallback on every tick.");

        // The old Unity-generated certificate is intentionally superseded in memory.
        // Existing runtime collision code already checks this certificate FIRST.
        snapshot.safeMovementTriangles = certified.ToArray();
        snapshot.safeMovementRadius = info.agentRadius;
        snapshot.safeMovementHeight = info.agentHeight;
        Console.WriteLine(
            $"[Server Movement] map={snapshot.mapId} instance={snapshot.instanceId} " +
            $"source=DotRecast tiles={populatedTiles} polygons={groundPolygons} " +
            $"certifiedTriangles={certified.Count} radius={info.agentRadius:0.###} " +
            $"height={info.agentHeight:0.###} (Unity safety mesh ignored)");
    }

    private static bool TryCreateCertifiedTriangle(
        float[] verts, int ai, int bi, int ci, float minimumUp,
        out ServerCollisionTriangle triangle)
    {
        triangle = default;
        int a = ai * 3, b = bi * 3, c = ci * 3;
        if (a < 0 || b < 0 || c < 0 ||
            a + 2 >= verts.Length || b + 2 >= verts.Length || c + 2 >= verts.Length)
            return false;
        float ax = verts[a], ay = verts[a + 1], az = verts[a + 2];
        float bx = verts[b], by = verts[b + 1], bz = verts[b + 2];
        float cx = verts[c], cy = verts[c + 1], cz = verts[c + 2];
        if (!IsFinite(ax) || !IsFinite(ay) || !IsFinite(az) ||
            !IsFinite(bx) || !IsFinite(by) || !IsFinite(bz) ||
            !IsFinite(cx) || !IsFinite(cy) || !IsFinite(cz))
            return false;
        float ux = bx - ax, uy = by - ay, uz = bz - az;
        float vx = cx - ax, vy = cy - ay, vz = cz - az;
        float nx = uy * vz - uz * vy;
        float ny = uz * vx - ux * vz;
        float nz = ux * vy - uy * vx;
        float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!IsFinite(length) || length <= 0.000001f)
            return false;
        nx /= length; ny /= length; nz /= length;
        if (ny < 0f)
        {
            (bx, cx) = (cx, bx);
            (by, cy) = (cy, by);
            (bz, cz) = (cz, bz);
            nx = -nx; ny = -ny; nz = -nz;
        }
        if (ny < minimumUp)
            return false;
        triangle = new ServerCollisionTriangle
        {
            surfaceId = 0,
            ax = ax, ay = ay, az = az,
            bx = bx, by = by, bz = bz,
            cx = cx, cy = cy, cz = cz,
            normalX = nx, normalY = ny, normalZ = nz,
            flags = ServerSurfaceFlags.Walkable | ServerSurfaceFlags.Ground,
        };
        return true;
    }

    private static string Sha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
