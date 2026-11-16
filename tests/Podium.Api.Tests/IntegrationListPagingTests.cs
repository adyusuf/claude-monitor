using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-077: the space list (at most 500) and the people list (at most 1000) say when they were cut, with an <c>X-Truncated: true</c> header, and
/// take optional <c>skip</c>/<c>take</c>; without them the answer is what it was.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationListPagingTests(ApiFactory api)
{
    private static bool Truncated(HttpResponseMessage response) =>
        response.Headers.TryGetValues(IntegrationImportEndpoints.TruncatedHeader, out var values) && values.Single() == "true";

    /// <summary>The names in order, joined by commas (a tuple compares a list by reference).</summary>
    private static async Task<string> JoinedAsync(HttpResponseMessage response, string property) => string.Join(",", await NamesAsync(response, property));

    private static async Task<List<string>> NamesAsync(HttpResponseMessage response, string property)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.EnumerateArray().Select(e => e.GetProperty(property).GetString()!)];
    }

    [Fact]
    public async Task A_site_with_one_space_more_than_the_cap_lists_the_cap_and_says_it_was_cut_and_one_with_exactly_the_cap_does_not()
    {
        var over = await JiraKit.ConnectedAsync(api, site =>
        {
            for (var i = 0; i <= IntegrationImportEndpoints.SpacesMax; i++) site.Space($"O{i:D3}");
        });
        var exact = await JiraKit.ConnectedAsync(api, site =>
        {
            for (var i = 0; i < IntegrationImportEndpoints.SpacesMax; i++) site.Space($"E{i:D3}");
        });

        var cut = await over.Admin.Http.GetAsync($"{over.Base}/spaces");
        var whole = await exact.Admin.Http.GetAsync($"{exact.Base}/spaces");

        Assert.Equal((IntegrationImportEndpoints.SpacesMax, true), ((await NamesAsync(cut, "key")).Count, Truncated(cut)));
        Assert.Equal((IntegrationImportEndpoints.SpacesMax, false), ((await NamesAsync(whole, "key")).Count, Truncated(whole)));
        var rest = await over.Admin.Http.GetAsync($"{over.Base}/spaces?skip={IntegrationImportEndpoints.SpacesMax}");
        Assert.Equal(($"O{IntegrationImportEndpoints.SpacesMax:D3}", false), (await JoinedAsync(rest, "key"), Truncated(rest)));
    }

    [Fact]
    public async Task Spaces_page_with_skip_and_take_and_a_take_over_the_cap_is_the_cap()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            foreach (var key in new[] { "PGA", "PGB", "PGC" }) site.Space(key);
        });

        var first = await w.Admin.Http.GetAsync($"{w.Base}/spaces?take=2");
        var second = await w.Admin.Http.GetAsync($"{w.Base}/spaces?skip=2&take=2");
        var all = await w.Admin.Http.GetAsync($"{w.Base}/spaces?take={IntegrationImportEndpoints.SpacesMax + 1}");
        var plain = await w.Admin.Http.GetAsync($"{w.Base}/spaces");

        Assert.Equal(("PGA,PGB", true), (await JoinedAsync(first, "key"), Truncated(first)));
        Assert.Equal(("PGC", false), (await JoinedAsync(second, "key"), Truncated(second)));
        Assert.Equal((3, false), ((await NamesAsync(all, "key")).Count, Truncated(all)));
        Assert.Equal((3, false), ((await NamesAsync(plain, "key")).Count, Truncated(plain)));
    }

    [Theory]
    [InlineData("spaces", "skip=-1", "skip")]
    [InlineData("spaces", "take=0", "take")]
    [InlineData("people", "skip=-1", "skip")]
    [InlineData("people", "take=0", "take")]
    public async Task A_negative_skip_or_a_take_under_one_is_refused(string list, string query, string field)
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("PGX"));

        Assert.Equal((field, IntegrationImportEndpoints.InvalidPaging), await ProjectKit.ProblemAsync(await w.Admin.Http.GetAsync($"{w.Base}/{list}?{query}")));
    }

    [Fact]
    public async Task People_past_the_cap_are_cut_with_the_header_and_page_with_skip_and_take_in_name_order()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await using (var db = api.Db())
        {
            var now = api.Clock.GetUtcNow().AddMinutes(-5);
            db.IntegrationPeople.AddRange(Enumerable.Range(0, IntegrationImportEndpoints.PeopleMax + 1).Select(i => new IntegrationPerson
            {
                ConnectionId = w.ConnectionId,
                ExternalAccountId = $"pg-{i:D4}",
                DisplayName = $"Person {i:D4}",
                FirstSeenAt = now,
                LastSeenAt = now,
                Active = true,
            }));
            await db.SaveChangesAsync();
        }

        var cut = await w.Admin.Http.GetAsync($"{w.Base}/people");
        var rest = await w.Admin.Http.GetAsync($"{w.Base}/people?skip={IntegrationImportEndpoints.PeopleMax}");
        var page = await w.Admin.Http.GetAsync($"{w.Base}/people?skip=1&take=2");

        var names = await NamesAsync(cut, "displayName");
        Assert.Equal((IntegrationImportEndpoints.PeopleMax, true, "Person 0000"), (names.Count, Truncated(cut), names[0]));
        Assert.Equal(($"Person {IntegrationImportEndpoints.PeopleMax:D4}", false), (await JoinedAsync(rest, "displayName"), Truncated(rest)));
        Assert.Equal(("Person 0001,Person 0002", true), (await JoinedAsync(page, "displayName"), Truncated(page)));
    }
}
