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
        return valid;
    }
}
