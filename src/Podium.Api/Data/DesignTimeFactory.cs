using Podium.Api.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Podium.Api.Data;

/// <summary>Lets "dotnet ef migrations add" build the model without starting the app (it needs no database).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PodiumDb>
{
    public PodiumDb CreateDbContext(string[] args)
    {
        var config = ApiConfig.From(new ConfigurationBuilder().AddEnvironmentVariables().Build(), development: true);
        return new PodiumDb(new DbContextOptionsBuilder<PodiumDb>().UseNpgsql(config.DatabaseUrl).Options);
    }
}
