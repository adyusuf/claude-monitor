using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The exact edges of the workspace settings: retention 1..3650 days, event size 1024 bytes..4 MiB (WorkspaceEndpoints.PutSettings).</summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceSettingsBoundsTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> PutAsync(TestUser user, string field, int value) =>
        user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/settings", new Dictionary<string, int> { [field] = value });

    private static async Task<int> SettingAsync(TestUser user, string field) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}")).GetProperty("settings").GetProperty(field).GetInt32();

    [Theory]
    [InlineData("retentionDays", 1)]
    [InlineData("retentionDays", 3650)]
    [InlineData("eventMaxBytes", 1024)]
    [InlineData("eventMaxBytes", 4194304)]
    public async Task A_value_on_the_edge_of_the_range_is_saved(string field, int value)
    {
        var owner = await api.NewClient().SignedUpAsync("bounds-ok");

        var response = await PutAsync(owner, field, value);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(value, await SettingAsync(owner, field));
    }

    [Theory]
    [InlineData("retentionDays", 3651)]
    [InlineData("eventMaxBytes", 1023)]
    [InlineData("eventMaxBytes", 4194305)]
    public async Task A_value_just_outside_the_range_is_refused_as_out_of_range_and_the_setting_is_kept(string field, int value)
    {
        var owner = await api.NewClient().SignedUpAsync("bounds-out");
        var before = await SettingAsync(owner, field);

        var response = await PutAsync(owner, field, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("out_of_range", errors.GetProperty(field)[0].GetString());
        Assert.Equal(before, await SettingAsync(owner, field));
    }
}
