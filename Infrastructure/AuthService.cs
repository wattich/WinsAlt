using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using WinsAlt.Core.Domain;
using WinsAlt.Web;

namespace WinsAlt.Infrastructure;

/// <summary>Credential record persisted in auth.json (a salted PBKDF2 hash - never the password).</summary>
internal sealed class AuthCreds
{
    public string Username { get; set; } = "admin";
    public int Iterations { get; set; } = 210_000;
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>
/// Dashboard sign-in.
///  - One admin account: username + PBKDF2-SHA256 salted hash in auth.json beside the exe.
///  - A fresh install (no auth.json) signs in with the default admin / admin; the dashboard keeps
///    warning until it is changed, and changing it writes auth.json. An auth.json that exists but
///    cannot be read locks everyone out (fail closed) - deleting it is the recovery, back to admin / admin.
///    Every change needs a signed-in session, from anywhere.
///  - Sessions are in-memory random tokens with a sliding lifetime, delivered as an HttpOnly
///    cookie. Restarting the service signs everyone out (acceptable for a LAN tool).
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "winsalt_session";
    public const int MinPasswordLength = 8;
    private const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const string DefaultUsername = "admin";
    public const string DefaultPassword = "admin";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private readonly string _path;
    private readonly ILogger<AuthService> _logger;
    private readonly Lock _lock = new();
    private AuthCreds? _creds;
    private bool _usingDefault;

    // token -> absolute expiry (UTC). Sliding: each successful validation extends it.
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();

    public AuthService(WinsOptions options, ILogger<AuthService> logger)
    {
        _logger = logger;
        _path = Path.Combine(options.DataDirectory, "auth.json");
        Load();
    }

    /// <summary>True while the built-in admin / admin is in use (no auth.json yet).</summary>
    public bool UsingDefaultPassword
    {
        get { lock (_lock) return _usingDefault; }
    }

    public string Username
    {
        get { lock (_lock) return _creds?.Username ?? DefaultUsername; }
    }

    // ---------- credentials ----------

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _creds = BuildCreds(DefaultUsername, DefaultPassword);
                    _usingDefault = true;
                    _logger.LogWarning("No admin password has been chosen yet: the dashboard signs in with admin / admin until it is changed");
                    return;
                }
                var creds = JsonSerializer.Deserialize(File.ReadAllText(_path), ConfigJsonContext.Default.AuthCreds);
                if (creds is not null && creds.Hash.Length > 0 && creds.Salt.Length > 0) _creds = creds;
                else _logger.LogError("auth.json holds no usable password - nobody can sign in. Delete it to go back to admin / admin");
            }
            catch (Exception ex)
            {
                // Fail closed: falling back to the well-known admin / admin on a dashboard that is open to the
                // LAN would hand it to anyone. Deleting the file is the deliberate way back to the default.
                _logger.LogError(ex, "auth.json is unreadable - nobody can sign in. Delete it to go back to admin / admin");
            }
        }
    }

    private static AuthCreds BuildCreds(string username, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new AuthCreds
        {
            Username = username,
            Iterations = DefaultIterations,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Pbkdf2(password, salt, DefaultIterations))
        };
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

    public bool VerifyPassword(string? username, string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        AuthCreds? c;
        lock (_lock) c = _creds;
        if (c is null) return false;

        if (!string.Equals(username?.Trim(), c.Username, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var expected = Convert.FromBase64String(c.Hash);
            var actual = Pbkdf2(password, Convert.FromBase64String(c.Salt), c.Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception ex)
        {
            // Malformed salt or hash (a hand-edited auth.json) - a normal auth failure, not a 500.
            _logger.LogWarning("Stored credentials are unreadable: {Msg}", ex.Message);
            return false;
        }
    }

    /// <summary>Changes the password (the current one must be given). Returns null on success, or an error message.</summary>
    public string? SetPassword(string? currentPassword, string? newPassword)
    {
        if (!VerifyPassword(Username, currentPassword))
            return "Current password is incorrect.";
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < MinPasswordLength)
            return $"The new password must be at least {MinPasswordLength} characters.";

        lock (_lock)
        {
            bool first = _usingDefault;
            _creds = BuildCreds(_creds?.Username ?? DefaultUsername, newPassword);

            // Atomic write (rename-replace): a half-written auth.json would be unreadable and
            // drop the dashboard back into setup mode.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_creds, ConfigJsonContext.Default.AuthCreds));
            File.Move(tmp, _path, overwrite: true);
            _usingDefault = false;

            _logger.LogWarning(first
                ? "Dashboard admin password changed from the default admin / admin."
                : "Dashboard password changed; all sessions signed out.");
        }

        // Force a fresh sign-in everywhere after any password change.
        _sessions.Clear();
        return null;
    }

    // ---------- sessions ----------

    public string CreateSession()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _sessions[token] = DateTime.UtcNow + SessionLifetime;
        return token;
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiry)) return false;
        if (DateTime.UtcNow > expiry)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = DateTime.UtcNow + SessionLifetime; // sliding expiry
        return true;
    }

    public void RemoveSession(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    // ---------- login throttle (brute-force guard, per source IP) ----------
    // After too many failed logins from one IP, that IP is locked out for a while. The check runs
    // BEFORE the PBKDF2 hash, so a locked IP cannot burn CPU either. State is in memory, so a
    // service restart clears it - which doubles as the operator's escape hatch.
    private sealed class LoginAttempts { public int Count; public DateTime WindowStartUtc; public DateTime LockedUntilUtc; }
    private readonly Dictionary<string, LoginAttempts> _loginAttempts = new();
    private readonly Lock _attemptsLock = new();
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Seconds the IP must wait before trying again (0 = not locked).</summary>
    public int LockoutSecondsRemaining(string ip)
    {
        lock (_attemptsLock)
        {
            if (_loginAttempts.TryGetValue(ip, out var a))
            {
                var remaining = a.LockedUntilUtc - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero) return (int)Math.Ceiling(remaining.TotalSeconds);
            }
            return 0;
        }
    }

    /// <summary>Records a failed login from this IP; locks the IP once it crosses the threshold.</summary>
    public void RecordFailedLogin(string ip)
    {
        var now = DateTime.UtcNow;
        lock (_attemptsLock)
        {
            if (_loginAttempts.Count >= 256)
            {
                // Drop stale entries so the map cannot grow without bound.
                foreach (var key in _loginAttempts.Where(kv => now > kv.Value.LockedUntilUtc && now - kv.Value.WindowStartUtc > FailureWindow)
                             .Select(kv => kv.Key).ToList())
                    _loginAttempts.Remove(key);
            }

            if (!_loginAttempts.TryGetValue(ip, out var a))
                _loginAttempts[ip] = a = new LoginAttempts { WindowStartUtc = now, LockedUntilUtc = DateTime.MinValue };

            if (now - a.WindowStartUtc > FailureWindow) { a.Count = 0; a.WindowStartUtc = now; }
            if (++a.Count >= MaxFailures)
            {
                a.LockedUntilUtc = now + LockoutDuration;
                a.Count = 0;
                a.WindowStartUtc = now;
                _logger.LogWarning("Sign-in locked out for {Ip}: {Max} failed attempts; blocked for {Min} min.",
                    ip, MaxFailures, (int)LockoutDuration.TotalMinutes);
            }
        }
    }

    public void ResetLoginAttempts(string ip)
    {
        lock (_attemptsLock) _loginAttempts.Remove(ip);
    }
}
