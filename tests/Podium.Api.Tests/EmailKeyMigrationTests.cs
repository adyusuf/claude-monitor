using System.Net;
using Podium.Api.Tests.Infrastructure;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-180: <c>users.email_key</c> is the e-mail identity key (trimmed, lower-cased, not folded). The migration fills it from the stored address
/// with the same mapping as <see cref="EmailIdentity.Key"/>, and refuses, changing nothing, when two accounts would share one key.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class EmailKeyMigrationTests(ApiFactory api)
{
    private const string Before = "20261109130258_SignedExecLevel";
    private const string Migration = "20261116065021_EmailKey";

    [Fact]
    public async Task The_key_is_filled_from_the_stored_address_as_the_api_computes_it_and_the_unique_index_is_made()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        const string accented = "Ömer.Işık+İz@Example-Mail.COM";
        var plain = await m.SeedUserAsync("Plain.User@Mail.COM", "plain.user@mail.com");
        var unicode = await m.SeedUserAsync(accented, SearchText.Email(accented));
        var deleted = Guid.NewGuid();
        await m.ExecAsync($"""
            INSERT INTO users (id, display_name, display_name_search, status, created_at, updated_at, failed_sign_ins)
            VALUES ('{deleted}', 'Deleted', 'deleted', 'deleted', now(), now(), 0)
            """);

        await m.MigrateAsync(Migration);

        Assert.Equal("plain.user@mail.com", await m.ScalarAsync($"SELECT email_key FROM users WHERE id = '{plain}'"));
        Assert.Equal(EmailIdentity.Key(accented), await m.ScalarAsync($"SELECT email_key FROM users WHERE id = '{unicode}'"));
        Assert.Equal("ömer.işık+iz@example-mail.com", EmailIdentity.Key(accented));
        Assert.NotEqual(SearchText.Email(accented), EmailIdentity.Key(accented));
        Assert.Null(await m.ScalarAsync($"SELECT email_key FROM users WHERE id = '{deleted}'"));
        Assert.Contains("CREATE UNIQUE INDEX ix_users_email_key ON public.users USING btree (email_key)", await m.IndexAsync("ix_users_email_key"));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.ColumnCountAsync("users", "email_key"));
        Assert.Null(await m.IndexAsync("ix_users_email_key"));
    }

    [Fact]
    public async Task A_new_account_gets_its_key_and_a_deleted_account_gives_it_up()
    {
        var user = await api.NewClient().SignedUpAsync("email-key");
        await using (var db = api.Db())
        {
            Assert.Equal(EmailIdentity.Key(user.Email), (await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).EmailKey);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        await using var after = api.Db();
        Assert.Null((await after.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id)).EmailKey);
    }

    [Fact]
    public async Task Two_accounts_on_one_key_stop_the_migration_before_it_changes_anything()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);
        // Only inconsistent data can do this (the folded address is unique and folds more than lower-casing): seeded by hand.
        await m.SeedUserAsync("Same.Box@Mail.com", "same.box@mail.com");
        await m.SeedUserAsync("same.box@mail.com", "same.box@mail.com-other");
        var rows = await m.RowsAsync("users");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => m.MigrateAsync(Migration));

        Assert.Contains("users: 1 e-mail identity keys are shared by more than one account", error.Message, StringComparison.Ordinal);
        Assert.Equal(rows, await m.RowsAsync("users"));
        Assert.Equal(0, await m.ColumnCountAsync("users", "email_key"));
        Assert.False(await m.AppliedAsync(Migration));
    }
}
