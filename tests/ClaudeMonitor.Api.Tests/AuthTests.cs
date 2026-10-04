using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class AuthTests(ApiFactory api)
{
    [Fact]
    public async Task Sign_up_verify_sign_in_and_read_me()
    {
        var user = await api.NewClient().SignedUpAsync("signup", "Örnek Şişman");
        var me = await user.GetJsonAsync("/api/me");
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
        Assert.Equal("Örnek Şişman", me.GetProperty("displayName").GetString());
        Assert.True(me.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("owner", me.GetProperty("workspaces")[0].GetProperty("role").GetString());
        Assert.Equal(0, me.GetProperty("providers").GetArrayLength());
    }

    [Fact]
    public async Task Me_without_a_session_is_unauthorised()
    {
        var response = await api.NewClient().Http.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_second_registration_of_an_address_looks_the_same_and_sends_nothing()
    {
        var user = await api.NewClient().SignedUpAsync("dup");
        var before = api.Mail.Sent.Count;
        var again = await api.NewClient().PostAsync("/api/auth/register",
            new { email = user.Email.ToUpperInvariant(), password = TestUser.Password, displayName = "Other" });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(before, api.Mail.Sent.Count);
    }

    [Theory]
    [InlineData("not-an-email", "long enough password", "Name", "email")]
    [InlineData("valid@gmail.com", "short", "Name", "password")]
    [InlineData("valid@gmail.com", "long enough password", "", "displayName")]
    public async Task Registration_validates_every_field(string email, string password, string name, string field)
    {
        var response = await api.NewClient().PostAsync("/api/auth/register", new { email, password, displayName = name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task An_unverified_account_cannot_sign_in_and_a_wrong_password_is_refused()
    {
        var client = api.NewClient();
        var email = Emails.New("unverified");
        await client.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "U" });
        var unverified = await client.PostAsync("/api/auth/login", new { email, password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        var wrong = await client.PostAsync("/api/auth/login", new { email, password = "not the password!" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var unknown = await client.PostAsync("/api/auth/login", new { email = Emails.New("nobody"), password = TestUser.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task A_verification_link_works_once()
    {
        var client = api.NewClient();
        var email = Emails.New("once");
        await client.PostAsync("/api/auth/register", new { email, password = TestUser.Password, displayName = "O" });
        var token = api.Mail.TokenFor(email);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/verify-email", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/verify-email", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/auth/verify-email", new { token = "" })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_server_side()
    {
        var user = await api.NewClient().SignedUpAsync("logout");
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Password_reset_changes_the_password_and_signs_out_everywhere()
    {
        var user = await api.NewClient().SignedUpAsync("reset");
        var other = api.NewClient();
        Assert.Equal(HttpStatusCode.Accepted, (await other.PostAsync("/api/auth/password/forgot", new { email = user.Email })).StatusCode);
        var token = api.Mail.TokenFor(user.Email);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await other.PostAsync("/api/auth/password/reset", new { token, password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await other.PostAsync("/api/auth/password/reset", new { token, password = "a brand new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await other.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await other.PostAsync("/api/auth/login", new { email = user.Email, password = "a brand new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await other.PostAsync("/api/auth/password/reset", new { token, password = "another new password" })).StatusCode);
    }

    [Fact]
    public async Task Forgot_for_an_unknown_address_looks_the_same()
    {
        var before = api.Mail.Sent.Count;
        var response = await api.NewClient().PostAsync("/api/auth/password/forgot", new { email = Emails.New("ghost") });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(before, api.Mail.Sent.Count);
    }

    [Fact]
    public async Task A_cookie_call_without_the_csrf_header_or_from_another_origin_is_refused()
    {
        var user = await api.NewClient().SignedUpAsync("csrf");
        var noHeader = await user.PostAsync("/api/workspaces", new { name = "X" }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workspaces") { Content = JsonContent.Create(new { name = "X" }) };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add("Origin", "https://evil.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers_and_version()
    {
        var response = await api.NewClient().Http.GetAsync("/api/version");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.Equal("require-corp", response.Headers.GetValues("Cross-Origin-Embedder-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("test-sha", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commit").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.NewClient().Http.GetAsync("/api/no-such-thing")).StatusCode);
    }

    [Fact]
    public async Task An_expired_session_is_not_accepted()
    {
        var user = await api.NewClient().SignedUpAsync("expiry");
        api.Clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_web_app_is_served_on_the_same_origin_and_never_answers_for_the_api()
    {
        var http = api.NewClient().Http;
        var script = await http.GetAsync("/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        var page = await http.GetAsync("/w/123/sessions");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>app</title>", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(page.Headers.CacheControl!.NoCache);
        Assert.Equal("<!doctype html><title>app</title>", await http.GetStringAsync("/"));
        var api_ = await http.GetAsync("/api/nothing-here");
        Assert.Equal(HttpStatusCode.NotFound, api_.StatusCode);
        Assert.DoesNotContain("<title>", await api_.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<(HttpStatusCode Status, string? Title)> Answer(HttpResponseMessage response) =>
        (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

    private static async Task LockAsync(TestUser client, string email)
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsync("/api/auth/login", new { email, password = "wrong password " + i })).StatusCode);
        }
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_for_a_while_even_against_the_right_one()
    {
        var user = await api.NewClient().SignedUpAsync("lockout");
        var client = api.NewClient();
        await LockAsync(client, user.Email);

        // Locked: the right password is refused with the very answer a wrong one gets (no 429 that would reveal the lock).
        var locked = await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password });
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_credentials"), await Answer(locked));
        DateTimeOffset? lockedUntil;
        await using (var db = api.Db())
        {
            var row = db.Users.Single(u => u.Id == user.Id);
            lockedUntil = row.LockedUntil;
            Assert.NotNull(lockedUntil);
            Assert.Equal(0, row.FailedSignIns); // the lock itself resets the count; a failure during it would make it 1
        }

        // Attempts during the lock neither count as failures nor push the end of the lock further away.
        api.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_credentials"),
            await Answer(await client.PostAsync("/api/auth/login", new { email = user.Email, password = "wrong during lock" })));
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_credentials"),
            await Answer(await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })));
        await using (var db = api.Db())
        {
            var row = db.Users.Single(u => u.Id == user.Id);
            Assert.Equal(lockedUntil, row.LockedUntil);
            Assert.Equal(0, row.FailedSignIns);
            Assert.Equal(1, db.AuditEvents.Count(a => a.ActorUserId == user.Id && a.Action == "user.account_locked"));
        }

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        await using var check = api.Db();
        Assert.True(check.AuditEvents.Any(a => a.ActorUserId == user.Id && a.Action == "user.account_locked"));
        Assert.Equal(0, check.Users.Single(u => u.Id == user.Id).FailedSignIns);
        // The attempts during the lock are still on record, marked as such.
        var failures = check.AuditEvents.Where(a => a.ActorUserId == user.Id && a.Action == "user.sign_in_failed").AsEnumerable().ToList();
        Assert.Equal(8, failures.Count); // 5 that locked + 3 during the lock
        Assert.Equal(3, failures.Count(a => a.Detail is { } d && d.RootElement.TryGetProperty("locked", out var flag) && flag.GetBoolean()));
    }

    [Fact]
    public async Task A_locked_account_an_unknown_address_and_a_wrong_password_get_the_same_answer()
    {
        var client = api.NewClient();
        var wrongOne = await api.NewClient().SignedUpAsync("same-wrong");
        var lockedOne = await api.NewClient().SignedUpAsync("same-locked");
        await LockAsync(client, lockedOne.Email);

        var wrong = await Answer(await client.PostAsync("/api/auth/login", new { email = wrongOne.Email, password = "not the password!" }));
        var locked = await Answer(await client.PostAsync("/api/auth/login", new { email = lockedOne.Email, password = TestUser.Password }));
        var unknown = await Answer(await client.PostAsync("/api/auth/login", new { email = Emails.New("same-nobody"), password = TestUser.Password }));
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_credentials"), wrong);
        Assert.Equal(wrong, locked);
        Assert.Equal(wrong, unknown);
        // A missing password is the same refusal too, not a different error.
        Assert.Equal(wrong, await Answer(await client.PostAsync("/api/auth/login", new { email = wrongOne.Email })));
    }

    [Fact]
    public async Task A_password_reset_opens_a_lock_so_the_owner_signs_in_at_once()
    {
        var user = await api.NewClient().SignedUpAsync("lock-reset");
        var client = api.NewClient();
        await LockAsync(client, user.Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        await using (var db = api.Db())
        {
            Assert.NotNull(db.Users.Single(u => u.Id == user.Id).LockedUntil);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/password/forgot", new { email = user.Email })).StatusCode);
        var token = api.Mail.TokenFor(user.Email);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/auth/password/reset", new { token, password = "a fresh password after the lock" })).StatusCode);
        await using (var db = api.Db())
        {
            var row = db.Users.Single(u => u.Id == user.Id);
            Assert.Equal(0, row.FailedSignIns);
            Assert.Null(row.LockedUntil);
        }

        // No clock advance: the lock did not simply run out.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync("/api/auth/login", new { email = user.Email, password = "a fresh password after the lock" })).StatusCode);
    }

    [Fact]
    public async Task A_good_sign_in_resets_the_failure_count()
    {
        var user = await api.NewClient().SignedUpAsync("lockout-reset");
        var client = api.NewClient();
        for (var i = 0; i < 4; i++) await client.PostAsync("/api/auth/login", new { email = user.Email, password = "nope nope " + i });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/auth/login", new { email = user.Email, password = "nope again!" })).StatusCode);
        await using var db = api.Db();
        Assert.Equal(1, db.Users.Single(u => u.Id == user.Id).FailedSignIns);
    }
}
