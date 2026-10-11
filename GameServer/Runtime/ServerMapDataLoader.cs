using System.Security.Cryptography;
using System.Text.Json;
using DotRecast.Detour.Io;
using Game.Shared.Actors;
using Game.Shared.Population;
using Game.Shared.World;

namespace Game.GameServer.Runtime;

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
            Console.WriteLine($"Server map data: no directory at {fullPath}; legacy flat movement compatibility remains active.");
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

            // Compose independently baked authoring layers into the existing runtime
            // ServerMapSnapshot, so movement, Population and spawns keep their contracts.
            if (snapshot.layerFormatVersion == 1)
                LoadAuthoringLayers(fullPath, file, snapshot);
            else if (snapshot.layerFormatVersion != 0)
                throw new InvalidDataException($"Unsupported map layer format {snapshot.layerFormatVersion} in {file}.");
            ValidateSpawnAnchors(file, snapshot);
            ValidateNavMesh(fullPath, file, snapshot);
            ValidateSharedWorld(fullPath, file, snapshot);
            maps.Add(snapshot);
        }

        if (required && maps.Count == 0)
            throw new InvalidOperationException($"No *.servermap.json files were found in required map directory: {fullPath}");

        Console.WriteLine($"Server map data: loaded {maps.Count} baked map partition(s) from {fullPath}");
        return maps;
    }


    private sealed class RouteLayerFile
    {
        public int formatVersion;
        public string mapId = string.Empty;
        public string instanceId = string.Empty;
        public string coreContentHash = string.Empty;
        public ServerPopulationRouteNode[] populationNodes = Array.Empty<ServerPopulationRouteNode>();
        public ServerPopulationRouteEdge[] populationEdges = Array.Empty<ServerPopulationRouteEdge>();
        public ServerPopulationPortal[] populationPortals = Array.Empty<ServerPopulationPortal>();
    }

    private sealed class SpawnLayerFile
    {
        public int formatVersion;
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
            // Route authoring failures are isolated from ground/movement and spawns.
            snapshot.populationNodes = Array.Empty<ServerPopulationRouteNode>();
            snapshot.populationEdges = Array.Empty<ServerPopulationRouteEdge>();
            snapshot.populationPortals = Array.Empty<ServerPopulationPortal>();
            Console.WriteLine($"World route layer absent for map '{snapshot.mapId}'; no baked Population routes loaded.");
        }
        Console.WriteLine($"World layers: map='{snapshot.mapId}', spawns={snapshot.spawnAnchors.Length}, " +
            $"nodes={snapshot.populationNodes.Length}, edges={snapshot.populationEdges.Length}");
    }

    private static void ValidateLayer(
        string path, ServerMapSnapshot core, int version,
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

    private static void ValidateSharedWorld(
        string mapDirectory,
        string manifestPath,
        ServerMapSnapshot snapshot)
    {
        ServerSharedWorldInfo info = snapshot.sharedWorld;
        if (info == null)
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has no shared-world manifest entry.");
        if (info.formatVersion != SharedWorldFormat.Version)
            throw new InvalidDataException(
                $"Server map '{manifestPath}' shared-world format {info.formatVersion} is incompatible with runtime format {SharedWorldFormat.Version}.");
        if (string.IsNullOrWhiteSpace(info.fileName))
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has an empty shared-world filename.");

        string fileName = Path.GetFileName(info.fileName);
        if (!string.Equals(fileName, info.fileName, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has an unsafe shared-world filename '{info.fileName}'.");

        string sharedPath = Path.Combine(mapDirectory, fileName);
        if (!File.Exists(sharedPath))
            throw new FileNotFoundException(
                $"Shared world data for map '{snapshot.mapId}' was not found.",
                sharedPath);

        string actualHash = Sha256(sharedPath);
        if (string.IsNullOrWhiteSpace(info.contentHash) ||
            !string.Equals(actualHash, info.contentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Shared world hash mismatch for map '{snapshot.mapId}'. Re-bake the Unity scene.");
        }

        Console.WriteLine(
            $"Shared world: map='{snapshot.mapId}', format={info.formatVersion}, file={fileName}, hash={ShortHash(actualHash)}");
    }

    private static string ShortHash(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value[..Math.Min(12, value.Length)];

    private static void ValidateNavMesh(
        string mapDirectory,
        string manifestPath,
        ServerMapSnapshot snapshot)
    {
        ServerNavMeshInfo info = snapshot.navMesh;
        if (info == null)
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has no server NavMesh manifest entry.");
        if (info.formatVersion != ServerNavMeshInfo.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Server map '{manifestPath}' navmesh format {info.formatVersion} is incompatible with runtime format {ServerNavMeshInfo.CurrentFormatVersion}.");
        if (string.IsNullOrWhiteSpace(info.fileName))
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has an empty server NavMesh filename.");

        string fileName = Path.GetFileName(info.fileName);
        if (!string.Equals(fileName, info.fileName, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Server map '{manifestPath}' has an unsafe server NavMesh filename '{info.fileName}'.");

        string navPath = Path.Combine(mapDirectory, fileName);
        if (!File.Exists(navPath))
            throw new FileNotFoundException(
                $"Server NavMesh for map '{snapshot.mapId}' was not found.",
                navPath);

        string actualHash = Sha256(navPath);
        if (string.IsNullOrWhiteSpace(info.contentHash) ||
            !string.Equals(actualHash, info.contentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Server NavMesh hash mismatch for map '{snapshot.mapId}'. Re-bake the Unity scene.");
        }

        int tiles;
        using (FileStream stream = File.OpenRead(navPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            var mesh = new DtMeshSetReader().Read(reader);
            tiles = 0;
            for (int i = 0; i < mesh.GetMaxTiles(); ++i)
            {
                var tile = mesh.GetTile(i);
                if (tile?.data?.header != null)
                    tiles++;
            }
        }

        if (tiles <= 0)
            throw new InvalidDataException(
                $"Server NavMesh for map '{snapshot.mapId}' contains no populated tiles.");

        Console.WriteLine(
            $"Server NavMesh: map='{snapshot.mapId}', tiles={tiles}, " +
            $"agent={info.agentRadius:0.###}r/{info.agentHeight:0.###}h, " +
            $"climb={info.agentMaxClimb:0.###}, slope={info.agentMaxSlope:0.#}°, " +
            $"file={fileName}");
    }

    private static string Sha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
