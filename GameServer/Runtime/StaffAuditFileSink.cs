using System.Text.Json;
using Game.Shared.Staff;

namespace Game.GameServer.Runtime;

internal sealed class StaffAuditFileSink
{
    private readonly object _gate = new object();
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new JsonSerializerOptions { IncludeFields = true };

    public StaffAuditFileSink(string path)
    {
        _path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "../Logs/StaffAudit.jsonl" : path, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
    }

    public void Write(StaffAuditRecord record)
    {
        if (record == null) return;
        string line = JsonSerializer.Serialize(record, _options);
        lock (_gate)
            File.AppendAllText(_path, line + Environment.NewLine);
    }
}
