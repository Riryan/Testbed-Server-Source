using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMODashboard;

internal enum AdminDocumentKind
{
    RuntimeConfig,
    GatewaySettings,
    GameplayContent,
}

internal sealed record AdminDocument(
    AdminDocumentKind Kind,
    string DisplayPath,
    string Content,
    string Version);

/// <summary>
/// Dashboard data boundary. The UI only edits canonical server documents through this
/// contract, so local filesystem access can later be swapped for the remote admin API
/// without changing the authoring screens.
/// </summary>
internal interface IServerAdminWorkspace : IDisposable
{
    string DisplayName { get; }
    Task<AdminDocument> ReadAsync(AdminDocumentKind kind, CancellationToken cancellationToken = default);
    Task<AdminDocument> WriteAsync(
        AdminDocumentKind kind,
        string content,
        string? expectedVersion,
        CancellationToken cancellationToken = default);
}

internal sealed class LocalServerAdminWorkspace : IServerAdminWorkspace
{
    private readonly ServerPaths _paths;

    public LocalServerAdminWorkspace(ServerPaths paths) => _paths = paths;

    public string DisplayName => "Local filesystem";

    public async Task<AdminDocument> ReadAsync(AdminDocumentKind kind, CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(kind);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{FriendlyName(kind)} was not found.", path);

        string content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return new AdminDocument(kind, path, content, ComputeVersion(content));
    }

    public async Task<AdminDocument> WriteAsync(
        AdminDocumentKind kind,
        string content,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        string path = ResolvePath(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.Root);

        if (File.Exists(path) && !string.IsNullOrWhiteSpace(expectedVersion))
        {
            string current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            string currentVersion = ComputeVersion(current);
            if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{FriendlyName(kind)} changed on disk after it was loaded. Reload it before saving so another edit is not overwritten.");
            }
        }

        string temp = path + ".dashboard.tmp";
        string backup = path + ".dashboard.bak";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        if (File.Exists(path))
            File.Copy(path, backup, overwrite: true);
        File.Move(temp, path, overwrite: true);

        return new AdminDocument(kind, path, content, ComputeVersion(content));
    }

    private string ResolvePath(AdminDocumentKind kind) => kind switch
    {
        AdminDocumentKind.RuntimeConfig => _paths.ServerConfig ?? _paths.ExpectedServerConfig,
        AdminDocumentKind.GatewaySettings => _paths.GatewayAppSettings ?? _paths.ExpectedGatewayAppSettings,
        AdminDocumentKind.GameplayContent => _paths.GameplayContent ?? _paths.ExpectedGameplayContent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string FriendlyName(AdminDocumentKind kind) => kind switch
    {
        AdminDocumentKind.RuntimeConfig => "ServerConfig.bat",
        AdminDocumentKind.GatewaySettings => "Gateway appsettings.json",
        AdminDocumentKind.GameplayContent => "GameplayContent.json",
        _ => kind.ToString(),
    };

    private static string ComputeVersion(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(hash);
    }

    public void Dispose() { }
}

/// <summary>
/// Future remote implementation for the same authoring UI. The server only needs to
/// expose the small document contract documented in ADMIN_API_CONTRACT.md.
/// </summary>
internal sealed class HttpServerAdminWorkspace : IServerAdminWorkspace
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpServerAdminWorkspace(Uri baseAddress, string? bearerToken = null, HttpClient? client = null)
    {
        if (baseAddress is null) throw new ArgumentNullException(nameof(baseAddress));
        _http = client ?? new HttpClient();
        _ownsClient = client is null;
        string baseText = baseAddress.ToString();
        _http.BaseAddress = baseText.EndsWith("/", StringComparison.Ordinal) ? baseAddress : new Uri(baseText + "/", UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
    }

    public string DisplayName => _http.BaseAddress is null ? "Remote admin API" : $"Remote {_http.BaseAddress}";

    public async Task<AdminDocument> ReadAsync(AdminDocumentKind kind, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(Route(kind), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RemoteDocumentDto dto = await response.Content.ReadFromJsonAsync<RemoteDocumentDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Remote admin API returned an empty document response.");
        return new AdminDocument(kind, dto.displayPath ?? Route(kind), dto.content ?? string.Empty, dto.version ?? string.Empty);
    }

    public async Task<AdminDocument> WriteAsync(
        AdminDocumentKind kind,
        string content,
        string? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var request = new RemoteWriteDto { content = content ?? string.Empty, expectedVersion = expectedVersion ?? string.Empty };
        using HttpResponseMessage response = await _http.PutAsJsonAsync(Route(kind), request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RemoteDocumentDto dto = await response.Content.ReadFromJsonAsync<RemoteDocumentDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Remote admin API returned an empty write response.");
        return new AdminDocument(kind, dto.displayPath ?? Route(kind), dto.content ?? content ?? string.Empty, dto.version ?? string.Empty);
    }

    private static string Route(AdminDocumentKind kind) => kind switch
    {
        AdminDocumentKind.RuntimeConfig => "v1/admin/documents/runtime-config",
        AdminDocumentKind.GatewaySettings => "v1/admin/documents/gateway-settings",
        AdminDocumentKind.GameplayContent => "v1/admin/documents/gameplay-content",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private sealed class RemoteDocumentDto
    {
        public string? displayPath { get; set; }
        public string? content { get; set; }
        public string? version { get; set; }
    }

    private sealed class RemoteWriteDto
    {
        public string content { get; set; } = string.Empty;
        public string expectedVersion { get; set; } = string.Empty;
    }
}
