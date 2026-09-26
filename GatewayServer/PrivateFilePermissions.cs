using System.Security.AccessControl;
using System.Security.Principal;

namespace Game.BackendServer;

internal static class PrivateFilePermissions
{
    public static void RestrictOwnerOnly(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictWindowsOwnerOnly(path);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (
            ex is PlatformNotSupportedException or
            UnauthorizedAccessException or
            IOException or
            SystemException)
        {
            Console.Error.WriteLine($"Could not restrict permissions on private file '{path}': {ex.Message}");
        }
    }

    private static void RestrictWindowsOwnerOnly(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no security identifier.");

        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
    }
}
