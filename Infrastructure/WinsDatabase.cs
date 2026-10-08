using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>wins-db.json root model.</summary>
internal sealed class DbFileModel
{
    public int Version { get; set; } = 1;
    public long SavedAt { get; set; }
    public List<DbRecord> Records { get; set; } = new();
}

internal sealed class DbRecord
{
    /// <summary>The 16 raw name bytes as hex (names are OEM bytes, not text - hex round-trips exactly).</summary>
    public string Name { get; set; } = "";
    public string Kind { get; set; } = nameof(NameKind.Unique);
    public int Flags { get; set; }
    public long RegisteredAt { get; set; }
    public List<DbMember> Members { get; set; } = new();
}

internal sealed class DbMember
{
    public string Ip { get; set; } = "";
    public long ExpiresAt { get; set; }
}

/// <summary>
/// Light-weight persistence for dynamic registrations: the whole table is snapshotted to
/// wins-db.json through the source-generated <see cref="DbJsonContext"/> (no reflection, nothing
/// for the trimmer to lose). Static mappings are not stored here - they live in their own file.
///
/// A snapshot rather than a write-ahead log is the right trade for WINS data: every record is
/// re-announced by its owner within one renewal interval, so losing the last few seconds of
/// registrations in a crash is self-healing, while a restart with an empty table is not.
/// </summary>
public sealed class WinsDatabase
{
    private readonly NameStore _store;
    private readonly ILogger<WinsDatabase> _logger;
    private readonly string _path;
    private readonly Lock _saveLock = new();
    private long _savedVersion;

    public WinsDatabase(NameStore store, WinsOptions options, ILogger<WinsDatabase> logger)
    {
        _store = store;
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "wins-db.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var model = JsonSerializer.Deserialize(File.ReadAllText(_path), DbJsonContext.Default.DbFileModel);
            if (model is null) return;

            long now = Clock.UnixNow();
            var records = new List<NameRecord>(model.Records.Count);
            foreach (var r in model.Records)
            {
                if (!NameKey.TryParseHex(r.Name, out var key)) continue;
                if (!Enum.TryParse<NameKind>(r.Kind, ignoreCase: true, out var kind)) continue;

                var members = new List<NameMember>(r.Members.Count);
                foreach (var m in r.Members)
                    if (m.ExpiresAt > now && Ipv4.TryParse(m.Ip, out uint ip)) members.Add(new NameMember(ip, m.ExpiresAt));
                if (members.Count == 0) continue; // expired while the service was down

                records.Add(new NameRecord(key, kind, (ushort)r.Flags, isStatic: false, r.RegisteredAt, members.ToArray()));
            }

            int restored = _store.Restore(records);
            _savedVersion = _store.Version;
            _logger.LogInformation("Restored {Count} registration(s) from wins-db.json ({Skipped} expired or invalid)",
                restored, model.Records.Count - restored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read wins-db.json - starting with an empty database");
        }
    }

    /// <summary>Writes a snapshot when the store changed since the last save. Returns true if it wrote.</summary>
    public bool SaveIfDirty()
    {
        lock (_saveLock)
        {
            long version = _store.Version;
            if (version == _savedVersion) return false;

            try
            {
                long now = Clock.UnixNow();
                var model = new DbFileModel { SavedAt = now };
                foreach (var record in _store.Records)
                {
                    if (record.IsStatic) continue;

                    var row = new DbRecord
                    {
                        Name = record.Key.ToHex(),
                        Kind = record.Kind.ToString(),
                        Flags = record.NbFlags,
                        RegisteredAt = record.RegisteredAt
                    };
                    foreach (var m in record.Members)
                        if (m.ExpiresAt > now) row.Members.Add(new DbMember { Ip = Ipv4.ToString(m.Address), ExpiresAt = m.ExpiresAt });
                    if (row.Members.Count > 0) model.Records.Add(row);
                }

                // Atomic write: temp file then rename-replace, so a crash mid-write leaves either
                // the old or the new snapshot intact, never a half-written one.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(model, DbJsonContext.Default.DbFileModel));
                File.Move(tmp, _path, overwrite: true);
                _savedVersion = version;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not persist wins-db.json (will retry): {Msg}", ex.Message);
                return false;
            }
        }
    }
}
