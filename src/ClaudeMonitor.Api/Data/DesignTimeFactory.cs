using ClaudeMonitor.Api.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaudeMonitor.Api.Data;

/// <summary>Lets "dotnet ef migrations add" build the model without starting the app (it needs no database).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MonitorDb>
{
    public MonitorDb CreateDbContext(string[] args)
    {
        var config = ApiConfig.From(new ConfigurationBuilder().AddEnvironmentVariables().Build(), development: true);
        return new MonitorDb(new DbContextOptionsBuilder<MonitorDb>().UseNpgsql(config.DatabaseUrl).Options);
    }
}
