using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>Test addresses (global #30): one helper; the base is overridable with E2E_EMAIL_BASE.</summary>
public static class Emails
{
    public static string New(string tag)
    {
        var configured = Environment.GetEnvironmentVariable("E2E_EMAIL_BASE");
        var baseAddress = string.IsNullOrEmpty(configured) ? "monitor.e2e@gmail.com" : configured;
        var at = baseAddress.IndexOf('@', StringComparison.Ordinal);
        return $"{baseAddress[..at]}+{tag}-{Guid.NewGuid().ToString("N")[..8]}{baseAddress[at..]}";
    }
}

/// <summary>A browser-like client: keeps cookies, sends the CSRF header, and knows its own account.</summary>
public sealed class TestUser(ApiFactory factory, HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; } = http;
    public ApiFactory Factory { get; } = factory;
    public string Email { get; private set; } = "";
    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public const string Password = "correct horse battery";

    /// <summary>Registers, verifies through the mailed link, signs in and reads /me.</summary>
    public async Task<TestUser> SignedUpAsync(string tag = "user", string name = "Test Person")
    {
        Email = Emails.New(tag);
        (await PostAsync("/api/auth/register", new { email = Email, password = Password, displayName = name })).EnsureSuccessStatusCode();
        (await PostAsync("/api/auth/verify-email", new { token = Factory.Mail.TokenFor(Email) })).EnsureSuccessStatusCode();
        (await PostAsync("/api/auth/login", new { email = Email, password = Password })).EnsureSuccessStatusCode();
        var me = await GetJsonAsync("/api/me");
        Id = me.GetProperty("id").GetGuid();
        WorkspaceId = me.GetProperty("workspaces")[0].GetProperty("id").GetGuid();
        return this;
    }

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) =>
        SendAsync(HttpMethod.Post, url, body, csrf);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (csrf) request.Headers.Add("X-CSRF", "1");
        return await Http.SendAsync(request);
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Connects an agent for this user in the given workspace through the device flow.</summary>
    public async Task<TestAgent> ConnectAgentAsync(Guid? workspaceId = null, string machineKey = "")
    {
        var agentHttp = Factory.CreateClient();
        var key = machineKey.Length > 0 ? machineKey : "machine-" + Guid.NewGuid().ToString("N");
        var code = await (await agentHttp.PostAsJsonAsync("/api/device/code",
                new DeviceCodeRequest(key, "laptop-1", OsKinds.MacOs, "arm64", "0.3.0"), Json))
            .Content.ReadFromJsonAsync<DeviceCodeResponse>(Json);
        (await PostAsync("/api/device/approve", new { userCode = code!.UserCode, workspaceId = workspaceId ?? WorkspaceId })).EnsureSuccessStatusCode();
        var tokenResponse = await agentHttp.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(code.DeviceCode), Json);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(Json);
        return new TestAgent(Factory, tokens!, key);
    }
}

/// <summary>An agent's HTTP client with its bearer token and version header.</summary>
public sealed class TestAgent
{
    public TestAgent(ApiFactory factory, TokenResponse tokens, string machineKey)
    {
        Tokens = tokens;
        MachineKey = machineKey;
        Http = factory.CreateClient();
        Http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.3.0");
    }

    public TokenResponse Tokens { get; }
    public string MachineKey { get; }
    public HttpClient Http { get; }
    private long seq;

    public async Task<HttpResponseMessage> SendAsync(params CapturedEvent[] events) =>
        await Http.PostAsJsonAsync("/api/agent/batches", new EventBatch(Interlocked.Increment(ref seq), events), TestUser.Json);

    public static CapturedEvent Hook(string session, string hook, object payload, DateTimeOffset at, string? project = "repo-key") =>
        new(HarnessKinds.ClaudeCode, session, EventKinds.Hook(hook), at, JsonSerializer.SerializeToElement(payload, TestUser.Json),
            ProjectKey: project, ProjectName: project == "repo-key" ? "Repo Name" : project, GitBranch: "main");

    public static CapturedEvent Of(string session, string kind, object payload, DateTimeOffset at) =>
        new(HarnessKinds.ClaudeCode, session, kind, at, JsonSerializer.SerializeToElement(payload, TestUser.Json));
}
