using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-060: the members and invitations lists take an optional limit and cursor. With neither the answer is the plain array it always was, capped;
/// with either it is { items, nextCursor }, in a stable order (a tiebreaker for equal names and equal times), every row once.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceListPagingTests(ApiFactory api)
{
    private async Task AddMembersAsync(TestUser owner, int count, Func<int, string> name)
    {
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        for (var n = 0; n < count; n++)
        {
            var key = $"page-{n}-{Guid.NewGuid():N}@gmail.com";
            var user = new User { Email = key, EmailNormalized = key, DisplayName = name(n), DisplayNameSearch = name(n).ToLowerInvariant(), CreatedAt = now, UpdatedAt = now };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = owner.WorkspaceId, UserId = user.Id, Role = Roles.Member, JoinedAt = now });
        }

        await db.SaveChangesAsync();
    }

    private async Task AddInvitationsAsync(TestUser owner, int count, DateTimeOffset createdAt)
    {
        await using var db = api.Db();
        for (var n = 0; n < count; n++)
        {
            var key = $"inv-{n}-{Guid.NewGuid():N}@gmail.com";
            db.WorkspaceInvitations.Add(new WorkspaceInvitation
            {
                WorkspaceId = owner.WorkspaceId, EmailNormalized = key, Email = key, Role = Roles.Member, TokenHash = Secrets.Hash(Secrets.NewToken()),
                InvitedBy = owner.Id, CreatedAt = createdAt, ExpiresAt = api.Clock.GetUtcNow().AddDays(7),
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Follows nextCursor from the first page; returns every id seen and the number of pages.</summary>
    private static async Task<(List<string> Ids, int Pages)> WalkAsync(TestUser reader, string url, int limit)
    {
        var ids = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await reader.GetJsonAsync($"{url}?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            Assert.Equal(JsonValueKind.Object, page.ValueKind);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.True(items.Count <= limit);
            ids.AddRange(items.Select(i => i.TryGetProperty("userId", out var u) ? u.GetString()! : i.GetProperty("id").GetString()!));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 50);
        return (ids, pages);
    }

    [Fact]
    public async Task Members_page_by_name_with_the_user_id_breaking_ties_and_every_member_comes_once()
    {
        var owner = await api.NewClient().SignedUpAsync("page-mem-owner");
        await AddMembersAsync(owner, 9, n => n % 3 == 0 ? "Same Name" : $"Pager {n:D2}"); // three people share one name
        var url = $"/api/workspaces/{owner.WorkspaceId}/members";
        var all = (await owner.GetJsonAsync(url)).EnumerateArray().Select(m => m.GetProperty("userId").GetString()!).ToList();
        Assert.Equal(10, all.Count);

        var (paged, pages) = await WalkAsync(owner, url, 4);

        Assert.Equal(3, pages);
        Assert.Equal(all, paged); // the same order as the plain list, no row twice, none lost
        Assert.Equal(all.Count, paged.Distinct().Count());
    }

    [Fact]
    public async Task Invitations_page_newest_first_with_the_id_breaking_ties_when_created_at_is_equal()
    {
        var owner = await api.NewClient().SignedUpAsync("page-inv-owner");
        await AddInvitationsAsync(owner, 5, api.Clock.GetUtcNow().AddMinutes(-5)); // five rows with one timestamp
        await AddInvitationsAsync(owner, 2, api.Clock.GetUtcNow().AddMinutes(-1));
        var url = $"/api/workspaces/{owner.WorkspaceId}/invitations";
        var all = (await owner.GetJsonAsync(url)).EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
        Assert.Equal(7, all.Count);

        var (paged, pages) = await WalkAsync(owner, url, 2);

        Assert.Equal(4, pages);
        Assert.Equal(all, paged);
        Assert.Equal(all.Count, paged.Distinct().Count());
    }

    [Fact]
    public async Task With_no_limit_and_no_cursor_the_answer_is_still_the_plain_array_and_a_cursor_alone_starts_a_page()
    {
        var owner = await api.NewClient().SignedUpAsync("page-compat-owner");
        await AddMembersAsync(owner, 3, n => $"Compat {n}");
        var url = $"/api/workspaces/{owner.WorkspaceId}/members";

        Assert.Equal(JsonValueKind.Array, (await owner.GetJsonAsync(url)).ValueKind);
        var first = await owner.GetJsonAsync(url + "?cursor=not-a-cursor");
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.Equal(4, first.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_limit_above_the_page_size_is_clamped_and_a_limit_below_one_gives_one_row()
    {
        var owner = await api.NewClient().SignedUpAsync("page-clamp-owner");
        await AddMembersAsync(owner, 3, n => $"Clamp {n}");
        var url = $"/api/workspaces/{owner.WorkspaceId}/members";

        Assert.Equal(1, (await owner.GetJsonAsync(url + "?limit=0")).GetProperty("items").GetArrayLength());
        Assert.Equal(4, (await owner.GetJsonAsync(url + "?limit=100000")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_plain_lists_stop_at_the_cap()
    {
        var owner = await api.NewClient().SignedUpAsync("page-cap-owner");
        await AddMembersAsync(owner, WorkspaceMemberList.ListMax + 2, n => $"Cap {n:D4}");
        await AddInvitationsAsync(owner, WorkspaceMemberList.ListMax + 2, api.Clock.GetUtcNow().AddMinutes(-2));

        Assert.Equal(WorkspaceMemberList.ListMax, (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/members")).GetArrayLength());
        Assert.Equal(WorkspaceMemberList.ListMax, (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations")).GetArrayLength());
    }

    [Fact]
    public async Task Paging_changes_nothing_about_who_may_read_a_list()
    {
        var owner = await api.NewClient().SignedUpAsync("page-deny-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "page-deny-member");
        var stranger = await api.NewClient().SignedUpAsync("page-deny-stranger");
        var invitations = $"/api/workspaces/{owner.WorkspaceId}/invitations?limit=2";
        var members = $"/api/workspaces/{owner.WorkspaceId}/members?limit=2";

        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync(invitations)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync(invitations)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync(members)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Http.GetAsync(members)).StatusCode);
    }
}
