using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

public sealed class RunEnvironmentTests
{
    [Fact]
    public void A_unix_run_environment_is_a_fixed_path_the_home_and_a_utf8_locale_and_nothing_else()
    {
        var env = RunEnvironment.Build("/var/lib/cm-agent", windows: false, systemRoot: "");
        Assert.Equal(["HOME=/var/lib/cm-agent", "LANG=C.UTF-8", "LC_ALL=C.UTF-8", "PATH=/usr/bin:/bin"], env.Select(p => $"{p.Key}={p.Value}").Order());
    }

    [Fact]
    public void A_windows_run_environment_is_the_system_folders_the_profile_and_a_temp_folder_inside_the_home()
    {
        var env = RunEnvironment.Build(@"C:\ProgramData\ClaudeMonitor\service", windows: true, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows\System32;C:\Windows", env["PATH"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.Equal(@"C:\ProgramData\ClaudeMonitor\service", env["USERPROFILE"]);
        Assert.Equal(env["TEMP"], env["TMP"]);
        Assert.StartsWith(@"C:\ProgramData\ClaudeMonitor\service", env["TEMP"], StringComparison.Ordinal);
        Assert.Equal(5, env.Count);
    }

    [Theory]
    [InlineData("CM_SERVER")]
    [InlineData("cm_service")]
    [InlineData("LD_PRELOAD")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("PYTHONPATH")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("BASH_ENV")]
    [InlineData("PAGER")]
    [InlineData("LESSOPEN")]
    [InlineData("COMSPEC")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("http_proxy")]
    public void A_variable_that_loads_code_or_redirects_tools_is_refused_by_name(string name)
    {
        Assert.True(RunEnvironment.IsForbidden(name));
        var e = Assert.Throws<InvalidOperationException>(() => RunEnvironment.AssertClean(new Dictionary<string, string> { [name] = "super-secret-value" }));
        Assert.Contains(name, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", e.Message, StringComparison.Ordinal); // the message names the variable, never its value
    }

    [Fact]
    public void A_variable_that_is_not_on_the_allowed_list_is_refused_too_and_the_allowed_ones_pass()
    {
        Assert.Throws<InvalidOperationException>(() => RunEnvironment.AssertClean(new Dictionary<string, string> { ["MY_TOKEN"] = "x" }));
        RunEnvironment.AssertClean(new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["home"] = "/h", ["LANG"] = "C", ["LC_ALL"] = "C" });
        Assert.False(RunEnvironment.IsForbidden("PATH"));
    }
}
