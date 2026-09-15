using System.Security.Cryptography;
using System.Text.Json;
using DotRecast.Detour.Io;
using Game.Shared.Actors;
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
