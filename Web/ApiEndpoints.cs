using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using WinsAlt.Core.Domain;
using WinsAlt.Core.Protocol;
using WinsAlt.Infrastructure;
using WinsAlt.Network;

namespace WinsAlt.Web;

/// <summary>The embedded dashboard page and its REST API (Minimal API, source-generated JSON only).</summary>
public static class ApiEndpoints
{
    /// <summary>Header carrying <c>Dashboard:AdminToken</c> for mutating requests.</summary>
    public const string AdminTokenHeader = "X-Admin-Token";

        // The WinsAlt mark (same drawing as #logo-mark in the dashboard): one name server resolving to its hosts.
    private const string FaviconSvg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
          <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stop-color="#67c082"/><stop offset="1" stop-color="#50a868"/>
          </linearGradient></defs>
          <rect width="100" height="100" rx="23" fill="url(#g)"/>
          <path d="M50 37 V52 M50 52 H26 V63 M50 52 H74 V63" fill="none" stroke="#fff" stroke-width="6.5" stroke-linecap="round" stroke-linejoin="round"/>
          <rect x="36" y="17" width="28" height="22" rx="6" fill="#fff"/>
          <circle cx="26" cy="72" r="10" fill="#fff"/><circle cx="74" cy="72" r="10" fill="#fff"/>
          <path d="M43 28 H57" stroke="#4ea567" stroke-width="5" stroke-linecap="round"/>
        </svg>
        """;

    /// <summary>Header every state-changing API call must carry (see <see cref="UseWinsSecurity"/>).</summary>
    public const string CsrfHeader = "X-Requested-With";
    private static readonly long ProcessStartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public const string CsrfHeaderValue = "WinsAlt";

    private const string ContentSecurityPolicy =
        // 'unsafe-eval' + 'unsafe-inline' are what the embedded Vue runtime needs (it compiles the page's
        // template in the browser). Everything is still confined to this origin: no external script,
        // style, image, frame or form target can be pulled in, and the page cannot be framed.
        "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>
    /// Request screening that runs before any endpoint:
    ///  - Host allow-list. Blocks DNS rebinding: a hostile web page whose domain is re-pointed at
    ///    127.0.0.1 would otherwise be "same origin" with the dashboard in the victim's browser.
    ///    IP literals, localhost and the machine's own name are always accepted (rebinding needs a
    ///    name the attacker controls); other names must be listed in Dashboard:AllowedHosts.
    ///  - CSRF. Every non-GET API call must carry X-Requested-With: WinsAlt. A cross-site page
    ///    cannot add a custom header without a CORS preflight, which this server never grants  - 
    ///    this is what protects the bodyless POSTs (and setup mode, where the local machine may
    ///    choose the first password without any cookie). The session cookie is SameSite=Strict on top.
    ///  - Response headers: no framing (clickjacking), no MIME sniffing, a same-origin CSP, no
    ///    referrer, and no caching of API data.
    ///  - Optional sign-in to view (Dashboard:RequireSignInToView) for requests from other machines.
    /// </summary>
    public static void UseWinsSecurity(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<WinsOptions>();

        // Dashboard:AllowedHosts is read from the options on every request - it can be changed while running.
        bool HostAllowed(string host) =>
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || Array.Exists(options.DashboardAllowedHosts, allowed => allowed.Equals(host, StringComparison.OrdinalIgnoreCase));

        app.Use(async (http, next) =>
        {
            string host = http.Request.Host.Host.Trim('[', ']');
            if (!IPAddress.TryParse(host, out _) && !HostAllowed(host))
            {
                await Reject(http, 400, "This host name is not allowed. Use the server's IP address, or add the name to Dashboard:AllowedHosts.");
                return;
            }

            var headers = http.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;

            if (http.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";

                bool safeMethod = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
                if (!safeMethod && http.Request.Headers[CsrfHeader] != CsrfHeaderValue)
                {
                    await Reject(http, 403, $"Missing {CsrfHeader}: {CsrfHeaderValue} header.");
                    return;
                }

                if (safeMethod && !IsPublicPath(http.Request.Path) && !MayView(http))
                {
                    await Reject(http, 401, "Sign in to view this server.");
                    return;
                }
            }

            await next(http);
        });
    }

    // What an unauthenticated browser needs in order to show the sign-in dialog at all.
    // The sign-in dialog is translated before anyone has signed in, so the language files are public too.
    private static bool IsPublicPath(PathString path) =>
        path.Equals("/api/info", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/me", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/lang", StringComparison.OrdinalIgnoreCase);

    private static Task Reject(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ErrorResponse(message), ApiJsonContext.Default.ErrorResponse);
    }

    public static void MapWinsApi(this WebApplication app)
    {
        // ---------- Dashboard page + Vue runtime (embedded in the executable - no CDN) ----------
        app.MapGet("/", () => EmbeddedAssets.IndexHtml is { } html
            ? Results.Bytes(html, "text/html; charset=utf-8")
            : Results.NotFound("Embedded dashboard page not found."));

        app.MapGet("/vue.js", () => EmbeddedAssets.VueJs is { } js
            ? Results.Bytes(js, "application/javascript; charset=utf-8")
            : Results.NotFound("Embedded Vue runtime not found."));

        IResult Favicon() => Results.Content(FaviconSvg, "image/svg+xml; charset=utf-8");
        app.MapGet("/favicon.svg", Favicon);
        app.MapGet("/favicon.ico", Favicon);

        var api = app.MapGroup("/api");

        // ----- Read-only -----
        api.MapGet("/info", (NbnsServer server, SelfRegistrationService self) =>
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            // A Linux host name may be a full DNS name ("wins-linux.example.lan"): the tab only needs the first label.
            var host = self.Name.Split('.')[0];
            return Results.Ok(new InfoResponse(v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}", "WinsAlt NetBIOS Name Server", ProcessStartedAt,
                server.State.ToString(), host));
        });

        // ----- Dashboard languages (public: the sign-in dialog needs them) -----
        api.MapGet("/lang", (LanguageService languages) => Results.Ok(languages.List()));
        api.MapGet("/lang/{code}", (string code, LanguageService languages) =>
            languages.Get(code) is { } json ? Results.Content(json, "application/json; charset=utf-8") : Results.NotFound());

        // ----- Sign-in: session lifecycle -----
        api.MapGet("/me", (HttpContext http, AuthService auth) =>
        {
            bool admin = IsAdmin(http);
            return Results.Ok(new MeResponse(admin, HasSession(http, auth), auth.Username, admin && auth.UsingDefaultPassword, MayView(http)));
        });

        api.MapPost("/login", (LoginRequest request, HttpContext http, AuthService auth) =>
        {
            string ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Brute-force guard: a locked-out IP is refused before the (deliberately slow) hash runs.
            int wait = auth.LockoutSecondsRemaining(ip);
            if (wait > 0)
                return Results.Json(new ErrorResponse($"Too many failed attempts. Try again in {wait}s."),
                    ApiJsonContext.Default.ErrorResponse, statusCode: 429);

            if (!auth.VerifyPassword(request.Username, request.Password))
            {
                auth.RecordFailedLogin(ip);
                return Results.Json(new ErrorResponse("Invalid username or password."),
                    ApiJsonContext.Default.ErrorResponse, statusCode: 401);
            }

            auth.ResetLoginAttempts(ip);
            http.Response.Cookies.Append(AuthService.CookieName, auth.CreateSession(), SessionCookieOptions());
            return Results.Ok(new MeResponse(true, true, auth.Username, auth.UsingDefaultPassword));
        });

        api.MapPost("/logout", (HttpContext http, AuthService auth) =>
        {
            if (http.Request.Cookies.TryGetValue(AuthService.CookieName, out var token)) auth.RemoveSession(token);
            http.Response.Cookies.Delete(AuthService.CookieName);
            return Results.Ok();
        });

        // Changes the password (the current one is checked too); every session is signed out afterwards.
        api.MapPost("/password", (ChangePasswordRequest request, HttpContext http, AuthService auth) =>
        {
            if (!IsAdmin(http))
                return Results.Json(new ErrorResponse("Admin access required."), ApiJsonContext.Default.ErrorResponse, statusCode: 401);

            return auth.SetPassword(request.CurrentPassword, request.NewPassword) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok();
        });

        api.MapGet("/status", (NbnsServer server, NameStore store, WinsCounters c, WinsOptions options, MetricsService metrics,
            SelfRegistrationService self, ReplicaStore replicas) =>
        {
            int total = store.Count, statics = store.StaticCount;
            return Results.Ok(new StatusResponse(
                new ListenerDto(server.State.ToString(), server.EndPoint.ToString(), server.LastError, server.StartedAt,
                    server.Workers, options.AnswerBroadcasts, options.ConflictPolicy.ToString()),
                new DatabaseDto(total, statics, Math.Max(total - statics, 0), replicas.Count),
                new CountersDto(
                    WinsCounters.Read(ref c.PacketsReceived), WinsCounters.Read(ref c.ResponsesSent),
                    WinsCounters.Read(ref c.Queries), WinsCounters.Read(ref c.QueryHits), WinsCounters.Read(ref c.QueryMisses),
                    WinsCounters.Read(ref c.DnsHits), WinsCounters.Read(ref c.PartnerHits), WinsCounters.Read(ref c.ReplicaHits), WinsCounters.Read(ref c.Registrations), WinsCounters.Read(ref c.Refreshes),
                    WinsCounters.Read(ref c.Releases), WinsCounters.Read(ref c.Conflicts), WinsCounters.Read(ref c.RefusedByPolicy),
                    WinsCounters.Read(ref c.BlockedNames), WinsCounters.Read(ref c.RateLimited), WinsCounters.Read(ref c.Oversized), WinsCounters.Read(ref c.Expired),
                    WinsCounters.Read(ref c.Malformed), WinsCounters.Read(ref c.Dropped),
                    WinsCounters.Read(ref c.IgnoredBroadcasts), WinsCounters.Read(ref c.Unsupported)),
                new DnsDto(options.DnsEnabled, options.DnsServers.Select(s => s.ToString()).ToList(), options.DnsSuffix),
                new SelfDto(self.Enabled, self.Name, self.Addresses, self.Group, options.SelfSuffixes.Select(s => s.ToString("X2")).ToList()),
                new SecurityDto(options.RegistrationMode.ToString(), options.AllowedSubnets.Select(s => s.ToString()).ToList(),
                    options.RequireAddressMatchesSource, options.BlockedNames, options.MaxDynamicNames, options.MaxNamesPerAddress,
                    options.RateLimitPerSecond, options.RateLimitBurst, options.RequireSignInToView),
                metrics.Current));
        });

        api.MapGet("/metrics/history", (MetricsService metrics) => Results.Ok(metrics.History()));

        api.MapGet("/names", (string? filter, string? type, string? suffix, string? source, int? limit, NameStore store,
            ReplicaStore replicas, PartnerService partners) =>
        {
            long now = Clock.UnixNow();
            string needle = (filter ?? "").Trim();
            int total = 0;
            var matched = new List<NameDto>();
            int suffixFilter = byte.TryParse(suffix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsed) ? parsed : -1;

            bool Matches(NameDto dto) => needle.Length == 0
                || dto.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || dto.Addresses.Any(a => a.Contains(needle, StringComparison.Ordinal));

            // "type=replica" predates the source filter and means the same thing.
            if (type == "replica") { source = "replication"; type = null; }
            bool wantLocal = source is null or "" or "local";
            bool wantReplication = source is null or "" or "replication";
            bool wantPartner = source is null or "" or "partner";
            var listed = new HashSet<NameKey>();

            // Answers forwarded from partner WINS servers (cache only) - shown unless the name is
            // also known locally or through replication, which is what a client would be answered from.
            foreach (var cachedName in partners.CachedNames(now))
            {
                if (store.TryGet(cachedName.Key, out var known) && known.IsActive(now)) continue;
                if (replicas.TryGet(cachedName.Key, out var copy) && copy.IsActive(now)) continue;
                total++;
                if (!wantPartner || type is "static" or "dynamic") continue;
                if (suffixFilter >= 0 && cachedName.Key.Suffix != suffixFilter) continue;

                var dto = ToDto(cachedName);
                if (Matches(dto)) matched.Add(dto);
            }

            // Names owned by other WinsAlt servers - shown unless this server holds the same name itself.
            foreach (var replica in replicas.Records)
            {
                // One row per name = what a client is answered from: a replica is hidden by a local record,
                // except that a replicated static mapping outranks a local registration.
                if (!replica.IsActive(now)) continue;
                if (store.TryGet(replica.Key, out var own) && own.IsActive(now) && (own.IsStatic || !replica.IsStatic)) continue;
                total++;
                if (!wantReplication || type is "static" or "dynamic") continue;
                if (suffixFilter >= 0 && replica.Key.Suffix != suffixFilter) continue;

                var dto = ToDto(replica, now);
                if (Matches(dto)) matched.Add(dto);
            }

            foreach (var record in store.Records)
            {
                if (!record.IsActive(now)) continue;
                if (!record.IsStatic && replicas.IsStatic(record.Key, now)) continue; // outranked by a static mapping elsewhere
                total++;
                if (!wantLocal) continue;
                if (type == "static" && !record.IsStatic) continue;
                if (type == "dynamic" && record.IsStatic) continue;
                if (suffixFilter >= 0 && record.Key.Suffix != suffixFilter) continue;

                var dto = ToDto(record, now);
                if (Matches(dto)) matched.Add(dto);
            }

            matched.Sort(static (a, b) =>
            {
                int byName = string.CompareOrdinal(a.Name, b.Name);
                return byName != 0 ? byName : string.CompareOrdinal(a.Suffix, b.Suffix);
            });

            int take = Math.Clamp(limit ?? 500, 1, 5000);
            return Results.Ok(new NamesResponse(total, matched.Count, matched.Count > take ? matched.GetRange(0, take) : matched));
        });

        // Resolves a name exactly the way a client's query would: database first, then DNS fallback.
        api.MapGet("/resolve", async (string? name, string? suffix, NameStore store, DnsFallbackResolver dns, PartnerService partners, ReplicaStore replicas,
            WinsOptions options, CancellationToken ct) =>
        {
            byte suffixByte = 0x20;
            if (!string.IsNullOrWhiteSpace(suffix)
                && !byte.TryParse(suffix.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out suffixByte))
                return Results.BadRequest(new ErrorResponse("Suffix must be two hex digits, e.g. 00 or 20."));
            if (!NameKey.TryCreate(name, suffixByte, out var key))
                return Results.BadRequest(new ErrorResponse($"Name must be 1-{NameKey.MaxNameChars} printable ASCII characters."));

            var addresses = new uint[NbnsPackets.MaxAddresses];
            var outcome = store.Query(key, Clock.UnixNow(), options.MaxTtlSeconds, addresses, out int count, out _, out _);
            string source = outcome switch
            {
                QueryOutcome.StaticHit => "static",
                QueryOutcome.Hit => "registration",
                _ => "none"
            };

            // Same precedence as a client query: a replicated static mapping outranks a local registration.
            if ((outcome == QueryOutcome.Miss || (outcome == QueryOutcome.Hit && replicas.IsStatic(key, Clock.UnixNow())))
                && replicas.Query(key, Clock.UnixNow(), options.MaxTtlSeconds, addresses, out int replicaCount, out _, out _))
            {
                count = replicaCount;
                source = "replica";
            }

            if (count == 0 && partners.Enabled && await partners.ResolveAsync(key, ct) is { } answer)
            {
                count = Math.Min(answer.Addresses.Length, addresses.Length);
                answer.Addresses.AsSpan(0, count).CopyTo(addresses);
                source = "partner";
            }

            if (count == 0 && dns.Enabled && DnsFallbackResolver.IsEligible(key))
            {
                uint resolved = await dns.ResolveAsync(key, ct);
                if (resolved != 0)
                {
                    addresses[0] = resolved;
                    count = 1;
                    source = "dns";
                }
            }

            return Results.Ok(new ResolveResponse(key.ToDisplayName(), key.Suffix.ToString("X2"), count > 0, source,
                addresses.Take(count).Select(Ipv4.ToString).ToList()));
        });

        api.MapGet("/querylog", (int? limit, QueryLog log) => Results.Ok(log.Snapshot(Math.Clamp(limit ?? 200, 1, 5000))));

        api.MapGet("/logs", (InMemoryLogStore store) => Results.Ok(store.Snapshot()));

        api.MapGet("/static", (StaticMappingService mappings) => Results.Ok(mappings.GetAll()));

        // Windows' own NetBIOS over TCP/IP per adapter - it must be off where the server listens.
        api.MapGet("/netbt", (NetbtAdapterService netbt) => Results.Ok(new NetbtResponse(netbt.Supported, netbt.List())));

        // Partner WINS servers that unknown names are looked up on.
        api.MapGet("/partners", (PartnerService partners) => Results.Ok(partners.GetAll()));

        // Replication with other WinsAlt servers.
        api.MapGet("/replication", (ReplicationService replication) => Results.Ok(replication.GetStatus()));

        // The Windows Firewall rules other machines need in order to reach this server.
        api.MapGet("/firewall", (FirewallService firewall, CancellationToken ct) => FirewallState(firewall, ct));

        // Every setting the dashboard can change, with its explanation and whether it needs a restart.
        api.MapGet("/settings", (SettingsService settings) => Results.Ok(settings.Describe()));

        // ----- Mutations (admin only) -----
        // Body: { "<config key>": "<value>", ... } - only the settings being changed.
        api.MapPut("/settings", (Dictionary<string, string> changes, SettingsService settings) =>
            settings.Save(changes) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok(settings.Describe()))
            .AddEndpointFilter(RequireAdmin);

        // Reveals the saved key so it can be copied to the other servers. Admin only, like every change.
        api.MapGet("/replication/key", (ReplicationService replication) => Results.Ok(new ReplicationKeyRequest(replication.GetKey())))
            .AddEndpointFilter(RequireAdmin);

        api.MapPost("/replication/key", (ReplicationKeyRequest request, ReplicationService replication) =>
            replication.SetKey(request.Key) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok(replication.GetStatus()))
            .AddEndpointFilter(RequireAdmin);

        api.MapPost("/replication/peers", (ReplicationPeer peer, ReplicationService replication) =>
            replication.UpsertPeer(peer) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok(replication.GetStatus()))
            .AddEndpointFilter(RequireAdmin);

        api.MapDelete("/replication/peers/{address}", (string address, ReplicationService replication) =>
            replication.DeletePeer(address) ? Results.NoContent() : Results.NotFound())
            .AddEndpointFilter(RequireAdmin);

        // Incident response: forget everything learned from partner servers and DNS (hits and misses).
        api.MapPost("/cache/clear", (PartnerService partners, DnsFallbackResolver dns, ILoggerFactory logs) =>
        {
            int cleared = partners.ClearCache() + dns.ClearCache();
            logs.CreateLogger("Dashboard").LogWarning("Forwarding caches cleared from the dashboard ({Count} entries)", cleared);
            return Results.Ok(new CacheClearResponse(cleared));
        }).AddEndpointFilter(RequireAdmin);

        api.MapPost("/partners", (PartnerServer server, PartnerService partners) =>
            partners.Upsert(server) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok(partners.GetAll()))
            .AddEndpointFilter(RequireAdmin);

        api.MapDelete("/partners/{address}", (string address, PartnerService partners) =>
            partners.Delete(address) ? Results.NoContent() : Results.NotFound())
            .AddEndpointFilter(RequireAdmin);

        // Restarts the Windows service (what settings marked "restart" wait for). The answer goes out
        // first; a helper process then stops and starts the service - see ServiceControl.
        api.MapPost("/service/restart", (ILoggerFactory logs, IHostApplicationLifetime lifetime) =>
        {
            if (ServiceControl.BeginRestart(lifetime) is { } error) return Results.BadRequest(new ErrorResponse(error));
            logs.CreateLogger("Dashboard").LogWarning("Service restart requested from the dashboard");
            return Results.Ok();
        }).AddEndpointFilter(RequireAdmin);

        api.MapPost("/firewall/{id}", async (string id, bool allow, FirewallService firewall, CancellationToken ct) =>
            await firewall.SetAsync(id, allow, ct) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : await FirewallState(firewall, ct))
            .AddEndpointFilter(RequireAdmin);

        api.MapPost("/netbt/{id}", async (string id, bool enabled, NetbtAdapterService netbt, NbnsServer server, CancellationToken ct) =>
        {
            if (await netbt.SetAsync(id, enabled, ct) is { } error)
                return Results.BadRequest(new ErrorResponse(error));

            // UDP 137 may just have been freed - do not make the operator wait for the retry timer.
            if (!enabled) server.RetryNow();
            return Results.Ok(new NetbtResponse(netbt.Supported, netbt.List()));
        }).AddEndpointFilter(RequireAdmin);

        api.MapPost("/static", (StaticMapping mapping, StaticMappingService mappings) =>
            mappings.Upsert(mapping) is { } error
                ? Results.BadRequest(new ErrorResponse(error))
                : Results.Ok(mapping))
            .AddEndpointFilter(RequireAdmin);

        api.MapDelete("/static/{name}", (string name, StaticMappingService mappings) =>
            mappings.Delete(name) ? Results.NoContent() : Results.NotFound())
            .AddEndpointFilter(RequireAdmin);

        // Force-removes one dynamic registration (its owner simply re-registers at next refresh).
        api.MapDelete("/names/{id}", (string id, NameStore store) =>
            NameKey.TryParseHex(id, out var key) && store.Remove(key) ? Results.NoContent() : Results.NotFound())
            .AddEndpointFilter(RequireAdmin);
    }

    private static async Task<IResult> FirewallState(FirewallService firewall, CancellationToken ct)
    {
        try
        {
            var kind = await firewall.DetectAsync(ct);
            return Results.Ok(new FirewallResponse(kind != FirewallService.Kind.None, await firewall.ListAsync(ct), null, FirewallService.DisplayName(kind)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Results.Ok(new FirewallResponse(false, [], "Could not ask the firewall: " + ex.Message, "unknown"));
        }
    }

    // ---------- Admin guard ----------
    // Endpoint filter: reject the call with 401 unless the caller may change server state.
    private static async ValueTask<object?> RequireAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (!IsAdmin(ctx.HttpContext))
            return Results.Json(new ErrorResponse("Admin access required."), ApiJsonContext.Default.ErrorResponse, statusCode: 401);
        return await next(ctx);
    }

    /// <summary>
    /// Who may change things - nobody who has not identified themselves:
    ///  1. a signed-in dashboard session (cookie);
    ///  2. a caller presenting <c>Dashboard:AdminToken</c> in the X-Admin-Token header (for scripts).
    /// Everyone else, the server's own console included, can only look.
    /// </summary>
    private static bool IsAdmin(HttpContext http)
    {
        var auth = http.RequestServices.GetRequiredService<AuthService>();
        if (HasSession(http, auth)) return true;

        string token = http.RequestServices.GetRequiredService<WinsOptions>().AdminToken;
        if (token.Length == 0) return false;

        string presented = http.Request.Headers[AdminTokenHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Who may read data: everyone, unless Dashboard:RequireSignInToView is on (the default) - then only
    /// whoever may change things. The server's own console is not exempt: behind a reverse proxy on the
    /// same host every LAN visitor would arrive from loopback.
    /// </summary>
    private static bool MayView(HttpContext http) =>
        !http.RequestServices.GetRequiredService<WinsOptions>().RequireSignInToView || IsAdmin(http);

    private static bool HasSession(HttpContext http, AuthService auth) =>
        http.Request.Cookies.TryGetValue(AuthService.CookieName, out var token) && auth.ValidateToken(token);

    // Plain HTTP on the LAN: Secure must be false or the browser silently drops the cookie.
    // SameSite=Strict is the CSRF mitigation for the state-changing endpoints.
    private static CookieOptions SessionCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = false,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = TimeSpan.FromHours(8)
    };

    // ---------- Mapping ----------
    private static NameDto ToDto(NameRecord record, long now)
    {
        var addresses = new List<string>();
        long expires = 0;
        foreach (var member in record.Members)
        {
            if (member.ExpiresAt <= now) continue;
            addresses.Add(Ipv4.ToString(member.Address));
            if (!record.IsStatic && member.ExpiresAt > expires) expires = member.ExpiresAt;
        }

        bool group = record.Kind == NameKind.Group;
        return new NameDto(
            record.Key.ToHex(),
            record.Key.ToDisplayName(),
            record.Key.Suffix.ToString("X2"),
            SuffixRole(record.Key.Suffix, group),
            record.Kind.ToString(),
            NbFlags.NodeTypeName(record.NbFlags),
            record.IsStatic,
            addresses,
            record.RegisteredAt,
            expires,
            record.Comment);
    }

    private static NameDto ToDto(ReplicaRecord replica, long now)
    {
        var addresses = new List<string>();
        foreach (var member in replica.Members)
            if (member.ExpiresAt > now) addresses.Add(Ipv4.ToString(member.Address));

        return new NameDto(
            replica.Key.ToHex(),
            replica.Key.ToDisplayName(),
            replica.Key.Suffix.ToString("X2"),
            SuffixRole(replica.Key.Suffix, replica.Kind == NameKind.Group),
            replica.Kind.ToString(),
            NbFlags.NodeTypeName(replica.NbFlags),
            replica.IsStatic,
            addresses,
            replica.RegisteredAt,
            replica.IsStatic ? 0 : replica.LatestExpiry,
            Comment: null,
            Origin: replica.Origin,
            Source: "Replication");
    }

    private static NameDto ToDto(CachedPartnerName cached)
    {
        bool group = (cached.Answer.NbFlags & NbFlags.Group) != 0;
        return new NameDto(
            cached.Key.ToHex(),
            cached.Key.ToDisplayName(),
            cached.Key.Suffix.ToString("X2"),
            SuffixRole(cached.Key.Suffix, group),
            group ? nameof(NameKind.Group) : cached.Answer.Addresses.Length > 1 ? nameof(NameKind.Multihomed) : nameof(NameKind.Unique),
            NbFlags.NodeTypeName(cached.Answer.NbFlags),
            IsStatic: false,
            cached.Answer.Addresses.Select(Ipv4.ToString).ToList(),
            RegisteredAt: 0,
            cached.ExpiresAt,
            Comment: null,
            Origin: cached.Answer.Source,
            Source: "Partner");
    }

    /// <summary>What the well-known NetBIOS suffix bytes mean.</summary>
    private static string SuffixRole(byte suffix, bool group) => suffix switch
    {
        0x00 => group ? "Domain / Workgroup" : "Workstation",
        0x01 => group ? "Master Browser (MSBROWSE)" : "Messenger",
        0x03 => "Messenger",
        0x06 => "RAS Server",
        0x1B => "Domain Master Browser",
        0x1C => "Domain Controllers",
        0x1D => "Master Browser",
        0x1E => "Browser Election",
        0x1F => "NetDDE",
        0x20 => "File Server",
        0x21 => "RAS Client",
        _ => "Other"
    };
}
