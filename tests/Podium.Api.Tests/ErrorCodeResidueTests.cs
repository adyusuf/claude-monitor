using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-089: the two codes a page can still receive that the web dictionary now has a text for (errors.test.ts): the API sends them as the first code of the field.</summary>
[Collection(ApiGroup.Name)]
public sealed class ErrorCodeResidueTests(ApiFactory api)
{
    [Fact]
    public async Task A_language_that_is_not_offered_is_refused_with_invalid_language_on_the_language_field()
    {
        var user = await api.NewClient().SignedUpAsync("res-language");

        var refused = await user.SendAsync(HttpMethod.Put, "/api/me/language", new { language = "xx" });
        var missing = await user.SendAsync(HttpMethod.Put, "/api/me/language", new { });

        Assert.Equal(("language", "invalid_language"), await ProjectKit.ProblemAsync(refused));
        Assert.Equal(("language", "invalid_language"), await ProjectKit.ProblemAsync(missing));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Put, "/api/me/language", new { language = "de" })).StatusCode);
    }

    [Fact]
    public async Task An_approval_key_name_that_is_empty_too_long_or_holds_a_control_character_is_refused_with_invalid_on_the_name_field()
    {
        var user = await api.NewClient().SignedUpAsync("res-key-name");

        foreach (var name in new[] { "", new string('k', 65), "tab\tname" })
        {
            var refused = await user.PostAsync("/api/me/approval-keys", new { name, publicKey = "AAAA" });
            Assert.Equal(("name", "invalid"), await ProjectKit.ProblemAsync(refused));
        }
    }
}
