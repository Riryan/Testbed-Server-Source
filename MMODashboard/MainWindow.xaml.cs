using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace MMODashboard;

public partial class MainWindow : Window
{
    private readonly ServerPaths _paths;
    private readonly DispatcherTimer _statusTimer;
    private readonly string _configDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MMODashboard");

    private string ConfigPath => Path.Combine(_configDirectory, "config.json");
    private string LegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MMOServerControl",
        "config.json");

    private bool _busy;
    private bool _initializing = true;
    private Process? _gatewayLauncher;
    private readonly ObservableCollection<GameServerInstanceConfig> _gameServerInstances = new();
    private readonly IGameServerControlPlane _gameServerControl = new LocalGameServerControlPlane();

    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x3E, 0xD5, 0x98));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0xD5, 0xDE, 0xED));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8F, 0x9C, 0xB2));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF6, 0xC8, 0x5F));
    private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x8C));
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x7A, 0xE8, 0xB7));

    public MainWindow(string serverRoot)
    {
        InitializeComponent();
        FitInitialWindowToWorkArea();
        _paths = new ServerPaths(serverRoot);
        ServerRootText.Text = _paths.Root;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshRuntimeStatus();

        Loaded += MainWindow_Loaded;
        Closing += (_, _) => SaveConfiguration(silent: true);
        Closed += (_, _) => { _statusTimer.Stop(); _gameServerControl.Dispose(); _adminWorkspace?.Dispose(); };
    }

    private void FitInitialWindowToWorkArea()
    {
        // Keep the native title bar and bottom controls inside the usable desktop,
        // including when Windows display scaling reduces the WPF work area.
        Rect workArea = SystemParameters.WorkArea;
        const double outerMargin = 24;

        double availableWidth = Math.Max(320, workArea.Width - outerMargin);
        double availableHeight = Math.Max(320, workArea.Height - outerMargin);

        // A small display must be allowed to shrink below the normal design minimum
        // rather than forcing the window chrome off-screen. The inner tab/editor
        // regions already scroll where needed.
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Math.Max(Width, MinWidth), availableWidth);
        Height = Math.Min(Math.Max(Height, MinHeight), availableHeight);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConfiguration();
        if (!ConfigurationLooksComplete())
            AutoDetectPaths(logResult: false);

        EnsureGameServerInstances();
        BindGameServerInstances();

        if (!ConfigurationLooksComplete() || !RuntimeConfigurationExists())
            SetupExpander.IsExpanded = true;

        _initializing = false;
        RefreshEnvironment();
        RefreshRuntimeStatus();
        _statusTimer.Start();
        await InitializeAdminAsync();
        Append("MMO Dashboard ready.", LogKind.Success);
    }

    private void LoadConfiguration()
    {
        try
        {
            var path = File.Exists(ConfigPath) ? ConfigPath : File.Exists(LegacyConfigPath) ? LegacyConfigPath : null;
            if (path is null) return;

            var config = JsonSerializer.Deserialize<DashboardConfig>(File.ReadAllText(path));
            if (config is null) return;

            BuildGatewayPathBox.Text = config.BuildGatewayBat ?? "";
            BuildGameServerPathBox.Text = config.BuildGameServerBat ?? "";
            RunGatewayPathBox.Text = config.RunGatewayBat ?? "";
            RunGameServerPathBox.Text = config.RunGameServerBat ?? "";

            _gameServerInstances.Clear();
            foreach (GameServerInstanceConfig instance in config.GameServers ?? new List<GameServerInstanceConfig>())
                _gameServerInstances.Add(instance.Clone());
        }
        catch (Exception ex)
        {
            Append($"Configuration load warning: {ex.Message}", LogKind.Warning);
        }
    }

    private void SaveConfiguration(bool silent = false)
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var config = new DashboardConfig
            {
                BuildGatewayBat = BuildGatewayPathBox.Text.Trim(),
                BuildGameServerBat = BuildGameServerPathBox.Text.Trim(),
                RunGatewayBat = RunGatewayPathBox.Text.Trim(),
                RunGameServerBat = RunGameServerPathBox.Text.Trim(),
                GameServers = _gameServerInstances.Select(instance => instance.Clone()).ToList(),
            };

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            RefreshEnvironment();
            if (!silent) Append("Configuration saved.", LogKind.Success);
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show(this, ex.Message, "Could not save configuration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsExistingScript(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim());

    private bool ConfigurationLooksComplete() =>
        IsExistingScript(BuildGatewayPathBox.Text) &&
        IsExistingScript(BuildGameServerPathBox.Text) &&
        IsExistingScript(RunGatewayPathBox.Text) &&
        IsExistingScript(RunGameServerPathBox.Text);

    private string ExpectedRuntimeConfigPath => Path.Combine(_paths.Root, "Config", "ServerConfig.bat");

    private bool RuntimeConfigurationExists() =>
        _paths.ServerConfig is not null && File.Exists(_paths.ServerConfig);

    private bool RequireRuntimeConfiguration()
    {
        if (RuntimeConfigurationExists()) return true;

        SetupExpander.IsExpanded = true;
        RefreshEnvironment();
        Append("Runtime ServerConfig.bat is missing. Use Install/Repair in Setup / Configuration.", LogKind.Error);
        MessageBox.Show(this,
            "Runtime configuration is missing.\n\nOpen Setup / Configuration and click Install/Repair next to Runtime Config.",
            "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool InstallRuntimeConfiguration()
    {
        try
        {
            var existing = _paths.ServerConfig;
            if (existing is not null && File.Exists(existing))
            {
                ServerConfigPathBox.Text = existing;
                Append($"Runtime configuration already exists: {existing}", LogKind.Info);
                return true;
            }

            var configDirectory = Path.GetDirectoryName(ExpectedRuntimeConfigPath)!;
            Directory.CreateDirectory(configDirectory);
            var template = Path.Combine(configDirectory, "ServerConfig.DevelopmentTemplate.bat");

            if (File.Exists(template))
                File.Copy(template, ExpectedRuntimeConfigPath, overwrite: false);
            else
                File.WriteAllText(ExpectedRuntimeConfigPath, DevelopmentRuntimeConfigText);

            Append($"Installed development runtime configuration: {ExpectedRuntimeConfigPath}", LogKind.Success);
            RefreshEnvironment();
            return true;
        }
        catch (Exception ex)
        {
            Append($"Could not install runtime configuration: {ex.Message}", LogKind.Error);
            MessageBox.Show(this, ex.Message, "Runtime configuration repair failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private const string DevelopmentRuntimeConfigText = """
@echo off
rem Canonical local-development standalone MMO server configuration.
set "SERVER_ROOT=%~dp0.."
for %%I in ("%SERVER_ROOT%") do set "SERVER_ROOT=%%~fI"
set "GATEWAY_SERVER_DIR=%SERVER_ROOT%\GatewayServer"
set "GATEWAY_SERVER_EXE=%SERVER_ROOT%\GatewayServer\GatewayServer.exe"
set "GATEWAY_PUBLIC_PORT=8443"
set "GATEWAY_INTERNAL_PORT=8444"
set "GAME_SERVER_DIR=%SERVER_ROOT%\GameServer"
set "GAME_SERVER_EXE=%SERVER_ROOT%\GameServer\GameServer.exe"
set "GAME_SERVER_PORT=7777"
set "GAME_SERVER_CONNECT_KEY=SampleConnectKey"
set "GAME_SERVER_BACKEND_INTERNAL=http://127.0.0.1:8444"
set "GAME_SERVER_BACKEND_KEY_FILE=%SERVER_ROOT%\Data\game-server-auth.key"
set "GAME_SERVER_REQUIRE_MAP_DATA=0"
""";

    private void RefreshEnvironment()
    {
        if (_initializing) return;

        GatewayScriptText.Text = ScriptSummary(BuildGatewayPathBox.Text, RunGatewayPathBox.Text);
        GameScriptText.Text = ScriptSummary(BuildGameServerPathBox.Text, RunGameServerPathBox.Text);
        ServerConfigPathBox.Text = _paths.ServerConfig ?? ExpectedRuntimeConfigPath;

        EnvironmentStatus.Text = !ConfigurationLooksComplete()
            ? "Configuration required — expand Setup / Configuration or use Auto Detect"
            : !RuntimeConfigurationExists()
                ? "Runtime configuration missing — use Install/Repair in Setup / Configuration"
                : "Configuration ready — build, runtime, and ServerConfig detected";
    }

    private static string ScriptSummary(string? build, string? run) =>
        $"Build: {ScriptName(build)}   •   Run: {ScriptName(run)}";

    private static string ScriptName(string? path) =>
        IsExistingScript(path) ? Path.GetFileName(path!.Trim()) : "missing";

    private void RefreshRuntimeStatus()
    {
        var gatewayRunning = Process.GetProcessesByName("GatewayServer").Any();
        GatewayDot.Fill = gatewayRunning ? Good : Bad;
        GatewayStatus.Text = gatewayRunning ? "Running" : "Stopped";

        int configured = _gameServerInstances.Count;
        int running = 0;
        foreach (GameServerInstanceConfig instance in _gameServerInstances)
        {
            if (_gameServerControl.GetStatus(instance.ServerId).Running)
                running++;
        }

        int allNamedProcesses = 0;
        try
        {
            Process[] processes = Process.GetProcessesByName("GameServer");
            allNamedProcesses = processes.Length;
            foreach (Process process in processes)
                process.Dispose();
        }
        catch { }

        int untracked = Math.Max(0, allNamedProcesses - running);
        bool anyRunning = running > 0 || untracked > 0;
        GameDot.Fill = anyRunning ? Good : Bad;
        GameStatus.Text = untracked > 0
            ? $"{running}/{configured} tracked running • {untracked} external"
            : $"{running}/{configured} running";

        GameServerInstanceCombo.Items.Refresh();
        GameServerInstancesGrid.Items.Refresh();
    }

    private void SetBusy(bool busy, string activity = "Ready")
    {
        _busy = busy;
        BuildAllButton.IsEnabled = !busy;
        BuildStartButton.IsEnabled = !busy;
        BuildGatewayButton.IsEnabled = !busy;
        BuildGameButton.IsEnabled = !busy;
        ActivityText.Text = activity;
    }

    private bool ValidateScript(string? path, string label)
    {
        if (IsExistingScript(path)) return true;
        SetupExpander.IsExpanded = true;
        Append($"{label} script is missing or invalid: {path}", LogKind.Error);
        MessageBox.Show(this, $"Choose a valid batch file for {label}, or use Auto Detect.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task<bool> BuildOneAsync(string? path, string label)
    {
        if (!ValidateScript(path, label)) return false;

        Append($"{label} started.", LogKind.Info);
        var code = await BatchRunner.RunAndWaitAsync(path!.Trim(), (line, stderr) =>
            Dispatcher.Invoke(() => AppendProcessLine(label, line, stderr)));

        Append(code == 0 ? $"{label} completed successfully." : $"{label} failed with exit code {code}.",
            code == 0 ? LogKind.Success : LogKind.Error);
        return code == 0;
    }

    private async Task<bool> BuildAllAsync()
    {
        if (_busy) return false;
        if (!ConfigurationLooksComplete())
        {
            SetupExpander.IsExpanded = true;
            MessageBox.Show(this, "Complete the four script paths first, or use Auto Detect.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        SaveConfiguration(silent: true);
        SetBusy(true, "Building Gateway…");
        Append("Build sequence started.", LogKind.Info);
        try
        {
            if (!await BuildOneAsync(BuildGatewayPathBox.Text, "Compile Gateway"))
            {
                Append("Build sequence stopped: Gateway build failed.", LogKind.Error);
                return false;
            }

            ActivityText.Text = "Building GameServer…";
            if (!await BuildOneAsync(BuildGameServerPathBox.Text, "Compile GameServer"))
            {
                Append("Build sequence stopped: GameServer build failed.", LogKind.Error);
                return false;
            }

            Append("Gateway and GameServer builds passed.", LogKind.Success);
            return true;
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private Process? StartRuntime(string? path, string label)
    {
        if (!ValidateScript(path, label)) return null;
        if (!RequireRuntimeConfiguration()) return null;

        try
        {
            SaveConfiguration(silent: true);
            Append($"Starting {label}…", LogKind.Info);
            return BatchRunner.Start(path!.Trim(),
                (line, stderr) => Dispatcher.Invoke(() => AppendProcessLine(label, line, stderr)),
                code => Dispatcher.Invoke(() =>
                {
                    Append($"{label} launcher exited with code {code}.", code == 0 ? LogKind.Info : LogKind.Error);
                    RefreshRuntimeStatus();
                }));
        }
        catch (Exception ex)
        {
            Append($"{label} failed to start: {ex.Message}", LogKind.Error);
            return null;
        }
    }

    private void StartGateway()
    {
        if (Process.GetProcessesByName("GatewayServer").Any())
        {
            Append("Gateway is already running.", LogKind.Warning);
            return;
        }
        if (!RequireRuntimeConfiguration())
            return;

        try
        {
            SaveConfiguration(silent: true);
            Dictionary<string, string> runtimeConfig = LoadRuntimeSetVariables(
                _paths.ServerConfig ?? ExpectedRuntimeConfigPath,
                _paths.Root);

            string executable = GetRuntimeValue(
                runtimeConfig,
                "GATEWAY_SERVER_EXE",
                Path.Combine(_paths.Root, "GatewayServer", "GatewayServer.exe"));
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable))
            {
                Append($"Gateway executable is missing: {executable}", LogKind.Error);
                MessageBox.Show(this,
                    "GatewayServer.exe is missing. Build Gateway first.",
                    "Gateway", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string workingDirectory = GetRuntimeValue(
                runtimeConfig,
                "GATEWAY_SERVER_DIR",
                Path.GetDirectoryName(executable) ?? Path.Combine(_paths.Root, "GatewayServer"));
            workingDirectory = Path.GetFullPath(workingDirectory);

            // Preserve the existing ServerConfig.bat port settings when bypassing the
            // launcher BAT. ASP.NET Core command-line configuration maps these directly
            // onto the existing GatewayServer options; no new protocol/config path is added.
            var arguments = new List<string>();
            AddRuntimeArgument(arguments,
                "--GatewayServer:HttpsPort",
                GetRuntimeValue(runtimeConfig, "GATEWAY_PUBLIC_PORT", "8443"));
            AddRuntimeArgument(arguments,
                "--GatewayServer:InternalPort",
                GetRuntimeValue(runtimeConfig, "GATEWAY_INTERNAL_PORT", "8444"));

            Append(
                $"Starting GatewayServer.exe directly on HTTPS {GetRuntimeValue(runtimeConfig, "GATEWAY_PUBLIC_PORT", "8443")} / internal {GetRuntimeValue(runtimeConfig, "GATEWAY_INTERNAL_PORT", "8444")}…",
                LogKind.Info);
            _gatewayLauncher = BatchRunner.StartExecutable(
                executable,
                workingDirectory,
                arguments,
                (line, stderr) => Dispatcher.Invoke(() => AppendProcessLine("Gateway", line, stderr)),
                code => Dispatcher.Invoke(() =>
                {
                    Append(
                        code == 0
                            ? "GatewayServer exited normally with code 0."
                            : $"GatewayServer exited with code {code}.",
                        code == 0 ? LogKind.Info : LogKind.Error);
                    _gatewayLauncher = null;
                    RefreshRuntimeStatus();
                }));

            if (_gatewayLauncher == null)
                Append("GatewayServer.exe could not be started.", LogKind.Error);
        }
        catch (Exception ex)
        {
            Append($"Gateway failed to start: {ex.Message}", LogKind.Error);
        }
    }

    private void StartGameServer(GameServerInstanceConfig? instance = null)
    {
        instance ??= GameServerInstanceCombo.SelectedItem as GameServerInstanceConfig;
        if (instance == null)
        {
            Append("Choose a GameServer instance first.", LogKind.Warning);
            return;
        }
        if (!instance.Enabled)
        {
            Append($"{instance.Name} is disabled.", LogKind.Warning);
            return;
        }
        if (!ValidateGameServerInstances(showMessage: true))
            return;
        if (_gameServerControl.GetStatus(instance.ServerId).Running)
        {
            Append($"{instance.Name} ({instance.ServerId}) is already running.", LogKind.Warning);
            return;
        }
        if (!RequireRuntimeConfiguration())
            return;

        try
        {
            SaveConfiguration(silent: true);
            Dictionary<string, string> runtimeConfig = LoadRuntimeSetVariables(
                _paths.ServerConfig ?? ExpectedRuntimeConfigPath,
                _paths.Root);

            string executable = GetRuntimeValue(
                runtimeConfig,
                "GAME_SERVER_EXE",
                Path.Combine(_paths.Root, "GameServer", "GameServer.exe"));
            executable = Path.GetFullPath(executable);
            if (!File.Exists(executable))
            {
                Append($"GameServer executable is missing: {executable}", LogKind.Error);
                MessageBox.Show(this,
                    "GameServer.exe is missing. Build GameServer first.",
                    "GameServer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string workingDirectory = GetRuntimeValue(
                runtimeConfig,
                "GAME_SERVER_DIR",
                Path.GetDirectoryName(executable) ?? Path.Combine(_paths.Root, "GameServer"));
            workingDirectory = Path.GetFullPath(workingDirectory);

            var arguments = new List<string>();
            AddRuntimeArgument(arguments, "--port", instance.Port.ToString());
            AddRuntimeArgument(arguments, "--max-connections", instance.MaxConnections.ToString());
            AddRuntimeArgument(arguments, "--server-id", instance.ServerId.Trim());
            AddRuntimeArgument(arguments, "--advertise-host", instance.AdvertiseHost.Trim());
            AddRuntimeArgument(arguments, "--advertise-port", instance.AdvertisePort.ToString());
            AddRuntimeArgument(arguments, "--connect-key", GetRuntimeValue(runtimeConfig, "GAME_SERVER_CONNECT_KEY", "SampleConnectKey"));
            AddRuntimeArgument(arguments, "--backend-internal", GetRuntimeValue(runtimeConfig, "GAME_SERVER_BACKEND_INTERNAL", "http://127.0.0.1:8444"));
            AddRuntimeArgument(arguments, "--backend-key-file", GetRuntimeValue(
                runtimeConfig,
                "GAME_SERVER_BACKEND_KEY_FILE",
                Path.Combine(_paths.Root, "Data", "game-server-auth.key")));

            // Shared content/runtime paths remain common to every GameServer instance.
            // Instance identity and endpoint are intentionally supplied by the Dashboard.
            AddOptionalRuntimeArgument(arguments, runtimeConfig, "GAME_SERVER_MAP_DATA_DIR", "--map-data-dir");
            AddOptionalRuntimeArgument(arguments, runtimeConfig, "GAME_SERVER_STAFF_AUTH_FILE", "--staff-auth-file");
            AddOptionalRuntimeArgument(arguments, runtimeConfig, "GAME_SERVER_STAFF_AUDIT_FILE", "--staff-audit-file");

            if (IsTruthy(GetRuntimeValue(runtimeConfig, "GAME_SERVER_REQUIRE_MAP_DATA", "0")))
                arguments.Add("--require-map-data");

            string label = $"{instance.Name}/{instance.ServerId}";
            Append($"Starting {label} on UDP {instance.Port}…", LogKind.Info);
            bool started = _gameServerControl.Start(
                instance,
                new GameServerLaunchSpec(executable, workingDirectory, arguments),
                (line, stderr) => Dispatcher.Invoke(() => AppendProcessLine(label, line, stderr)),
                code => Dispatcher.Invoke(() =>
                {
                    Append(
                        code == 0
                            ? $"{label} exited normally with code 0."
                            : $"{label} exited with code {code}.",
                        code == 0 ? LogKind.Info : LogKind.Error);
                    RefreshRuntimeStatus();
                }));

            if (!started)
                Append($"{label} could not be started.", LogKind.Error);
        }
        catch (Exception ex)
        {
            Append($"GameServer failed to start: {ex.Message}", LogKind.Error);
        }

        RefreshRuntimeStatus();
    }

    private static Dictionary<string, string> LoadRuntimeSetVariables(string configPath, string serverRoot)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceLine in File.ReadLines(configPath))
        {
            string line = (sourceLine ?? string.Empty).Trim();
            if (line.StartsWith("@", StringComparison.Ordinal))
                line = line.Substring(1).TrimStart();
            if (!line.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                continue;

            string assignment = line.Substring(4).Trim();
            if (assignment.Length >= 2 && assignment[0] == '"' && assignment[^1] == '"')
                assignment = assignment.Substring(1, assignment.Length - 2);

            int equals = assignment.IndexOf('=');
            if (equals <= 0)
                continue;

            string name = assignment.Substring(0, equals).Trim();
            string value = assignment.Substring(equals + 1);
            if (name.Length > 0)
                raw[name] = value;
        }

        // The BAT derives SERVER_ROOT with %~dp0/for syntax. The Dashboard already knows
        // the canonical root, so use that exact resolved path instead of attempting to
        // interpret cmd.exe metasyntax.
        raw["SERVER_ROOT"] = Path.GetFullPath(serverRoot);

        for (int pass = 0; pass < 8; ++pass)
        {
            bool changed = false;
            foreach (string key in raw.Keys.ToArray())
            {
                string expanded = raw[key];
                foreach (KeyValuePair<string, string> pair in raw)
                    expanded = expanded.Replace("%" + pair.Key + "%", pair.Value, StringComparison.OrdinalIgnoreCase);
                expanded = Environment.ExpandEnvironmentVariables(expanded);
                if (!string.Equals(expanded, raw[key], StringComparison.Ordinal))
                {
                    raw[key] = expanded;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        return raw;
    }

    private static string GetRuntimeValue(
        Dictionary<string, string> values,
        string name,
        string fallback) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static void AddRuntimeArgument(List<string> arguments, string name, string value)
    {
        arguments.Add(name);
        arguments.Add(value);
    }

    private static void AddOptionalRuntimeArgument(
        List<string> arguments,
        Dictionary<string, string> values,
        string settingName,
        string argumentName)
    {
        if (!values.TryGetValue(settingName, out string? value) || string.IsNullOrWhiteSpace(value))
            return;
        AddRuntimeArgument(arguments, argumentName, value.Trim());
    }

    private static bool IsTruthy(string value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private async Task StartAllAsync()
    {
        if (!IsExistingScript(RunGatewayPathBox.Text) || !IsExistingScript(RunGameServerPathBox.Text))
        {
            SetupExpander.IsExpanded = true;
            MessageBox.Show(this, "Run Gateway and Run GameServer paths must be valid first.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!RequireRuntimeConfiguration() || !ValidateGameServerInstances(showMessage: true)) return;

        StartGateway();
        await Task.Delay(1400);
        foreach (GameServerInstanceConfig instance in _gameServerInstances.Where(item => item.Enabled))
        {
            StartGameServer(instance);
            await Task.Delay(250);
        }
        await Task.Delay(500);
        RefreshRuntimeStatus();
    }

    private static void KillByName(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private void StopGateway()
    {
        BatchRunner.TryKillTree(_gatewayLauncher);
        _gatewayLauncher = null;
        KillByName("GatewayServer");
        Append("Gateway stop requested.", LogKind.Info);
        RefreshRuntimeStatus();
    }

    private void StopGameServer(GameServerInstanceConfig? instance = null)
    {
        instance ??= GameServerInstanceCombo.SelectedItem as GameServerInstanceConfig;
        if (instance == null)
        {
            Append("Choose a GameServer instance first.", LogKind.Warning);
            return;
        }

        bool stopped = _gameServerControl.Stop(instance.ServerId);
        Append(
            stopped
                ? $"{instance.Name} ({instance.ServerId}) stop requested."
                : $"{instance.Name} ({instance.ServerId}) is not tracked as a running local instance.",
            stopped ? LogKind.Info : LogKind.Warning);
        RefreshRuntimeStatus();
    }

    private async Task StopAllAsync()
    {
        _gameServerControl.StopAll();
        Append("Tracked GameServer instances stop requested.", LogKind.Info);
        StopGateway();
        await Task.Delay(500);
        RefreshRuntimeStatus();
    }

    private void EnsureGameServerInstances()
    {
        if (_gameServerInstances.Count > 0)
            return;

        _gameServerInstances.Add(new GameServerInstanceConfig
        {
            Name = "GameServer 01",
            ServerId = "gameserver-01",
            Port = 7777,
            AdvertiseHost = "127.0.0.1",
            AdvertisePort = 7777,
            MaxConnections = 4096,
            Enabled = true,
        });
        _gameServerInstances.Add(new GameServerInstanceConfig
        {
            Name = "GameServer 02",
            ServerId = "gameserver-02",
            Port = 7778,
            AdvertiseHost = "127.0.0.1",
            AdvertisePort = 7778,
            MaxConnections = 4096,
            Enabled = true,
        });
    }

    private void BindGameServerInstances()
    {
        GameServerInstanceCombo.ItemsSource = _gameServerInstances;
        GameServerInstancesGrid.ItemsSource = _gameServerInstances;
        if (GameServerInstanceCombo.SelectedItem == null && _gameServerInstances.Count > 0)
            GameServerInstanceCombo.SelectedIndex = 0;
    }

    private bool ValidateGameServerInstances(bool showMessage)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        foreach (GameServerInstanceConfig instance in _gameServerInstances)
        {
            instance.Name = (instance.Name ?? string.Empty).Trim();
            instance.ServerId = (instance.ServerId ?? string.Empty).Trim();
            instance.AdvertiseHost = (instance.AdvertiseHost ?? string.Empty).Trim();

            string? error = null;
            if (instance.Name.Length == 0) error = "GameServer instance name is required.";
            else if (instance.ServerId.Length == 0 || instance.ServerId.Length > 96) error = "GameServer serverId must be 1-96 characters.";
            else if (!ids.Add(instance.ServerId)) error = $"Duplicate GameServer serverId: {instance.ServerId}.";
            else if (instance.Port < 1 || instance.Port > 65535) error = $"{instance.ServerId} UDP port must be 1-65535.";
            else if (!ports.Add(instance.Port)) error = $"Duplicate local GameServer UDP port: {instance.Port}.";
            else if (instance.AdvertiseHost.Length == 0 || instance.AdvertiseHost.Length > 255) error = $"{instance.ServerId} advertise host is invalid.";
            else if (instance.AdvertisePort < 1 || instance.AdvertisePort > 65535) error = $"{instance.ServerId} advertise port must be 1-65535.";
            else if (instance.MaxConnections < 1 || instance.MaxConnections > 100000) error = $"{instance.ServerId} max connections must be 1-100000.";

            if (error == null)
                continue;

            Append(error, LogKind.Error);
            if (showMessage)
                MessageBox.Show(this, error, "GameServer Instances", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void AutoDetectPaths(bool logResult = true)
    {
        BuildGatewayPathBox.Text = KeepOrDetected(BuildGatewayPathBox.Text, _paths.BuildGateway);
        BuildGameServerPathBox.Text = KeepOrDetected(BuildGameServerPathBox.Text, _paths.BuildGameServer);
        RunGatewayPathBox.Text = KeepOrDetected(RunGatewayPathBox.Text, _paths.RunGateway);
        RunGameServerPathBox.Text = KeepOrDetected(RunGameServerPathBox.Text, _paths.RunGameServer);

        RefreshEnvironment();
        if (logResult)
        {
            Append(ConfigurationLooksComplete()
                    ? "Auto Detect found all four scripts."
                    : "Auto Detect finished. Missing paths can be selected manually.",
                ConfigurationLooksComplete() ? LogKind.Success : LogKind.Warning);
        }
    }

    private static string KeepOrDetected(string current, string? detected) =>
        IsExistingScript(current) ? current.Trim() : detected ?? current.Trim();

    private void PickScript(TextBox target)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select batch file",
            Filter = "Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = _paths.Root
        };

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FileName;
            RefreshEnvironment();
        }
    }

    private void AppendProcessLine(string label, string line, bool stderr)
    {
        var kind = stderr || line.Contains(" error ", StringComparison.OrdinalIgnoreCase) || line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)
            ? LogKind.Error
            : line.Contains("warning", StringComparison.OrdinalIgnoreCase) ? LogKind.Warning : LogKind.Info;
        Append($"{label} | {line}", kind);
    }

    private void Append(string text, LogKind kind)
    {
        var brush = kind switch
        {
            LogKind.Error => Error,
            LogKind.Warning => Warn,
            LogKind.Success => Success,
            LogKind.Muted => Muted,
            _ => Normal
        };
        var p = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
        p.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Muted });
        p.Inlines.Add(new Run(text) { Foreground = brush });
        LogBox.Document.Blocks.Add(p);
        LogBox.ScrollToEnd();
    }

    private async void BuildGateway_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Building Gateway…");
        try { await BuildOneAsync(BuildGatewayPathBox.Text, "Compile Gateway"); }
        finally { SetBusy(false); }
    }

    private async void BuildGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Building GameServer…");
        try { await BuildOneAsync(BuildGameServerPathBox.Text, "Compile GameServer"); }
        finally { SetBusy(false); }
    }

    private async void BuildAll_Click(object sender, RoutedEventArgs e) => await BuildAllAsync();

    private async void BuildStart_Click(object sender, RoutedEventArgs e)
    {
        if (!await BuildAllAsync()) return;
        ActivityText.Text = "Starting services…";
        await StartAllAsync();
        ActivityText.Text = "Ready";
    }

    private void RunGateway_Click(object sender, RoutedEventArgs e) => StartGateway();
    private void RunGame_Click(object sender, RoutedEventArgs e) => StartGameServer();
    private void StopGateway_Click(object sender, RoutedEventArgs e) => StopGateway();
    private void StopGame_Click(object sender, RoutedEventArgs e) => StopGameServer();

    private async void RunAllGames_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireRuntimeConfiguration() || !ValidateGameServerInstances(showMessage: true)) return;
        foreach (GameServerInstanceConfig instance in _gameServerInstances.Where(item => item.Enabled))
        {
            StartGameServer(instance);
            await Task.Delay(250);
        }
        RefreshRuntimeStatus();
    }

    private void StopAllGames_Click(object sender, RoutedEventArgs e)
    {
        _gameServerControl.StopAll();
        Append("Tracked GameServer instances stop requested.", LogKind.Info);
        RefreshRuntimeStatus();
    }

    private void AddGameServerInstance_Click(object sender, RoutedEventArgs e)
    {
        int ordinal = 1;
        var usedIds = new HashSet<string>(_gameServerInstances.Select(item => item.ServerId), StringComparer.OrdinalIgnoreCase);
        var usedPorts = new HashSet<int>(_gameServerInstances.Select(item => item.Port));
        while (usedIds.Contains($"gameserver-{ordinal:00}")) ordinal++;
        int port = 7776 + ordinal;
        while (usedPorts.Contains(port) && port < 65535) port++;
        if (port > 65535)
        {
            MessageBox.Show(this, "No free UDP port is available for another local GameServer instance.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var instance = new GameServerInstanceConfig
        {
            Name = $"GameServer {ordinal:00}",
            ServerId = $"gameserver-{ordinal:00}",
            Port = port,
            AdvertiseHost = "127.0.0.1",
            AdvertisePort = port,
            MaxConnections = 4096,
            Enabled = true,
        };
        _gameServerInstances.Add(instance);
        GameServerInstancesGrid.SelectedItem = instance;
        GameServerInstanceCombo.SelectedItem = instance;
        RefreshRuntimeStatus();
    }

    private void RemoveGameServerInstance_Click(object sender, RoutedEventArgs e)
    {
        if (GameServerInstancesGrid.SelectedItem is not GameServerInstanceConfig instance) return;
        if (_gameServerControl.GetStatus(instance.ServerId).Running)
        {
            MessageBox.Show(this, "Stop this GameServer instance before removing it.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _gameServerInstances.Remove(instance);
        if (_gameServerInstances.Count == 0) EnsureGameServerInstances();
        BindGameServerInstances();
        RefreshRuntimeStatus();
    }

    private void SaveGameServerInstances_Click(object sender, RoutedEventArgs e)
    {
        GameServerInstancesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GameServerInstancesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!ValidateGameServerInstances(showMessage: true)) return;
        SaveConfiguration();
        BindGameServerInstances();
        RefreshRuntimeStatus();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e) => await StartAllAsync();
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await StopAllAsync();
    private void Clear_Click(object sender, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    private void BrowseBuildGateway_Click(object sender, RoutedEventArgs e) => PickScript(BuildGatewayPathBox);
    private void BrowseBuildGameServer_Click(object sender, RoutedEventArgs e) => PickScript(BuildGameServerPathBox);
    private void BrowseRunGateway_Click(object sender, RoutedEventArgs e) => PickScript(RunGatewayPathBox);
    private void BrowseRunGameServer_Click(object sender, RoutedEventArgs e) => PickScript(RunGameServerPathBox);
    private void AutoDetectButton_Click(object sender, RoutedEventArgs e) => AutoDetectPaths();
    private void SaveConfigurationButton_Click(object sender, RoutedEventArgs e) => SaveConfiguration();
    private async void InstallRuntimeConfig_Click(object sender, RoutedEventArgs e)
    {
        if (InstallRuntimeConfiguration())
            await LoadRuntimeConfigAsync();
    }
    private void ConfigurationPath_TextChanged(object sender, TextChangedEventArgs e) => RefreshEnvironment();

    private void OpenRoot_Click(object sender, RoutedEventArgs e) => OpenFolder(_paths.Root);
    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        var build = Path.Combine(_paths.Root, "Build");
        Directory.CreateDirectory(build);
        OpenFolder(build);
    }

    private static void OpenFolder(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path }, UseShellExecute = true });
    }

    private enum LogKind { Info, Success, Warning, Error, Muted }
}
