#nullable enable
using Game.GameServer.Backend;
using Game.GameServer.Networking;
using Game.GameServer.Runtime;

namespace Game.GameServer;

internal static class Program
{
    public static int Main(string[] args)
    {
        CancellationTokenSource? shutdown = null;
        ConsoleCancelEventHandler? cancelHandler = null;
        EventHandler? exitHandler = null;

        try
        {
            GameServerOptions options = GameServerOptions.Parse(args);
            string keyPath = Path.GetFullPath(options.BackendKeyFile, Environment.CurrentDirectory);
            if (!File.Exists(keyPath))
                throw new FileNotFoundException("Backend game-server key was not found.", keyPath);

            string gameServerKey = File.ReadAllText(keyPath).Trim();
            if (string.IsNullOrWhiteSpace(gameServerKey))
                throw new InvalidOperationException("Backend game-server key is empty.");

            shutdown = new CancellationTokenSource();
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                TryCancel(shutdown);
            };
            exitHandler = (_, _) => TryCancel(shutdown);

            Console.CancelKeyPress += cancelHandler;
            AppDomain.CurrentDomain.ProcessExit += exitHandler;

            using var runtime = new GameServerRuntime(
                options.BackendInternalBaseUrl,
                gameServerKey,
                TimeSpan.FromSeconds(8),
                options.MapDataDirectory,
                options.RequireMapData,
                options.StaffAuthorizationFile,
                options.StaffAuditFile);
            using var directoryLease = new BackendGameServerDirectoryLease(
                runtime.Backend,
                options,
                runtime.Maps);
            directoryLease.Start(shutdown.Token);
            using var host = new GameServerHost(options, runtime, directoryLease);

            Console.WriteLine("GameServer starting...");
            host.Run(shutdown.Token);
            Console.WriteLine("GameServer stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("GameServer startup failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (cancelHandler != null)
                Console.CancelKeyPress -= cancelHandler;
            if (exitHandler != null)
                AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            shutdown?.Dispose();
        }
    }

    private static void TryCancel(CancellationTokenSource? shutdown)
    {
        if (shutdown == null)
            return;

        try
        {
            if (!shutdown.IsCancellationRequested)
                shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Process-exit and startup-failure paths can overlap. Cancellation is best-effort here.
        }
    }
}
