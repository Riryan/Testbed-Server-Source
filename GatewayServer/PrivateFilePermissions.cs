using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

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
            System.Security.SecurityException or
            InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not restrict permissions on private file '{path}': {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsOwnerOnly(string path)
    {
        // Preserve the file owner. Setting ownership requires WRITE_OWNER and can fail for a
        // correctly non-elevated Gateway even when that process is allowed to protect the DACL.
        // Restrict the existing ACL instead of trying to take ownership.
        var file = new FileInfo(path);
        FileSecurity security = file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        SecurityIdentifier owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Keep the existing owner and grant that owner explicit control after inherited access is
        // removed. This changes the DACL only; it does not require WRITE_OWNER/elevation.
        file.SetAccessControl(security);
    }
}
