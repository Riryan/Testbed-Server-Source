using System.Text.Json;
using Game.Shared.Staff;

namespace Game.GameServer.Runtime;

internal static class StaffAuthorizationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<StaffAuthorizationSnapshot> Load(string filePath)
    {
        string fullPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(filePath) ? "../Content/StaffAuthorizations.json" : filePath,
            Environment.CurrentDirectory);
        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"Staff authorization: no file at {fullPath}; no accounts receive staff authority.");
            return Array.Empty<StaffAuthorizationSnapshot>();
        }

        StaffAuthorizationDocument document = JsonSerializer.Deserialize<StaffAuthorizationDocument>(
            File.ReadAllText(fullPath), JsonOptions) ?? new StaffAuthorizationDocument();
        var valid = new List<StaffAuthorizationSnapshot>();
        foreach (StaffAuthorizationSnapshot entry in document.entries ?? Array.Empty<StaffAuthorizationSnapshot>())
        {
            if (entry == null || entry.accountId <= 0 || entry.CapabilityMask == StaffCapability.None)
                continue;
            valid.Add(entry);
        }
        Console.WriteLine($"Staff authorization: loaded {valid.Count} server-authorized account(s) from {fullPath}");
        return valid;
    }
}
