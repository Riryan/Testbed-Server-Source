using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMODashboard;

internal sealed class EditableSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Kind { get; set; } = "Text";
}

internal sealed class RuntimeConfigEditor
{
    private readonly List<string> _lines;
    private readonly Dictionary<string, int> _lineByKey;

    public AdminDocument Document { get; private set; }
    public ObservableCollection<EditableSetting> Settings { get; } = new();

    private RuntimeConfigEditor(AdminDocument document, List<string> lines, Dictionary<string, int> lineByKey)
    {
        Document = document;
        _lines = lines;
        _lineByKey = lineByKey;
    }

    public static RuntimeConfigEditor Parse(AdminDocument document)
    {
        string normalized = (document.Content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        var lineByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var editor = new RuntimeConfigEditor(document, lines, lineByKey);

        for (int i = 0; i < lines.Count; ++i)
        {
            if (!TryParseSetLine(lines[i], out string key, out string value))
                continue;
            if (lineByKey.ContainsKey(key))
                continue;
            lineByKey[key] = i;
            editor.Settings.Add(new EditableSetting { Key = key, Value = value, Kind = ClassifyRuntimeKey(key) });
        }

        return editor;
    }

    public string BuildContent()
    {
        foreach (EditableSetting setting in Settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key) || !_lineByKey.TryGetValue(setting.Key, out int lineIndex))
                continue;
            if (setting.Value.Contains('\r') || setting.Value.Contains('\n'))
                throw new InvalidOperationException($"Runtime setting '{setting.Key}' cannot contain a line break.");
            if (setting.Value.Contains('"'))
                throw new InvalidOperationException($"Runtime setting '{setting.Key}' cannot contain a double quote in ServerConfig.bat.");
            _lines[lineIndex] = $"set \"{setting.Key}={setting.Value}\"";
        }
        return string.Join("\r\n", _lines).TrimEnd('\r', '\n') + "\r\n";
    }

    public void ReplaceDocument(AdminDocument document) => Document = document;

    private static bool TryParseSetLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (line is null) return false;
        string text = line.Trim();
        if (!text.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) return false;
        text = text[4..].Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        int equals = text.IndexOf('=');
        if (equals <= 0) return false;
        key = text[..equals].Trim();
        value = text[(equals + 1)..];
        return key.Length > 0;
    }

    private static string ClassifyRuntimeKey(string key)
    {
        if (key.EndsWith("_PORT", StringComparison.OrdinalIgnoreCase)) return "Port";
        if (key.EndsWith("_DIR", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_EXE", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase)) return "Path";
        if (key.Contains("KEY", StringComparison.OrdinalIgnoreCase)) return "Credential / key";
        if (key.Contains("REQUIRE", StringComparison.OrdinalIgnoreCase)) return "Flag";
        if (key.Contains("BACKEND", StringComparison.OrdinalIgnoreCase)) return "Endpoint";
        return "Runtime";
    }
}

internal sealed class GatewaySettingsEditor
{
    private readonly JsonObject _root;
    private readonly JsonObject _gateway;
    private readonly Dictionary<string, JsonValueKind> _kindByKey = new(StringComparer.Ordinal);

    public AdminDocument Document { get; private set; }
    public ObservableCollection<EditableSetting> Settings { get; } = new();

    private GatewaySettingsEditor(AdminDocument document, JsonObject root, JsonObject gateway)
    {
        Document = document;
        _root = root;
        _gateway = gateway;
    }

    public static GatewaySettingsEditor Parse(AdminDocument document)
    {
        JsonNode? node = JsonNode.Parse(document.Content, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        JsonObject root = node as JsonObject ?? throw new InvalidOperationException("Gateway appsettings.json must contain a JSON object.");
        JsonObject gateway = root["GatewayServer"] as JsonObject ?? throw new InvalidOperationException("Gateway appsettings.json is missing the GatewayServer object.");
        var editor = new GatewaySettingsEditor(document, root, gateway);

        foreach ((string key, JsonNode? value) in gateway)
        {
            string display;
            JsonValueKind kind;
            if (value is JsonArray array)
            {
                kind = JsonValueKind.Array;
                display = string.Join(", ", array.Select(v => v?.GetValue<string>() ?? string.Empty));
            }
            else if (value is JsonValue jsonValue)
            {
                using JsonDocument scalar = JsonDocument.Parse(jsonValue.ToJsonString());
                kind = scalar.RootElement.ValueKind;
                display = kind switch
                {
                    JsonValueKind.String => scalar.RootElement.GetString() ?? string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => scalar.RootElement.GetRawText(),
                };
            }
            else
            {
                continue;
            }

            editor._kindByKey[key] = kind;
            editor.Settings.Add(new EditableSetting { Key = key, Value = display, Kind = FriendlyJsonKind(kind) });
        }

        return editor;
    }

    public string BuildContent()
    {
        ValidateKnownSettings();

        foreach (EditableSetting setting in Settings)
        {
            if (!_kindByKey.TryGetValue(setting.Key, out JsonValueKind kind))
                continue;
            _gateway[setting.Key] = ConvertValue(setting.Key, setting.Value, kind);
        }

        return _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public void ReplaceDocument(AdminDocument document) => Document = document;

    private void ValidateKnownSettings()
    {
        int? https = GetInt("HttpsPort");
        int? internalPort = GetInt("InternalPort");
        if (https is < 1 or > 65535) throw new InvalidOperationException("HttpsPort must be 1-65535.");
        if (internalPort is < 1 or > 65535) throw new InvalidOperationException("InternalPort must be 1-65535.");
        if (https.HasValue && internalPort.HasValue && https == internalPort) throw new InvalidOperationException("HttpsPort and InternalPort cannot be the same.");

        ValidateRange("AdmissionLifetimeSeconds", 30, 600);
        ValidateMinimum("Pbkdf2Iterations", 100000);
        ValidateRange("PasswordWorkerCount", 1, 64);
        ValidateRange("PasswordWorkerQueueCapacity", 1, 4096);
        ValidateRange("DatabaseCriticalQueueCapacity", 1, 65536);
        ValidateRange("DatabaseNormalQueueCapacity", 1, 65536);
        ValidateRange("DatabaseBackgroundQueueCapacity", 1, 65536);
        ValidateMinimum("LoginAttemptsPerMinutePerIp", 1);
        ValidateMinimum("AccountCreatesPerTenMinutesPerIp", 1);
        ValidateRange("CharacterLimit", 1, 16);
    }

    private int? GetInt(string key)
    {
        EditableSetting? setting = Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
        if (setting is null) return null;
        if (!int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new InvalidOperationException($"{key} must be an integer.");
        return value;
    }

    private void ValidateRange(string key, int min, int max)
    {
        int? value = GetInt(key);
        if (value.HasValue && (value.Value < min || value.Value > max))
            throw new InvalidOperationException($"{key} must be {min}-{max}.");
    }

    private void ValidateMinimum(string key, int min)
    {
        int? value = GetInt(key);
        if (value.HasValue && value.Value < min)
            throw new InvalidOperationException($"{key} must be at least {min}.");
    }

    private static JsonNode? ConvertValue(string key, string value, JsonValueKind kind)
    {
        switch (kind)
        {
            case JsonValueKind.String:
                return JsonValue.Create(value ?? string.Empty);
            case JsonValueKind.Number:
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return JsonValue.Create(integer);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) return JsonValue.Create(number);
                throw new InvalidOperationException($"{key} must be numeric.");
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (bool.TryParse(value, out bool flag)) return JsonValue.Create(flag);
                if (value == "1") return JsonValue.Create(true);
                if (value == "0") return JsonValue.Create(false);
                throw new InvalidOperationException($"{key} must be true or false.");
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    array.Add(part);
                return array;
            default:
                throw new InvalidOperationException($"{key} has an unsupported JSON value type.");
        }
    }

    private static string FriendlyJsonKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Number => "Number",
        JsonValueKind.True or JsonValueKind.False => "Boolean",
        JsonValueKind.Array => "List (comma separated)",
        _ => "Text",
    };
}
