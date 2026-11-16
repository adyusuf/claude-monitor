using System.Text;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// A permission request's tool input is capped (F-189): an older agent that sends a whole file gets it kept as the captured-content marker with a
/// preview, so the database and every list hold at most the cap; an input within the cap is kept as it came.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionToolInputCapTests(ApiFactory api)
{
    [Fact]
    public async Task An_input_over_the_cap_is_kept_as_a_marker_with_a_preview_and_listed_so()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "cap-over");
        var content = new string('x', PermissionToolInputs.MaxBytes);

        var created = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Write", new { file_path = "/w/a.txt", content });

        var row = await ApprovalRuleKit.RequestRowAsync(api, created.Id);
        var kept = row.ToolInput.RootElement;
        Assert.True(kept.GetProperty("truncated").GetBoolean());
        Assert.True(kept.GetProperty("bytes").GetInt32() > PermissionToolInputs.MaxBytes);
        Assert.Equal(PermissionToolInputs.PreviewChars, kept.GetProperty("preview").GetString()!.Length);
        Assert.False(kept.TryGetProperty("content", out _));
        Assert.True(Encoding.UTF8.GetByteCount(kept.GetRawText()) < PermissionToolInputs.MaxBytes);
        var sessionId = row.SessionId;
        var listed = (await w.Owner.GetJsonAsync($"/api/sessions/{sessionId}/permission-requests")).EnumerateArray().Single();
        Assert.True(listed.GetProperty("toolInput").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task An_input_within_the_cap_is_kept_whole()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "cap-under");
        var content = new string('x', 10_000);

        var created = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Write", new { file_path = "/w/a.txt", content });

        var kept = (await ApprovalRuleKit.RequestRowAsync(api, created.Id)).ToolInput.RootElement;
        Assert.Equal(content, kept.GetProperty("content").GetString());
        Assert.False(kept.TryGetProperty("truncated", out _));
    }
}
