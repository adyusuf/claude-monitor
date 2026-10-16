using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Update;

/// <summary>
/// With MONITOR_DOWNLOADS_URL the builds live on the download host, but an old link, the web page and the plugin's bootstrap still ask
/// this server for /downloads/&lt;file&gt;. A file this server holds itself was already answered by the static-file mount, so only the rest is
/// sent on (302, not 301: the host may change). A name that is not a plain file name is a 404.
/// </summary>
public static class DownloadsRedirect
{
    public static void Map(IEndpointRouteBuilder routes, string downloadsUrl) =>
        routes.MapGet($"/{ApiConfig.DownloadsFolder}/{{file}}", (string file) =>
            UpdateUrls.IsPlainFile(file) ? Results.Redirect($"{downloadsUrl}/{file}") : Results.NotFound());
}
