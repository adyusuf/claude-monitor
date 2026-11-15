using Podium.Agent.Auth;
using Podium.Agent.Net;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// The refresh token is stored before the access token. The server has already replaced the old refresh token, so a
/// stop between the two writes must leave the new one: an old access token only costs one more refresh, an old refresh
/// token would be judged a reuse and revoke the agent.
/// </summary>
public sealed class TokenSaveOrderTests
{
    [Fact]
    public void A_failure_while_storing_the_access_token_still_leaves_the_new_refresh_token()
    {
        var store = new FailingAccessWrites();
        store.Items[Credentials.Access] = "old-access";
        store.Items[Credentials.Refresh] = "old-refresh";
        using var http = ApiClient.CreateHttp("https://m.invalid", new FakeApi());
        using var api = new ApiClient(http, store, TimeSpan.FromSeconds(30));
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<IOException>(() => api.SaveTokens(new TokenResponse("new-access", "new-refresh", now.AddMinutes(30), now.AddDays(30), Guid.NewGuid(), Guid.NewGuid())));

        Assert.Equal("new-refresh", store.Items[Credentials.Refresh]);
    }

    private sealed class FailingAccessWrites : ICredentialStore
    {
        public Dictionary<string, string> Items { get; } = [];

        public string? Read(string account) => Items.GetValueOrDefault(account);

        public void Write(string account, string secret)
        {
            if (account == Credentials.Access) throw new IOException("the store went away");
            Items[account] = secret;
        }

        public void Delete(string account) => Items.Remove(account);
    }
}
