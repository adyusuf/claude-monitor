using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The pure rules of a machine role (ADR-0008 §2, §3a): what a claim may tighten, what counts as set, what lowers.</summary>
public sealed class MachineRolesTests
{
    private const string Prod = MachineEnvironments.Production;
    private const string Test = MachineEnvironments.Test;
    private const string Db = MachineFunctions.Database;
    private const string App = MachineFunctions.Application;

    public static TheoryData<string?, string?, string?> Environments => new()
    {
        { null, null, null },
        { null, Test, null },
        { null, MachineEnvironments.Dev, null },
        { null, Prod, Prod },
        { Test, null, Test },
        { Test, Prod, Prod },
        { Test, Test, Test },
        { Prod, null, Prod },
        { Prod, Test, Prod },
        { Prod, Prod, Prod },
        { MachineEnvironments.Stage, Prod, Prod },
        { MachineEnvironments.Stage, MachineEnvironments.Other, MachineEnvironments.Stage },
        { MachineEnvironments.Preprod, null, MachineEnvironments.Preprod },
    };

    [Theory]
    [MemberData(nameof(Environments))]
    public void The_effective_environment_is_production_when_either_side_says_so_and_otherwise_the_set_value(string? set, string? claimed, string? expected) =>
        Assert.Equal(expected, MachineRoles.EffectiveEnvironment(set, claimed));

    public static TheoryData<string?, string?, string?> Functions => new()
    {
        { null, null, null },
        { null, App, null },
        { null, MachineFunctions.Development, null },
        { null, Db, Db },
        { App, null, App },
        { App, Db, Db },
        { App, App, App },
        { Db, null, Db },
        { Db, App, Db },
        { Db, Db, Db },
        { MachineFunctions.Web, Db, Db },
        { MachineFunctions.Ci, MachineFunctions.Other, MachineFunctions.Ci },
    };

    [Theory]
    [MemberData(nameof(Functions))]
    public void The_effective_function_is_database_when_either_side_says_so_and_otherwise_the_set_value(string? set, string? claimed, string? expected) =>
        Assert.Equal(expected, MachineRoles.EffectiveFunction(set, claimed));

    [Theory]
    [InlineData(Prod, null, true)]
    [InlineData(null, Db, true)]
    [InlineData(Prod, Db, true)]
    [InlineData(Prod, App, true)]
    [InlineData(Test, Db, true)]
    [InlineData(Test, App, false)]
    [InlineData(Test, null, false)]
    [InlineData(MachineEnvironments.Stage, MachineFunctions.Web, false)]
    [InlineData(null, null, false)]
    public void Only_production_or_database_is_strict(string? environment, string? function, bool expected) =>
        Assert.Equal(expected, MachineRoles.IsStrict(environment, function));

    [Fact]
    public void Only_a_set_environment_counts_as_set_and_a_claim_alone_never_does()
    {
        Assert.False(MachineRoles.IsSet(null));
        foreach (var environment in MachineEnvironments.All) Assert.True(MachineRoles.IsSet(environment));
        // A machine that claims production but has no set role is effectively production and still not set.
        Assert.Equal(Prod, MachineRoles.EffectiveEnvironment(null, Prod));
        Assert.False(MachineRoles.IsSet(null));
    }

    [Theory]
    [InlineData(Prod, null, Test, null, true)]
    [InlineData(Prod, App, Test, App, true)]
    [InlineData(Prod, null, null, null, true)]
    [InlineData(null, Db, null, App, true)]
    [InlineData(Test, Db, Test, App, true)]
    [InlineData(Test, Db, Test, null, true)]
    [InlineData(Prod, Db, Prod, App, true)]
    [InlineData(Test, null, Prod, null, false)]
    [InlineData(null, null, Prod, null, false)]
    [InlineData(null, null, null, Db, false)]
    [InlineData(Test, App, Test, Db, false)]
    [InlineData(Prod, Db, Prod, Db, false)]
    [InlineData(Test, App, Test, App, false)]
    [InlineData(null, null, null, null, false)]
    [InlineData(Test, App, null, null, false)]
    public void Leaving_production_or_database_lowers_and_nothing_else_does(string? oldEnv, string? oldFn, string? newEnv, string? newFn, bool expected) =>
        Assert.Equal(expected, MachineRoles.IsLowering(oldEnv, oldFn, newEnv, newFn));

    [Fact]
    public void Null_and_every_listed_environment_and_function_pass_and_anything_else_does_not()
    {
        Assert.True(MachineRoles.IsEnvironment(null));
        Assert.True(MachineRoles.IsFunction(null));
        foreach (var environment in MachineEnvironments.All) Assert.True(MachineRoles.IsEnvironment(environment), environment);
        foreach (var function in MachineFunctions.All) Assert.True(MachineRoles.IsFunction(function), function);
        foreach (var bad in new[] { "prod", "", "Production", " test", "database" }) Assert.False(MachineRoles.IsEnvironment(bad), bad);
        foreach (var bad in new[] { "prod", "", "Database", "db ", "production" }) Assert.False(MachineRoles.IsFunction(bad), bad);
    }

    [Theory]
    [InlineData(null, null, "not set", "not set")]
    [InlineData(null, App, "not set", "not set")]
    [InlineData("test", "development", "test machine", "test/development")]
    [InlineData("test", "application", "test server", "test/application")]
    [InlineData("test", "web", "test server", "test/web")]
    [InlineData("production", "application", "live server", "production/application")]
    [InlineData("production", "database", "live database server", "production/database")]
    [InlineData("production", null, "live server", "production/not set")]
    [InlineData("test", "database", "test database server", "test/database")]
    [InlineData("stage", null, "stage machine", "stage/not set")]
    [InlineData("other", "other", "other other machine", "other/other")]
    public void A_role_is_described_for_people_and_coded_for_the_session_start_block(string? environment, string? function, string described, string code)
    {
        Assert.Equal(described, MachineRoles.Describe(environment, function));
        Assert.Equal(code, MachineRoles.Code(environment, function));
    }
}
