using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Streaming;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-254: a member holds at most <see cref="StreamLimiter.MaxPerMember"/> workspace streams at once; the next is 429 too_many_streams, and a closed stream gives its place back.</summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceStreamCapTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> OpenAsync(TestUser user) =>
        user.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/workspaces/{user.WorkspaceId}/stream"), HttpCompletionOption.ResponseHeadersRead);

    private static async Task<List<HttpResponseMessage>> FillAsync(TestUser user)
    {
        var streams = new List<HttpResponseMessage>();
        for (var n = 0; n < StreamLimiter.MaxPerMember; n++)
        {
            var response = await OpenAsync(user);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            streams.Add(response);
        }

        return streams;
    }

    [Fact]
    public async Task The_stream_above_the_cap_is_429_too_many_streams_and_another_member_is_not_affected()
    {
        var owner = await api.NewClient().SignedUpAsync("scap-owner");
        var other = await RemoteKit.MemberAsync(api, owner, "scap-other");
        var streams = await FillAsync(owner);
        try
        {
            var refused = await OpenAsync(owner);

            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(StreamLimiter.TooManyStreams, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
            using var others = await OpenAsync(other);
            Assert.Equal(HttpStatusCode.OK, others.StatusCode);
        }
        finally
        {
            streams.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public async Task A_closed_stream_gives_its_place_back()
    {
        var owner = await api.NewClient().SignedUpAsync("scap-release");
        var streams = await FillAsync(owner);
        try
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await OpenAsync(owner)).StatusCode);

            streams[0].Dispose();

            // The server notices the disconnect a moment later: ask until the place is free.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            HttpStatusCode last;
            do
            {
                using var again = await OpenAsync(owner);
                last = again.StatusCode;
                if (last != HttpStatusCode.OK) await Task.Delay(50, timeout.Token);
            }
            while (last != HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.OK, last);
        }
        finally
        {
            streams.ForEach(s => s.Dispose());
        }
    }
}
