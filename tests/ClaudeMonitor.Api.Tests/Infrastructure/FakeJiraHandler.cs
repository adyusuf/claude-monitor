using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>The HTTP face of <see cref="FakeJira"/>: routes a request to the model and answers in Jira's JSON shapes.</summary>
public sealed partial class FakeJiraHandler(FakeJira jira) : HttpMessageHandler
{
    private const int DefaultPage = 50;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var bearer = request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null;
        jira.Requests.Enqueue(new FakeRequest(request.Method, uri.Host, path, bearer, body));
        if (jira.TakeFailure(path + uri.Query) is { } failure) return failure;

        if (uri.Host == FakeJira.AuthHost && request.Method == HttpMethod.Post && path == "/oauth/token") return await TokenAsync(body, cancellationToken);
        if (uri.Host != FakeJira.ApiHost) return Status(HttpStatusCode.NotFound);
        var account = jira.AccountOf(bearer);
        if (account is null) return Status(HttpStatusCode.Unauthorized);
        if (path == "/oauth/token/accessible-resources")
        {
            return Json(new JsonArray([.. account.Sites.Select(s => (JsonNode)new JsonObject { ["id"] = s.CloudId, ["url"] = "https://" + s.Host, ["name"] = s.Name })]));
        }

        var match = SitePath().Match(path);
        if (!match.Success) return Status(HttpStatusCode.NotFound);
        var site = account.Sites.FirstOrDefault(s => s.CloudId == match.Groups["cloud"].Value);
        if (site is null) return Status(HttpStatusCode.Forbidden);
        return Route(site, account, request.Method, match.Groups["rest"].Value, HttpUtility.ParseQueryString(uri.Query), body);
    }

    private async Task<HttpResponseMessage> TokenAsync(string body, CancellationToken ct)
    {
        if (jira.TokenDelay > TimeSpan.Zero) await Task.Delay(jira.TokenDelay, ct); // simulated network latency, not a wait for a state
        var form = JsonNode.Parse(body)?.AsObject();
        if (form?["client_id"]?.GetValue<string>() != FakeJira.ClientId || form["client_secret"]?.GetValue<string>() != FakeJira.ClientSecret)
        {
            return Json(new JsonObject { ["error"] = "access_denied" }, HttpStatusCode.Unauthorized);
        }

        var grant = form["grant_type"]?.GetValue<string>();
        var tokens = grant switch
        {
            "authorization_code" => jira.Exchange(form["code"]?.GetValue<string>() ?? "", form["code_verifier"]?.GetValue<string>() ?? "", form["redirect_uri"]?.GetValue<string>() ?? ""),
            "refresh_token" => jira.Refresh(form["refresh_token"]?.GetValue<string>() ?? ""),
            _ => default,
        };
        if (!tokens.Ok) return Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
        return Json(new JsonObject
        {
            ["access_token"] = tokens.Access,
            ["refresh_token"] = tokens.Refresh,
            ["expires_in"] = jira.ExpiresInSeconds,
            ["scope"] = "read:jira-work offline_access",
            ["token_type"] = "Bearer",
        });
    }

    private HttpResponseMessage Route(FakeSite site, FakeAccount account, HttpMethod method, string rest, System.Collections.Specialized.NameValueCollection query, string body)
    {
        var start = int.TryParse(query["startAt"], out var s) ? s : 0;
        var size = int.TryParse(query["maxResults"], out var m) ? m : DefaultPage;
        if (rest == "/rest/api/3/myself")
        {
            return Json(new JsonObject { ["accountId"] = account.Id, ["displayName"] = account.DisplayName, ["emailAddress"] = account.Email });
        }

        if (rest == "/rest/api/3/project/search")
        {
            var spaces = site.Spaces.Where(x => !x.Deleted).ToList();
            return Json(new JsonObject { ["isLast"] = start + size >= spaces.Count, ["values"] = Array(spaces.Skip(start).Take(size).Select(SpaceJson)) });
        }

        if (rest.StartsWith("/rest/api/3/project/", StringComparison.Ordinal))
        {
            var space = site.Spaces.FirstOrDefault(x => !x.Deleted && x.Key == Uri.UnescapeDataString(rest["/rest/api/3/project/".Length..]));
            return space is null ? Status(HttpStatusCode.NotFound) : Json(SpaceJson(space));
        }

        if (rest == "/rest/api/3/field")
        {
            return Json(new JsonArray(
                new JsonObject { ["id"] = "summary", ["name"] = "Summary", ["schema"] = new JsonObject { ["type"] = "string" } },
                new JsonObject { ["id"] = "customfield_10020", ["name"] = "Sprint", ["schema"] = new JsonObject { ["custom"] = "com.pyxis.greenhopper.jira:gh-sprint" } },
                new JsonObject { ["id"] = "customfield_10016", ["name"] = "Story point estimate", ["schema"] = new JsonObject { ["type"] = "number" } }));
        }

        if (rest == "/rest/api/3/search/jql" && method == HttpMethod.Post) return Search(site, body);
        if (rest.StartsWith("/rest/api/3/issue/", StringComparison.Ordinal))
        {
            var key = Uri.UnescapeDataString(rest["/rest/api/3/issue/".Length..]);
            var issue = site.Spaces.Where(x => !x.Deleted).SelectMany(x => x.Issues).FirstOrDefault(i => !i.Deleted && i.Key == key);
            return issue is null ? Status(HttpStatusCode.NotFound) : Json(IssueJson(site, issue, agile: false));
        }

        if (rest == "/rest/api/3/user/assignable/multiProjectSearch")
        {
            var space = site.Spaces.FirstOrDefault(x => x.Key == query["projectKeys"]);
            var people = (space?.People ?? []).Skip(start).Take(size);
            return Json(new JsonArray([.. people.Select(PersonJson)]));
        }

        if (rest.StartsWith("/rest/api/3/webhook", StringComparison.Ordinal)) return Webhook(site, method, rest, body);
        return rest.StartsWith("/rest/agile/1.0/", StringComparison.Ordinal) ? Agile(site, rest["/rest/agile/1.0".Length..], query, start, size) : Status(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Agile(FakeSite site, string rest, System.Collections.Specialized.NameValueCollection query, int start, int size)
    {
        if (rest == "/board")
        {
            var boards = site.Spaces.Where(x => !x.Deleted && x.BoardId is not null && x.Key == query["projectKeyOrId"]).ToList();
            return Json(new JsonObject { ["isLast"] = true, ["values"] = Array(boards.Select(b => (JsonNode)new JsonObject { ["id"] = b.BoardId, ["type"] = "scrum" })) });
        }

        var board = BoardPath().Match(rest);
        if (board.Success && site.SpaceOfBoard(long.Parse(board.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)) is { } space)
        {
            if (board.Groups["what"].Value == "sprint")
            {
                var sprints = site.Sprints.Values.Where(x => !x.Deleted && x.BoardId == space.BoardId).OrderBy(x => x.Id).ToList();
                return Json(new JsonObject { ["isLast"] = start + size >= sprints.Count, ["values"] = Array(sprints.Skip(start).Take(size).Select(SprintJson)) });
            }

            if (space.BoardListStatus is { } refused) return Status(refused);
            var backlog = space.Issues.Where(i => !i.Deleted && i.Category != "done" && (i.SprintId is not { } sid || site.Sprints[sid].State != "active"));
            return Json(new JsonObject { ["issues"] = Array(backlog.Skip(start).Take(size).Select(i => IssueJson(site, i, agile: true))) });
        }

        var sprint = SprintPath().Match(rest);
        if (!sprint.Success || !site.Sprints.TryGetValue(long.Parse(sprint.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture), out var found) || found.Deleted)
        {
            return Status(HttpStatusCode.NotFound);
        }

        if (!sprint.Groups["issue"].Success) return Json(SprintJson(found));
        var inSprint = site.Spaces.SelectMany(x => x.Issues).Where(i => !i.Deleted && i.SprintId == found.Id);
        return Json(new JsonObject { ["issues"] = Array(inSprint.Skip(start).Take(size).Select(i => IssueJson(site, i, agile: true))) });
    }

    private static HttpResponseMessage Search(FakeSite site, string body)
    {
        var request = JsonNode.Parse(body)!.AsObject();
        var jql = request["jql"]!.GetValue<string>();
        var size = request["maxResults"]?.GetValue<int>() ?? 100;
        var offset = request["nextPageToken"]?.GetValue<string>() is { } token ? int.Parse(token[1..], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var live = site.Spaces.Where(x => !x.Deleted).ToList();
        IEnumerable<FakeIssue> found;
        Match m;
        if ((m = BacklogJql().Match(jql)).Success)
        {
            found = live.Where(x => x.Key == m.Groups["k"].Value).SelectMany(x => x.Issues).Where(i => !i.Deleted && i.SprintId is null && i.Category != "done");
        }
        else if ((m = ChangedJql().Match(jql)).Success)
        {
            var keys = m.Groups["keys"].Value.Split(',').Select(k => k.Trim().Trim('"')).ToList();
            var since = DateTimeOffset.ParseExact(m.Groups["d"].Value, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
            found = live.Where(x => keys.Contains(x.Key)).SelectMany(x => x.Issues)
                .Where(i => !i.Deleted && new DateTimeOffset(i.Updated.Year, i.Updated.Month, i.Updated.Day, i.Updated.Hour, i.Updated.Minute, 0, TimeSpan.Zero) >= since)
                .OrderBy(i => i.Updated);
        }
        else if ((m = IdsJql().Match(jql)).Success)
        {
            found = live.Where(x => x.Key == m.Groups["k"].Value).SelectMany(x => x.Issues).Where(i => !i.Deleted);
        }
        else
        {
            return Json(new JsonObject { ["errorMessages"] = new JsonArray("unsupported query in the fake") }, HttpStatusCode.BadRequest);
        }

        var all = found.ToList();
        var page = new JsonObject { ["issues"] = Array(all.Skip(offset).Take(size).Select(i => IssueJson(site, i, agile: false))) };
        if (offset + size < all.Count) page["nextPageToken"] = "p" + (offset + size);
        return Json(page);
    }

    private HttpResponseMessage Webhook(FakeSite site, HttpMethod method, string rest, string body)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        if (method == HttpMethod.Post && rest == "/rest/api/3/webhook")
        {
            var results = new JsonArray();
            foreach (var w in json["webhooks"]!.AsArray())
            {
                var hook = new FakeWebhook(jira.NextId(), json["url"]!.GetValue<string>(), [.. w!["events"]!.AsArray().Select(e => e!.GetValue<string>())], w["jqlFilter"]?.GetValue<string>());
                lock (site.Webhooks) site.Webhooks.Add(hook);
                results.Add(new JsonObject { ["createdWebhookId"] = hook.Id });
            }

            return Json(new JsonObject { ["webhookRegistrationResult"] = results });
        }

        var ids = json["webhookIds"]!.AsArray().Select(i => i!.GetValue<long>()).ToList();
        if (method == HttpMethod.Delete)
        {
            lock (site.Webhooks) site.Webhooks.RemoveAll(w => ids.Contains(w.Id));
            return Status(HttpStatusCode.Accepted); // Jira answers 202 with no body
        }

        if (method == HttpMethod.Put && rest == "/rest/api/3/webhook/refresh")
        {
            if (!site.RefreshFails) return Json(new JsonObject { ["expirationDate"] = "2099-01-01T00:00:00.000+0000" });
            lock (site.Webhooks) site.Webhooks.RemoveAll(w => ids.Contains(w.Id)); // Jira has let them expire: it no longer knows them
            return Json(new JsonObject { ["errorMessages"] = new JsonArray("unknown webhook") }, HttpStatusCode.BadRequest);
        }

        return Status(HttpStatusCode.NotFound);
    }

    [GeneratedRegex("^/ex/jira/(?<cloud>[^/]+)(?<rest>/.*)$")]
    private static partial Regex SitePath();

    [GeneratedRegex("^/board/(?<id>\\d+)/(?<what>sprint|backlog)$")]
    private static partial Regex BoardPath();

    [GeneratedRegex("^/sprint/(?<id>\\d+)(?<issue>/issue)?$")]
    private static partial Regex SprintPath();

    [GeneratedRegex("^project = \"(?<k>[^\"]+)\" AND sprint is EMPTY AND statusCategory != Done ORDER BY Rank ASC$")]
    private static partial Regex BacklogJql();

    [GeneratedRegex("^project in \\((?<keys>.+)\\) AND updated >= \"(?<d>[^\"]+)\" ORDER BY updated ASC$")]
    private static partial Regex ChangedJql();

    [GeneratedRegex("^project = \"(?<k>[^\"]+)\" ORDER BY key ASC$")]
    private static partial Regex IdsJql();
}
