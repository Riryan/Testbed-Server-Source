namespace Game.BackendServer;

internal static class PathUtility
{
    public static string Resolve(string contentRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        return Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(contentRoot, path));
    }
}
