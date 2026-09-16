using System.IO;

namespace MMODashboard;

internal static class DashboardSelfTest
{
    // This test intentionally performs filesystem-only validation.
    // Never launch BATs or long-running server processes from installer/self-test code.
    public static int Run(string[] args)
    {
        try
        {
            var root = GetArgument(args, "--server-root") ?? ServerRootResolver.Resolve(AppContext.BaseDirectory);
            if (root is null || !ServerRootResolver.LooksLikeServerRoot(root))
                return 10;

            var paths = new ServerPaths(root);
            if (!paths.HasRequiredScripts)
                return 11;
            if (paths.ServerConfig is null || !File.Exists(paths.ServerConfig))
                return 13;

            return 0;
        }
        catch
        {
            return 99;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
