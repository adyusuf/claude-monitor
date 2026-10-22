using Podium.Api;
using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

// The API's entry point: configuration, services, middleware and routes (ADR-0002). The pieces live in their own
// files; this file only wires them. "--migrate" applies the database migrations and exits (the deploy runs it).
var builder = WebApplication.CreateBuilder(args);
var development = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
var config = ApiConfig.From(builder.Configuration, development);
builder.Services.AddPodiumServices(config);

var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PodiumDb>().Database.MigrateAsync();
    return;
}

app.UseMonitorPipeline(config);
await app.RunAsync();

/// <summary>Visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
