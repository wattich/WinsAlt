using System.Globalization;
using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>
/// One static mapping - the equivalent of an LMHOSTS line / WINS static record. A mapping
/// expands to one database record per suffix.
/// </summary>
public sealed class StaticMapping
{
    public string Name { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    /// <summary>NetBIOS suffixes as two hex digits. Empty = "00" (workstation) + "20" (file server).</summary>
    public List<string> Suffixes { get; set; } = new();
    public bool Group { get; set; }
    public string? Comment { get; set; }
}

/// <summary>static-mappings.json root model. Serialized camelCase + indented for hand-editing.</summary>
internal sealed class StaticMappingFileModel
{
    public List<StaticMapping> Mappings { get; set; } = new();
}

/// <summary>
/// Thread-safe read/write of static-mappings.json, pushed into the <see cref="NameStore"/> as
/// non-expiring records. A FileSystemWatcher picks up hand edits, so the file can be maintained
/// like an LMHOSTS file without restarting the service.
/// </summary>
public sealed class StaticMappingService : IDisposable
{
    private static readonly string[] DefaultSuffixes = ["00", "20"];

    private readonly NameStore _store;
    private readonly ILogger<StaticMappingService> _logger;
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly FileSystemWatcher? _watcher;
    private List<StaticMapping> _mappings = new();
    private IReadOnlyList<StaticEntry> _automatic = [];
    private DateTime _lastInternalWrite = DateTime.MinValue;

    public StaticMappingService(NameStore store, WinsOptions options, ILogger<StaticMappingService> logger)
    {
        _store = store;
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "static-mappings.json");
        Load();

        try
        {
            _watcher = new FileSystemWatcher(options.DataDirectory, "static-mappings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Not watching static-mappings.json for edits: {Msg}", ex.Message);
        }
    }

    public IReadOnlyList<StaticMapping> GetAll()
    {
        lock (_lock) return _mappings.Select(Clone).ToList();
    }

    /// <summary>
    /// Replaces the automatically maintained records (this server's own name). They behave like
    /// static mappings but are not written to static-mappings.json, and a mapping the operator
    /// defined for the same name always takes precedence.
    /// </summary>
    public void SetAutomaticEntries(IReadOnlyList<StaticEntry> entries)
    {
        lock (_lock)
        {
            _automatic = entries;
            Apply_NoLock();
        }
    }

    /// <summary>Adds or replaces the mapping with the same name. Returns an error message, or null on success.</summary>
    public string? Upsert(StaticMapping mapping)
    {
        if (Normalize(mapping, out _) is { } error) return error;

        lock (_lock)
        {
            int index = _mappings.FindIndex(m => m.Name == mapping.Name);
            if (index >= 0) _mappings[index] = mapping;
            else _mappings.Add(mapping);
            Save_NoLock();
            Apply_NoLock();
        }
        return null;
    }

    public bool Delete(string name)
    {
        name = name.Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (_mappings.RemoveAll(m => m.Name == name) == 0) return false;
            Save_NoLock();
            Apply_NoLock();
            return true;
        }
    }

    /// <summary>
    /// Validates a mapping and rewrites it in canonical form (upper-case name, two-digit hex
    /// suffixes, dotted addresses). Returns an error message, or null when valid.
    /// </summary>
    private static string? Normalize(StaticMapping m, out List<StaticEntry> entries)
    {
        entries = new List<StaticEntry>();

        m.Name = (m.Name ?? "").Trim().ToUpperInvariant();
        if (!NameKey.TryCreate(m.Name, 0, out _))
            return $"Invalid NetBIOS name '{m.Name}': 1-{NameKey.MaxNameChars} printable ASCII characters, no spaces.";

        var addresses = new List<uint>();
        foreach (var text in m.Addresses ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!Ipv4.TryParse(text, out uint ip) || ip is 0 or Ipv4.LimitedBroadcast) return $"Invalid IPv4 address: {text}";
            if (!addresses.Contains(ip)) addresses.Add(ip);
        }
        if (addresses.Count == 0) return "At least one IPv4 address is required.";
        if (addresses.Count > NbnsPackets.MaxAddresses) return $"At most {NbnsPackets.MaxAddresses} addresses per name.";
        m.Addresses = addresses.Select(Ipv4.ToString).ToList();

        var suffixes = new List<byte>();
        foreach (var text in m.Suffixes is { Count: > 0 } given ? given : DefaultSuffixes.ToList())
        {
            if (!byte.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte suffix))
                return $"Invalid suffix '{text}': expected two hex digits such as 00, 20 or 1C.";
            if (!suffixes.Contains(suffix)) suffixes.Add(suffix);
        }
        m.Suffixes = suffixes.Select(s => s.ToString("X2")).ToList();
        m.Comment = string.IsNullOrWhiteSpace(m.Comment) ? null : m.Comment.Trim();

        var kind = m.Group ? NameKind.Group : addresses.Count > 1 ? NameKind.Multihomed : NameKind.Unique;
        foreach (byte suffix in suffixes)
        {
            NameKey.TryCreate(m.Name, suffix, out var key);
            entries.Add(new StaticEntry(key, kind, addresses.ToArray(), m.Comment));
        }
        return null;
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var model = JsonSerializer.Deserialize(File.ReadAllText(_path), ConfigJsonContext.Default.StaticMappingFileModel);
                    _mappings = model?.Mappings ?? new();
                }
                else
                {
                    _mappings = new();
                    Save_NoLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read static-mappings.json - using no static mappings");
                _mappings = new();
            }
            Apply_NoLock();
        }
    }

    /// <summary>Pushes the current mapping list into the name store, skipping (and logging) invalid rows.</summary>
    private void Apply_NoLock()
    {
        var entries = new List<StaticEntry>();
        var seen = new HashSet<NameKey>();
        var valid = new List<StaticMapping>(_mappings.Count);

        foreach (var mapping in _mappings)
        {
            if (Normalize(mapping, out var expanded) is { } error)
            {
                _logger.LogWarning("Skipped static mapping '{Name}': {Error}", mapping.Name, error);
                continue;
            }
            valid.Add(mapping);
            foreach (var entry in expanded)
                if (seen.Add(entry.Key)) entries.Add(entry);
        }

        foreach (var entry in _automatic)
            if (seen.Add(entry.Key)) entries.Add(entry);

        _mappings = valid;
        _store.ReplaceStatics(entries, Clock.UnixNow());
    }

    private void Save_NoLock()
    {
        var json = JsonSerializer.Serialize(new StaticMappingFileModel { Mappings = _mappings },
            ConfigJsonContext.Default.StaticMappingFileModel);
        _lastInternalWrite = DateTime.UtcNow;
        // Atomic write: temp file then rename-replace (see WinsDatabase).
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Skip events caused by our own save (1-second debounce).
        if (DateTime.UtcNow - _lastInternalWrite < TimeSpan.FromSeconds(1)) return;

        try
        {
            Thread.Sleep(200); // wait for the editor to finish writing
            Load();
            _logger.LogInformation("Detected external change to static-mappings.json - reloaded {Count} mapping(s)", _mappings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to handle external static-mappings change: {Msg}", ex.Message);
        }
    }

    private static StaticMapping Clone(StaticMapping m) => new()
    {
        Name = m.Name,
        Addresses = new List<string>(m.Addresses),
        Suffixes = new List<string>(m.Suffixes),
        Group = m.Group,
        Comment = m.Comment
    };

    public void Dispose() => _watcher?.Dispose();
}
