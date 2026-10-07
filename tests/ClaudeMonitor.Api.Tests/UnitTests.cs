using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Text;
using Microsoft.Extensions.Configuration;

namespace ClaudeMonitor.Api.Tests;

public sealed class UnitTests
{
    [Theory]
    [InlineData("Şişman", "sisman")]
    [InlineData("İSTANBUL", "istanbul")]
    [InlineData("ıslak IŞIK", "islak isik")]
    [InlineData("  Çağrı Öz ", "cagri oz")]
    [InlineData("Ünal", "unal")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Search_text_is_case_and_accent_insensitive(string? input, string expected) =>
        Assert.Equal(expected, SearchText.Normalize(input));

    [Fact]
    public void A_contains_pattern_escapes_the_users_wildcards() =>
        Assert.Equal("%50\\% off\\_now\\\\%", SearchText.ContainsPattern("50% off_now\\"));

    [Fact]
    public void Tokens_are_random_and_hashes_stable()
    {
        Assert.NotEqual(Secrets.NewToken(), Secrets.NewToken());
        Assert.Equal(Secrets.Hash("x"), Secrets.Hash("x"));
        Assert.Equal(64, Secrets.Hash("x").Length);
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$", Secrets.NewUserCode());
        Assert.Equal("BCDF-GHJK", Secrets.NormalizeUserCode("bcdf ghjk"));
        Assert.Equal("ABC", Secrets.NormalizeUserCode("abc"));
        Assert.Equal("", Secrets.NormalizeUserCode(null));
        Assert.Null(Secrets.IpTag(null));
        Assert.Equal(16, Secrets.IpTag(System.Net.IPAddress.Loopback)!.Length);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("short", false)]
    [InlineData("          ", false)]
    [InlineData("long enough", true)]
    public void Password_policy(string? password, bool ok) => Assert.Equal(ok, Secrets.PasswordAcceptable(password));

    [Fact]
    public void Passwords_verify_only_against_their_own_hash()
    {
        var user = new User();
        Assert.False(Secrets.VerifyPassword(user, "anything"));
        user.PasswordHash = Secrets.HashPassword(user, "the password");
        Assert.True(Secrets.VerifyPassword(user, "the password"));
        Assert.False(Secrets.VerifyPassword(user, "the passwore"));
    }

    [Fact]
    public void The_dummy_aware_check_answers_like_the_plain_one()
    {
        var user = new User();
        Assert.False(Secrets.VerifyPasswordOrDummy(null, "anything")); // no such user
        Assert.False(Secrets.VerifyPasswordOrDummy(user, "anything")); // a user without a password (provider-only)
        user.PasswordHash = Secrets.HashPassword(user, "the password");
        Assert.True(Secrets.VerifyPasswordOrDummy(user, "the password"));
        Assert.False(Secrets.VerifyPasswordOrDummy(user, "the passwore"));
        Assert.False(Secrets.VerifyPasswordOrDummy(user, null));
        Assert.False(Secrets.VerifyPasswordOrDummy(null, null));
    }

    [Fact]
    public void An_unknown_address_costs_about_as_much_as_a_wrong_password()
    {
        var user = new User();
        user.PasswordHash = Secrets.HashPassword(user, "the password");
        // The fastest of several samples, not a median: CPU contention (other tests running in parallel) only ever ADDS time, so the
        // minimum is the truest cost of the work itself, where a median of a few samples can still be inflated by a busy machine.
        static TimeSpan Fastest(Func<bool> verify)
        {
            verify(); // warm up: the first call pays for JIT and for the dummy hash being made
            var fastest = TimeSpan.MaxValue;
            for (var i = 0; i < 9; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                Assert.False(verify());
                var took = System.Diagnostics.Stopwatch.GetElapsedTime(start);
                if (took < fastest) fastest = took;
            }

            return fastest;
        }

        var known = Fastest(() => Secrets.VerifyPasswordOrDummy(user, "not the password"));
        var unknown = Fastest(() => Secrets.VerifyPasswordOrDummy(null, "not the password"));
        var noPassword = Fastest(() => Secrets.VerifyPasswordOrDummy(new User(), "not the password"));
        // Without the dummy verification an unknown address would return in microseconds; the real work factor is
        // milliseconds. The margin is wide (a quarter) so a busy machine does not make this flaky.
        Assert.True(unknown >= known * 0.25, $"unknown {unknown.TotalMilliseconds:F2} ms vs known {known.TotalMilliseconds:F2} ms");
        Assert.True(noPassword >= known * 0.25, $"no password {noPassword.TotalMilliseconds:F2} ms vs known {known.TotalMilliseconds:F2} ms");
    }

    [Theory]
    [InlineData("EmailNormalized", "email_normalized")]
    [InlineData("Id", "id")]
    [InlineData("FK_session_events", "fk_session_events")]
    [InlineData("Cache2Write", "cache2_write")]
    public void Snake_case(string input, string expected) => Assert.Equal(expected, MonitorDb.SnakeCase(input));

    [Fact]
    public void Cursors_round_trip_and_reject_garbage()
    {
        var at = new DateTimeOffset(2026, 10, 3, 1, 2, 3, TimeSpan.Zero);
        var id = Guid.CreateVersion7();
        Assert.Equal((at, id), Cursor.Parse(Cursor.Of(at, id)));
        Assert.Null(Cursor.Parse(null));
        Assert.Null(Cursor.Parse("1.2.3"));
        Assert.Null(Cursor.Parse("x.y"));
        Assert.Null(Cursor.Parse($"{long.MaxValue}.{id:N}"));
    }

    [Fact]
    public void Roles_rank_and_unknown_ranks_lowest()
    {
        Assert.True(Roles.Rank(Roles.Owner) > Roles.Rank(Roles.Admin));
        Assert.True(Roles.Rank(Roles.Member) > Roles.Rank(Roles.Viewer));
        Assert.Equal(0, Roles.Rank("root"));
    }

    [Fact]
    public void Configuration_outside_development_requires_its_variables()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => ApiConfig.From(empty, development: false));
        var dev = ApiConfig.From(empty, development: true);
        Assert.Equal("http://localhost:5173", dev.PublicOrigin);
        Assert.Null(dev.GitHub);
        Assert.False(dev.SecureCookies);

        var prod = ApiConfig.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITOR_DB"] = "Host=db",
            ["MONITOR_PUBLIC_ORIGIN"] = "https://monitor.invalid/",
            ["MONITOR_SMTP_HOST"] = "smtp",
            ["MONITOR_SMTP_PORT"] = "587",
            ["MONITOR_SMTP_FROM"] = "a@b.invalid",
            ["MONITOR_ARCHIVE_DIR"] = "/var/archive",
            ["MONITOR_GOOGLE_CLIENT_ID"] = "id",
            ["MONITOR_GOOGLE_CLIENT_SECRET"] = "secret",
            ["MONITOR_TRUST_PROXY"] = "true",
            ["MONITOR_PROXY_NETWORKS"] = "173.245.48.0/20, 2400:cb00::/32",
            ["MONITOR_BACKGROUND_JOBS"] = "off",
            ["MONITOR_AUTH_RATE_PER_MINUTE"] = "5",
            ["MONITOR_MFA_KEY"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        }).Build(), development: false);
        Assert.Equal("https://monitor.invalid", prod.PublicOrigin);
        Assert.True(prod.Smtp.StartTls);
        Assert.True(prod.SecureCookies);
        Assert.True(prod.TrustProxy);
        Assert.Equal(["173.245.48.0/20", "2400:cb00::/32"], prod.ProxyNetworks.Select(n => n.ToString()));
        Assert.False(prod.BackgroundJobs);
        Assert.Equal(5, prod.AuthRequestsPerMinute);
        Assert.Equal("id", prod.Google!.ClientId);

        IConfiguration With(string origin, string mfaKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MONITOR_DB"] = "Host=db",
            ["MONITOR_PUBLIC_ORIGIN"] = origin,
            ["MONITOR_SMTP_HOST"] = "smtp",
            ["MONITOR_SMTP_PORT"] = "587",
            ["MONITOR_SMTP_FROM"] = "a@b.invalid",
            ["MONITOR_ARCHIVE_DIR"] = "/var/archive",
            ["MONITOR_MFA_KEY"] = mfaKey,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ApiConfig.From(With("http://monitor.invalid"), development: false));
        Assert.False(ApiConfig.From(With("http://localhost:5190"), development: false).SecureCookies);
        Assert.Throws<InvalidOperationException>(() => ApiConfig.From(With("https://m.invalid", "c2hvcnQ"), development: false));
        var trustingAnyone = new ConfigurationBuilder().AddInMemoryCollection(With("https://m.invalid").AsEnumerable()
            .Append(new KeyValuePair<string, string?>("MONITOR_TRUST_PROXY", "true"))).Build();
        Assert.Throws<InvalidOperationException>(() => ApiConfig.From(trustingAnyone, development: false));
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", 1)]
    [InlineData("claude-opus-5-5", 4)]
    public void Prices_match_the_longest_prefix(string model, decimal input) =>
        Assert.Equal(input, ApiConfig.From(new ConfigurationBuilder().Build(), true).PriceFor(model)!.Input);

    [Fact]
    public void An_unknown_model_has_no_price() =>
        Assert.Null(ApiConfig.From(new ConfigurationBuilder().Build(), true).PriceFor("gpt-x"));

    [Fact]
    public async Task The_broker_delivers_to_subscribers_of_a_topic_only()
    {
        var broker = new Broker();
        broker.Publish("nobody", new StreamMessage("x", new { }));
        using (var a = broker.Subscribe("t"))
        {
            using var b = broker.Subscribe("other");
            Assert.Equal(1, broker.SubscriberCount("t"));
            broker.Publish("t", new StreamMessage("hello", 1));
            Assert.Equal("hello", (await a.Reader.ReadAsync()).Event);
            Assert.False(b.Reader.TryRead(out _));
        }

        Assert.Equal(0, broker.SubscriberCount("t"));
        Assert.Equal(0, broker.SubscriberCount("never"));
    }

    [Fact]
    public void Http_helpers_validate_and_clamp()
    {
        Assert.True(Http.IsEmail("a+b@gmail.com"));
        Assert.False(Http.IsEmail("Name <a@b.com>"));
        Assert.False(Http.IsEmail(new string('a', 250) + "@b.com"));
        Assert.False(Http.IsName(new string('a', 101)));
        var config = ApiConfig.From(new ConfigurationBuilder().Build(), true);
        Assert.Equal(25, Http.Limit(null, config));
        Assert.Equal(1, Http.Limit(-5, config));
        Assert.Equal(100, Http.Limit(1000, config));
    }
}
