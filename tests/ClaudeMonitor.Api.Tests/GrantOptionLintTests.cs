using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>A grant a Claude session asks for may not carry an option that runs another program (ADR-0005).</summary>
public sealed class GrantOptionLintTests
{
    private static GrantTemplate Template(params string[] argv) => new(argv, "/var/log/app", 60);

    [Theory]
    [InlineData("/usr/bin/find", "-exec")]
    [InlineData("/usr/bin/find", "-execdir")]
    [InlineData("/usr/bin/find", "-ok")]
    [InlineData("/usr/bin/find", "-delete")]
    [InlineData("/usr/bin/tar", "--to-command=cat")]
    [InlineData("/usr/bin/tar", "--to-c=cat")]
    [InlineData("/usr/bin/tar", "--checkpoint-action=exec=x")]
    [InlineData("/usr/bin/fetcher", "--upload-pack=x")]
    [InlineData("/usr/bin/scp", "-oProxyCommand=nc")]
    [InlineData("/usr/bin/scp", "-oproxycommand=nc")]
    [InlineData("/usr/bin/rsync", "-e")]
    [InlineData("/usr/bin/rsync", "--rsh=ssh")]
    [InlineData("/usr/bin/rsync", "--rs=ssh")]
    [InlineData("/usr/bin/rsync", "-avze")]
    [InlineData("/usr/bin/find", "{enum:-ls|-exec}")]
    [InlineData("/usr/bin/find", "-exec={word}")]
    public void An_option_that_runs_another_program_is_refused_in_a_grant_a_claude_session_asked_for(string program, string element)
    {
        var template = Template(program, "/var/log", element);
        Assert.Equal(GrantErrors.ExecOption, GrantMatcher.Validate(template, OsKinds.Linux, requestedByClaude: true));
        Assert.Null(GrantMatcher.Validate(template, OsKinds.Linux, requestedByClaude: false));
    }

    [Theory]
    [InlineData("/usr/bin/tail", "-e")]
    [InlineData("/usr/bin/rsync", "-avz")]
    [InlineData("/usr/bin/rsync", "--archive")]
    [InlineData("/usr/bin/find", "-name")]
    [InlineData("/usr/bin/find", "--")]
    [InlineData("/usr/bin/find", "-ex")]
    [InlineData("/usr/bin/find", "{path:/var/log/app/}")]
    public void An_ordinary_option_passes_the_lint_of_a_claude_request(string program, string element) =>
        Assert.Null(GrantMatcher.Validate(Template(program, element), OsKinds.Linux, requestedByClaude: true));
}
