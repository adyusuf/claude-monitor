using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A person of the fake site. The e-mail is hidden (null) unless the test gives one, as on a real site.</summary>
public sealed class FakePerson(string accountId, string displayName, string? email = null)
{
    public string AccountId { get; } = accountId;
    public string DisplayName { get; set; } = displayName;
    public string? Email { get; set; } = email;
    public string AccountType { get; set; } = "atlassian";
    public bool Active { get; set; } = true;
}

/// <summary>An issue of the fake site, in Jira's words (status category <c>new</c>, <c>indeterminate</c> or <c>done</c>).</summary>
public sealed class FakeIssue(string id, string key, string summary)
{
    public string Id { get; } = id;
    public string Key { get; } = key;
    public string Summary { get; set; } = summary;
    public string? Description { get; set; }
    public string Type { get; set; } = "Story";
    public bool IsSubtask { get; set; }
    public string? ParentKey { get; set; }
    public string? Priority { get; set; } = "Medium";
    public string Status { get; set; } = "To Do";
    public string Category { get; set; } = "new";
    public string? Resolution { get; set; }
    public FakePerson? Assignee { get; set; }
    public FakePerson? Reporter { get; set; }
    public double? Points { get; set; }
    public long? SprintId { get; set; }
    public List<long> PastSprintIds { get; } = [];
    public DateTimeOffset Updated { get; set; }
    public bool Deleted { get; set; }
}

public sealed class FakeSprint(long id, string name, string state)
{
    public long Id { get; } = id;
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    public string Name { get; set; } = name;
    public string State { get; set; } = state;
    public string? Goal { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public long BoardId { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>A space (Jira project) with its board and its issues in rank order.</summary>
public sealed class FakeSpace(FakeSite site, string id, string key, string name)
{
    public string Id { get; } = id;
    public string Key { get; } = key;
    public string Name { get; set; } = name;
    public string? Description { get; set; }
    public FakePerson? Lead { get; set; }
    /// <summary>Null: a space without a board (no Jira Software); the backlog then falls back to a JQL search.</summary>
    public long? BoardId { get; set; }
    /// <summary>When set, the board's backlog answers with this status (a kanban board answers 400).</summary>
    public HttpStatusCode? BoardListStatus { get; set; }
    public bool Deleted { get; set; }
    public List<FakeIssue> Issues { get; } = [];
    public List<FakePerson> People { get; } = [];

    public FakeIssue Issue(string summary, Action<FakeIssue>? configure = null)
    {
        var number = Issues.Count + 1;
        var issue = new FakeIssue(site.Jira.NextId().ToString(CultureInfo.InvariantCulture), $"{Key}-{number}", summary);
        configure?.Invoke(issue);
        site.Jira.Touch(issue);
        Issues.Add(issue);
        return issue;
    }
}

public sealed class FakeWebhook(long id, string url, IReadOnlyList<string> events, string? jql)
{
    public long Id { get; } = id;
    public string Url { get; } = url;
    public IReadOnlyList<string> Events { get; } = events;
    public string? Jql { get; } = jql;
}

/// <summary>One Jira Cloud site: its cloud id, host, spaces, sprints and registered webhooks. A test makes its own, so tests do not meet.</summary>
public sealed class FakeSite
{
    public FakeSite(FakeJira jira, string subdomain)
    {
        Jira = jira;
        Subdomain = subdomain;
    }

    public FakeJira Jira { get; }
    public string Subdomain { get; }
    public string CloudId { get; } = Guid.NewGuid().ToString();
    public string Host => Subdomain + ".atlassian.net";
    public string Name => "Site " + Subdomain;
    public List<FakeSpace> Spaces { get; } = [];
    public ConcurrentDictionary<long, FakeSprint> Sprints { get; } = new();
    public List<FakeWebhook> Webhooks { get; } = [];
    /// <summary>When true, extending a webhook is refused because Jira let it expire: it is gone from the site and the call answers 400.</summary>
    public bool RefreshFails { get; set; }

    public FakeSpace Space(string key, string? name = null, bool board = true)
    {
        var space = new FakeSpace(this, Jira.NextId().ToString(CultureInfo.InvariantCulture), key, name ?? key + " space");
        if (board) space.BoardId = Jira.NextId();
        Spaces.Add(space);
        return space;
    }

    public FakeSprint Sprint(FakeSpace space, string name, string state = "future", string? start = null, string? end = null)
    {
        var sprint = new FakeSprint(Jira.NextId(), name, state) { Start = start, End = end, BoardId = space.BoardId ?? 0 };
        Sprints[sprint.Id] = sprint;
        return sprint;
    }

    public FakeSpace? SpaceOfBoard(long board) => Spaces.FirstOrDefault(s => s.BoardId == board);
}

public sealed class FakeAccount(string id, string displayName, string? email)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public string? Email { get; } = email;
    public List<FakeSite> Sites { get; } = [];
    /// <summary>When true Atlassian refuses every refresh token of this account and every access token it issued (the user revoked the app).</summary>
    public bool Revoked { get; set; }
    public string? CurrentRefreshToken { get; set; }
}

public sealed record FakeRequest(HttpMethod Method, string Host, string Path, string? Bearer, string Body);

/// <summary>
/// A scripted Atlassian: the OAuth token endpoint, the site list and the Jira REST and agile endpoints the integration calls, on an in-memory
/// model. Installed as the primary handler of the API's "atlassian" HTTP client, so no test can reach a real account. Keeps a log of every
/// request and every token it issued, so a test can prove that no secret leaks. The routing is in <see cref="FakeJiraHandler"/>.
/// </summary>
public sealed class FakeJira(Func<DateTimeOffset> now)
{
    public const string ClientId = "jira-client-id";
    public const string ClientSecret = "jira-client-secret-value";
    public const string AuthHost = "auth.atlassian.com";
    public const string ApiHost = "api.atlassian.com";

    private long ids = 10000;
    private long lastStamp;
    private int accounts;
    private readonly ConcurrentDictionary<string, (FakeAccount Account, string Challenge, string Redirect)> codes = new();
    private readonly ConcurrentDictionary<string, FakeAccount> refreshTokens = new();
    private readonly ConcurrentDictionary<string, FakeAccount> accessTokens = new();
    private readonly List<(string Site, string Contains, HttpStatusCode Status, string? RetryAfter, int Left)> failures = [];
    private readonly object gate = new();

    public ConcurrentQueue<FakeRequest> Requests { get; } = new();
    public ConcurrentQueue<string> IssuedSecrets { get; } = new();
    /// <summary>How long the token endpoint takes (a refresh takes time on a real network); used to overlap two callers.</summary>
    public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;
    public int ExpiresInSeconds { get; set; } = 3600;
    public int RefreshCount => Requests.Count(r => r.Path == "/oauth/token" && r.Body.Contains("\"refresh_token\"", StringComparison.Ordinal));

    public HttpMessageHandler Handler() => new FakeJiraHandler(this);

    public long NextId() => Interlocked.Increment(ref ids);

    /// <summary>Gives the issue a new <c>updated</c> value, always later than any given before (Jira's value changes with every edit).</summary>
    public void Touch(FakeIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        lock (gate)
        {
            var stamp = Math.Max(now().ToUnixTimeMilliseconds(), lastStamp + 1);
            lastStamp = stamp;
            issue.Updated = DateTimeOffset.FromUnixTimeMilliseconds(stamp);
        }
    }

    public FakeSite Site(string? subdomain = null) => new(this, subdomain ?? "t-" + Guid.NewGuid().ToString("N")[..10]);

    public FakeAccount Account(string name, string? email, params FakeSite[] sites)
    {
        var account = new FakeAccount($"acc-{Interlocked.Increment(ref accounts)}-{Guid.NewGuid():N}"[..24], name, email);
        account.Sites.AddRange(sites);
        return account;
    }

    /// <summary>An authorization code that the token endpoint exchanges once, for the PKCE challenge and redirect address the sign-in used.</summary>
    public string NewCode(FakeAccount account, string challenge, string redirect)
    {
        ArgumentNullException.ThrowIfNull(account);
        var code = "code-" + Guid.NewGuid().ToString("N");
        codes[code] = (account, challenge, redirect);
        IssuedSecrets.Enqueue(code);
        return code;
    }

    /// <summary>
    /// The next request to this site whose path or query contains the text answers with this status (with a Retry-After for a 429),
    /// <paramref name="times"/> times. Scoped to the site, so a rule a test leaves behind never reaches another test.
    /// </summary>
    public void Fail(FakeSite site, string contains, HttpStatusCode status, int times = 1, string? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        lock (gate) failures.Add(($"/ex/jira/{site.CloudId}", contains, status, retryAfter, times));
    }

    public HttpResponseMessage? TakeFailure(string pathAndQuery)
    {
        lock (gate)
        {
            var at = failures.FindIndex(f => f.Left > 0 && pathAndQuery.StartsWith(f.Site, StringComparison.Ordinal) && pathAndQuery.Contains(f.Contains, StringComparison.Ordinal));
            if (at < 0) return null;
            var f = failures[at];
            failures[at] = f with { Left = f.Left - 1 };
            var response = new HttpResponseMessage(f.Status) { Content = new StringContent("{\"message\":\"secret detail of the failure\"}", Encoding.UTF8, "application/json") };
            if (f.RetryAfter is { } wait) response.Headers.TryAddWithoutValidation("Retry-After", wait);
            return response;
        }
    }

    public int Count(FakeSite site, string pathContains) =>
        Requests.Count(r => r.Path.StartsWith($"/ex/jira/{site.CloudId}", StringComparison.Ordinal) && r.Path.Contains(pathContains, StringComparison.Ordinal));

    /// <summary>A valid access token of the account, as if it had signed in (for tests that call the adapter directly).</summary>
    public string AccessFor(FakeAccount account) => Issue(account).Access!;

    public IssuedTokens Exchange(string code, string verifier, string redirect)
    {
        if (!codes.TryRemove(code, out var entry) || entry.Account.Revoked || entry.Redirect != redirect || Challenge(verifier) != entry.Challenge) return default;
        return Issue(entry.Account);
    }

    public IssuedTokens Refresh(string refresh)
    {
        if (!refreshTokens.TryRemove(refresh, out var account) || account.Revoked) return default;
        return Issue(account);
    }

    public FakeAccount? AccountOf(string? bearer) => bearer is not null && accessTokens.TryGetValue(bearer, out var a) && !a.Revoked ? a : null;

    private IssuedTokens Issue(FakeAccount account)
    {
        var access = "at-" + Guid.NewGuid().ToString("N");
        var refresh = "rt-" + Guid.NewGuid().ToString("N");
        accessTokens[access] = account;
        refreshTokens[refresh] = account;
        account.CurrentRefreshToken = refresh;
        IssuedSecrets.Enqueue(access);
        IssuedSecrets.Enqueue(refresh);
        return new IssuedTokens(access, refresh);
    }

    public static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+0000'", CultureInfo.InvariantCulture);
}

public readonly record struct IssuedTokens(string? Access, string? Refresh)
{
    public bool Ok => Access is not null;
}
