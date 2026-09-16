using System.Diagnostics;
using System.IO;

namespace MMODashboard;

internal static class BatchRunner
{
    public static async Task<int> RunAndWaitAsync(
        string batchPath,
        Action<string, bool>? onLine = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchPath) || !File.Exists(batchPath))
            return -1;

        using var process = CreateProcess(batchPath);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, false); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, true); };

        if (!process.Start()) return -1;

        // Existing maintenance BATs may contain PAUSE. EOF prevents an invisible hang.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            return -2;
        }
    }

    public static Process? Start(string batchPath, Action<string, bool>? onLine = null, Action<int>? onExit = null)
    {
        if (string.IsNullOrWhiteSpace(batchPath) || !File.Exists(batchPath))
            return null;

        var process = CreateProcess(batchPath);
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, false); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, true); };
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            onExit?.Invoke(code);
            process.Dispose();
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }


    public static Process? StartExecutable(
        string executablePath,
        string workingDirectory,
        IEnumerable<string>? arguments = null,
        Action<string, bool>? onLine = null,
        Action<int>? onExit = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        string resolvedWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = resolvedWorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };

        if (arguments != null)
        {
            foreach (string argument in arguments)
            {
                if (argument != null)
                    psi.ArgumentList.Add(argument);
            }
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, false); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data, true); };
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            onExit?.Invoke(code);
            process.Dispose();
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static Process CreateProcess(string batchPath)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(comSpec))
            comSpec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(batchPath)) ?? Environment.CurrentDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = comSpec,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        // IMPORTANT: ArgumentList lets .NET quote the batch path correctly. This is the
        // fix for paths such as C:\MMO\Project Testbed\... . No nested cmd quote string.
        psi.ArgumentList.Add("/D");
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add(Path.GetFullPath(batchPath));

        return new Process { StartInfo = psi };
    }

    public static void TryKillTree(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch { }
    }
}
