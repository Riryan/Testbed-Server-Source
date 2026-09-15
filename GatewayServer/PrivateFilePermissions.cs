namespace Game.BackendServer;

internal static class PrivateFilePermissions
{
    public static void RestrictOwnerOnly(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || OperatingSystem.IsWindows() || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException or
            UnauthorizedAccessException or
            IOException)
        {
            Console.Error.WriteLine($"Could not restrict permissions on private file '{path}': {ex.Message}");
        }
    }
}
