using System.Text.Json;
using System.Text.Json.Serialization;
using Game.Shared.Content;

namespace Game.BackendServer;

/// <summary>
/// Owns the currently published team-authored gameplay definitions. File changes are
/// observed through FileSystemWatcher, debounced, fully validated, and then atomically
/// activated. GetCurrent() performs no filesystem polling. Invalid edits never replace
/// the last known-good content snapshot.
/// </summary>
internal sealed class ContentDefinitionStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private readonly GameplayContentCatalogV2Reader _catalogReader;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reloadDebounce;

    private GameplayContentSnapshot _current;
    private bool _disposed;

    public string ContentPath => _path;
    public event Action<long> RevisionChanged;

    public ContentDefinitionStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Gameplay content path is required.", nameof(path));

        _path = Path.GetFullPath(path);
        _json.Converters.Add(new JsonStringEnumConverter());
        _catalogReader = new GameplayContentCatalogV2Reader(_path, _json);
        _current = LoadRequired();

        string directory = _catalogReader.ContentRoot;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Gameplay content path must resolve inside a content directory.");

        _reloadDebounce = new Timer(
            static state => ((ContentDefinitionStore)state).ReloadFromFileEvent(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _watcher = new FileSystemWatcher(directory, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };
        _watcher.Changed += OnContentFileChanged;
        _watcher.Created += OnContentFileChanged;
        _watcher.Deleted += OnContentFileChanged;
        _watcher.Renamed += OnContentFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    public GameplayContentSnapshot GetCurrent() => Volatile.Read(ref _current);

    private GameplayContentSnapshot LoadRequired()
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException("Gameplay content definition file was not found.", _path);

        GameplayContentSnapshot snapshot = ReadSnapshot();
        GameplayContentSnapshotCache.Warm(snapshot);
        return snapshot;
    }

    private void OnContentFileChanged(object sender, FileSystemEventArgs args)
    {
        if (_catalogReader.IsCatalogFile(args.FullPath))
            ScheduleReload();
    }

    private void OnContentFileRenamed(object sender, RenamedEventArgs args)
    {
        if (_catalogReader.IsCatalogFile(args.FullPath) || _catalogReader.IsCatalogFile(args.OldFullPath))
            ScheduleReload();
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Console.Error.WriteLine($"[Content] File watcher reported an error; scheduling reconciliation: {args.GetException()?.Message}");
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            // Editors often save through several Changed/Renamed notifications or temporary
            // replace files. Debouncing is event-triggered; it is not periodic polling.
            _reloadDebounce.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadFromFileEvent()
    {
        long activatedRevision = 0;

        lock (_gate)
        {
            if (_disposed || !File.Exists(_path))
                return;

            try
            {
                GameplayContentSnapshot candidate = ReadSnapshot();

                // Duplicate editor notifications after the same save are expected.
                // They are ignored without producing rejection noise.
                if (candidate != null && candidate.revision == _current.revision)
                    return;

                if (!GameplayContentValidation.TryValidateCompatibleUpdate(_current, candidate, out string error))
                {
                    Console.Error.WriteLine($"[Content] Rejected definition update: {error}");
                    return;
                }

                // Warm the validation/lookup cache before publication so request paths
                // never pay a first-use full-catalog scan for the new revision.
                GameplayContentSnapshotCache.Warm(candidate);

                long previous = _current.revision;
                Volatile.Write(ref _current, candidate);
                activatedRevision = candidate.revision;
                Console.WriteLine($"[Content] Activated gameplay content revision {candidate.revision} (previous {previous}).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Content] Failed to reload gameplay definitions. Keeping revision {_current.revision}: {ex.Message}");
            }
        }

        if (activatedRevision > 0)
            RevisionChanged?.Invoke(activatedRevision);
    }

    private GameplayContentSnapshot ReadSnapshot()
    {
        // Catalog V2 reads the manifest and all domain catalogs into one immutable runtime
        // snapshot. Each file is opened with FileShare.ReadWrite by the reader so common
        // editor replace/write patterns still fail closed to the last known-good revision.
        return _catalogReader.Load();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
        }

        _watcher.Dispose();
        _reloadDebounce.Dispose();
    }
}
