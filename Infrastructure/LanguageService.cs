using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinsAlt.Core.Domain;

namespace WinsAlt.Infrastructure;

/// <summary>One language the dashboard can be shown in.</summary>
public sealed record LanguageInfo(string Code, string Name, string Source);

/// <summary>
/// The dashboard's language files: flat JSON objects of key → text (help texts as arrays of lines).
///  - Built in: wwwroot/lang/&lt;code&gt;.json, compiled into the executable (English and Thai).
///  - Added by the operator: &lt;data folder&gt;/lang/&lt;code&gt;.json. Read on every request, so a new or corrected
///    file is used without a restart; a file with a built-in code replaces the built-in one.
/// The page always loads English as well and falls back to it for keys a translation lacks.
/// </summary>
public sealed partial class LanguageService
{
    public const int MaxFileBytes = 256 * 1024;
    private const string ResourceMarker = ".wwwroot.lang.";

    private readonly string _customDirectory;
    private readonly ILogger<LanguageService> _logger;
    private readonly Dictionary<string, string> _builtIn = new(StringComparer.Ordinal);

    public LanguageService(WinsOptions options, ILogger<LanguageService> logger)
    {
        _customDirectory = Path.Combine(options.DataDirectory, "lang");
        _logger = logger;

        var assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            int at = resource.IndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || !resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string code = resource[(at + ResourceMarker.Length)..^".json".Length].ToLowerInvariant();
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _builtIn[code] = reader.ReadToEnd();
        }
    }

    /// <summary>The folder an operator puts extra or corrected language files in.</summary>
    public string CustomDirectory => _customDirectory;

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})?$")]
    private static partial Regex CodePattern();

    /// <summary>A language code is the only part of a request that reaches a file path, so it is checked strictly.</summary>
    public static bool IsValidCode(string? code) => code is { Length: <= 12 } && CodePattern().IsMatch(code);

    public IReadOnlyList<LanguageInfo> List()
    {
        var found = new SortedDictionary<string, LanguageInfo>(StringComparer.Ordinal);
        foreach (var (code, text) in _builtIn)
            found[code] = new LanguageInfo(code, NameOf(text) ?? code, "built-in");

        foreach (var (code, text) in CustomFiles())
            found[code] = new LanguageInfo(code, NameOf(text) ?? code, _builtIn.ContainsKey(code) ? "custom, replaces built-in" : "custom");

        return found.Values.ToList();
    }

    /// <summary>The language file's JSON text, or null when there is no such language (or the code is not valid).</summary>
    public string? Get(string code)
    {
        code = code.ToLowerInvariant();
        if (!IsValidCode(code)) return null;
        if (ReadCustom(code) is { } custom) return custom;
        return _builtIn.GetValueOrDefault(code);
    }

    private IEnumerable<(string Code, string Text)> CustomFiles()
    {
        if (!Directory.Exists(_customDirectory)) yield break;
        foreach (string path in Directory.EnumerateFiles(_customDirectory, "*.json"))
        {
            string code = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (IsValidCode(code) && ReadCustom(code) is { } text) yield return (code, text);
        }
    }

    /// <summary>A custom file that exists, is not too large and is a JSON object; otherwise null (and a warning).</summary>
    private string? ReadCustom(string code)
    {
        string path = Path.Combine(_customDirectory, code + ".json");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length > MaxFileBytes)
            {
                _logger.LogWarning("Language file {Path} is larger than {Max} KB and is ignored", path, MaxFileBytes / 1024);
                return null;
            }
            string text = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Language file {Path} is not a JSON object and is ignored", path);
                return null;
            }
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Language file {Path} cannot be used: {Msg}", path, ex.Message);
            return null;
        }
    }

    private static string? NameOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("_name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }
}
