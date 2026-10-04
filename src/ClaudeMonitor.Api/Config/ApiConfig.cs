namespace ClaudeMonitor.Api.Config;

/// <summary>
/// The API's ONE configuration module (global #2): the only place that reads the environment or holds a URL,
/// port, path, lifetime or price. Every other file takes an <see cref="ApiConfig"/>. Development fallbacks are
/// defined here and only here, and only apply when the environment is Development or Testing.
/// Every variable is listed in .env.example.
/// </summary>
public sealed record ApiConfig
{
    public required string DatabaseUrl { get; init; }
    public required string PublicOrigin { get; init; }
    public required SmtpSettings Smtp { get; init; }
    public OAuthClient? GitHub { get; init; }
    public OAuthClient? Google { get; init; }
    public required string ArchiveDir { get; init; }
    public string? WebRoot { get; init; }
    public required string Commit { get; init; }
    public required Version MinimumAgentVersion { get; init; }

    /// <summary>32 bytes that seal the TOTP secrets at rest (MONITOR_MFA_KEY, base64url).</summary>
    public required byte[] MfaKey { get; init; }
    public bool SecureCookies { get; init; } = true;
    public bool BackgroundJobs { get; init; } = true;
    public bool TrustProxy { get; init; }

    /// <summary>The proxies whose X-Forwarded-* headers count (CIDR list); required when TrustProxy is on outside development.</summary>
    public IReadOnlyList<System.Net.IPNetwork> ProxyNetworks { get; init; } = [];
    public int AuthRequestsPerMinute { get; init; } = 20;
    public int LockoutAfter { get; init; } = 5;
    public TimeSpan LockoutFor { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan MfaPendingLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int RecoveryCodes { get; init; } = 10;

    /// <summary>How fresh a sign-in must be to delete an account that has neither a password nor two-step sign-in.</summary>
    public TimeSpan ReauthWindow { get; init; } = TimeSpan.FromMinutes(10);
    public const string MfaIssuer = "Claude Monitor";

    public TimeSpan LoginSessionLifetime { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan EmailTokenLifetime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan DeviceCodeLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public int DevicePollSeconds { get; init; } = 5;
    public TimeSpan AgentAccessLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AgentRefreshLifetime { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan CommandLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public int PermissionWaitMaxSeconds { get; init; } = 600;
    public int MaxBatchEvents { get; init; } = 500;
    public long MaxBatchBytes { get; init; } = 8 * 1024 * 1024;
    public int PageSizeMax { get; init; } = 100;

    /// <summary>USD per million tokens: input, output, cache read, cache write (5-minute TTL, input x 1.25).
    /// A model not listed is NOT priced: its cost stays null ("cannot be measured"), never a guess.</summary>
    public IReadOnlyDictionary<string, ModelPrice> Prices { get; init; } = DefaultPrices;

    public const string SessionCookie = "cm_session";

    /// <summary>The provider sign-in's pending two-step token (never in the address), sent only to the second step.</summary>
    public const string MfaCookie = "cm_mfa";
    public const string MfaCookiePath = "/api/auth/mfa";
    public const string CsrfHeader = "X-CSRF";
    public const long BatchBodyLimit = 16 * 1024 * 1024;
    /// <summary>The configuration node SMTP may also be read from (e.g. a server-only appsettings.Production.json).</summary>
    public const string SmtpSection = "Smtp";

    public static readonly IReadOnlyDictionary<string, ModelPrice> DefaultPrices = new Dictionary<string, ModelPrice>
    {
        ["claude-opus-5-5"] = new(4m, 20m, 0.2m, 5m),
        ["claude-sonnet-5-5"] = new(2m, 10m, 0.2m, 2.5m),
        ["claude-haiku-4-5"] = new(1m, 5m, 0.1m, 1.25m),
    };

    public static ApiConfig From(IConfiguration env, bool development)
    {
        ArgumentNullException.ThrowIfNull(env);
        string Required(string key, string devFallback) =>
            env[key] is { Length: > 0 } v ? v
            : development ? devFallback
            : throw new InvalidOperationException($"{key} is not set (see .env.example)");

        // SMTP: the MONITOR_SMTP_* key first (the environment file), else the same value from the "Smtp" node
        // (Host, Port, User, Password, From, StartTls) — so a server-only appsettings.Production.json can hold it.
        // An EMPTY key counts as unset and never hides the node (deploy.ps1 writes every monitor.env line, blank ones too).
        string? SmtpValue(string key, string node) =>
            env[key] is { Length: > 0 } v ? v : env[$"{SmtpSection}:{node}"] is { Length: > 0 } n ? n : null;
        string SmtpRequired(string key, string node, string devFallback) =>
            SmtpValue(key, node) ?? (development ? devFallback
                : throw new InvalidOperationException($"{key} (or {SmtpSection}:{node}) is not set (see .env.example)"));

        OAuthClient? Client(string prefix) =>
            env[$"{prefix}_CLIENT_ID"] is { Length: > 0 } id && env[$"{prefix}_CLIENT_SECRET"] is { Length: > 0 } secret
                ? new OAuthClient(id, secret)
                : null;

        // Outside development the site must be https; plain http is accepted only on the machine itself (the local
        // e2e stack), and cookies are Secure exactly when the site is https.
        var origin = Required("MONITOR_PUBLIC_ORIGIN", "http://localhost:5173").TrimEnd('/');
        var loopback = Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && originUri.IsLoopback;
        if (!development && !origin.StartsWith("https://", StringComparison.Ordinal) && !loopback)
        {
            throw new InvalidOperationException("MONITOR_PUBLIC_ORIGIN must be https outside development (see .env.example)");
        }

        var trustProxy = env["MONITOR_TRUST_PROXY"] == "true";
        var networks = (env["MONITOR_PROXY_NETWORKS"] ?? "").Split([',', ' ', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(System.Net.IPNetwork.Parse).ToList();
        if (trustProxy && networks.Count == 0 && !development)
        {
            throw new InvalidOperationException("MONITOR_TRUST_PROXY needs MONITOR_PROXY_NETWORKS: the proxies' address ranges (see .env.example)");
        }

        return new ApiConfig
        {
            DatabaseUrl = Required("MONITOR_DB", "Host=localhost;Port=55432;Database=monitor;Username=monitor;Password=monitor"),
            PublicOrigin = origin,
            Smtp = new SmtpSettings(
                SmtpRequired("MONITOR_SMTP_HOST", "Host", "localhost"),
                int.Parse(SmtpRequired("MONITOR_SMTP_PORT", "Port", "1025"), System.Globalization.CultureInfo.InvariantCulture),
                SmtpValue("MONITOR_SMTP_USER", "User"),
                SmtpValue("MONITOR_SMTP_PASSWORD", "Password"),
                SmtpRequired("MONITOR_SMTP_FROM", "From", "monitor@localhost"),
                string.Equals(SmtpValue("MONITOR_SMTP_TLS", "StartTls") ?? (development ? "false" : "true"), "true",
                    StringComparison.OrdinalIgnoreCase)),
            GitHub = Client("MONITOR_GITHUB"),
            Google = Client("MONITOR_GOOGLE"),
            ArchiveDir = Required("MONITOR_ARCHIVE_DIR", Path.Combine(Path.GetTempPath(), "claude-monitor-archive")),
            WebRoot = env["MONITOR_WEB_ROOT"],
            Commit = env["MONITOR_COMMIT"] is { Length: > 0 } c ? c : "dev",
            MfaKey = Key32(Required("MONITOR_MFA_KEY", "ZGV2ZWxvcG1lbnQtb25seS1tZmEta2V5LTMyLWJ5dGU")),
            MinimumAgentVersion = Version.Parse(env["MONITOR_MIN_AGENT_VERSION"] is { Length: > 0 } m ? m : "0.1.0"),
            SecureCookies = origin.StartsWith("https://", StringComparison.Ordinal),
            BackgroundJobs = env["MONITOR_BACKGROUND_JOBS"] != "off",
            TrustProxy = trustProxy,
            ProxyNetworks = networks,
            AuthRequestsPerMinute = int.TryParse(env["MONITOR_AUTH_RATE_PER_MINUTE"], out var rate) && rate > 0 ? rate : 20,
        };
    }

    private static byte[] Key32(string base64Url)
    {
        var text = base64Url.Replace('-', '+').Replace('_', '/');
        var key = Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
        return key.Length == 32 ? key : throw new InvalidOperationException("MONITOR_MFA_KEY must be 32 bytes (base64url)");
    }

    /// <summary>The price for a model id: the longest configured prefix wins ("claude-haiku-4-5-20251001" -> "claude-haiku-4-5").</summary>
    public ModelPrice? PriceFor(string model) =>
        Prices.Where(p => model.StartsWith(p.Key, StringComparison.Ordinal))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => (ModelPrice?)p.Value)
            .FirstOrDefault();
}

public sealed record SmtpSettings(string Host, int Port, string? User, string? Password, string From, bool StartTls);

public sealed record OAuthClient(string ClientId, string ClientSecret);

/// <summary>The sign-in providers' public endpoints (fixed by the providers, not per deployment).</summary>
public static class ProviderEndpoints
{
    public const string GitHubAuthorize = "https://github.com/login/oauth/authorize";
    public const string GitHubToken = "https://github.com/login/oauth/access_token";
    public const string GitHubUser = "https://api.github.com/user";
    public const string GitHubEmails = "https://api.github.com/user/emails";
    public const string GoogleAuthorize = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleToken = "https://oauth2.googleapis.com/token";
    public const string GoogleUser = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string CallbackPrefix = "/api/auth/callback/";
}

public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
