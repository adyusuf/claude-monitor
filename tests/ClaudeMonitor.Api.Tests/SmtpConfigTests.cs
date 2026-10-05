using System.Text;
using ClaudeMonitor.Api.Config;
using Microsoft.Extensions.Configuration;

namespace ClaudeMonitor.Api.Tests;

/// <summary>SMTP from the MONITOR_SMTP_* keys or from the "Smtp" node of an appsettings file (a server-only
/// appsettings.Production.json); the key wins, an empty key never hides the node.</summary>
public sealed class SmtpConfigTests
{
    private static readonly Dictionary<string, string?> Required = new()
    {
        ["MONITOR_DB"] = "Host=db",
        ["MONITOR_PUBLIC_ORIGIN"] = "https://monitor.invalid",
        ["MONITOR_ARCHIVE_DIR"] = "/var/archive",
        ["MONITOR_MFA_KEY"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
    };

    private const string Node = """
        {
          "Smtp": {
            "Host": "smtp.node.invalid",
            "Port": 587,
            "User": "sender@node.invalid",
            "Password": "node-password",
            "From": "sender@node.invalid",
            "StartTls": true
          }
        }
        """;

    /// <summary>The way ASP.NET layers it: the JSON file first, environment variables (here: keys) over it.</summary>
    private static ApiConfig Load(string json, Dictionary<string, string?>? keys = null, bool development = false) =>
        ApiConfig.From(new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .AddInMemoryCollection(Required.Concat(keys ?? []))
            .Build(), development);

    [Fact]
    public void The_Smtp_node_of_an_appsettings_file_is_enough_outside_development()
    {
        var smtp = Load(Node).Smtp;
        Assert.Equal(new SmtpSettings("smtp.node.invalid", 587, "sender@node.invalid", "node-password",
            "sender@node.invalid", StartTls: true), smtp);
    }

    [Fact]
    public void A_MONITOR_SMTP_key_wins_over_the_node()
    {
        var smtp = Load(Node, new() { ["MONITOR_SMTP_HOST"] = "smtp.env.invalid", ["MONITOR_SMTP_PASSWORD"] = "env-password" }).Smtp;
        Assert.Equal("smtp.env.invalid", smtp.Host);
        Assert.Equal("env-password", smtp.Password);
        Assert.Equal("sender@node.invalid", smtp.User);              // the rest still comes from the node
    }

    [Fact]
    public void An_empty_key_does_not_hide_the_node()
    {
        // deploy.ps1 writes every monitor.env line into web.config, a blank MONITOR_SMTP_PASSWORD= too.
        var smtp = Load(Node, new() { ["MONITOR_SMTP_PASSWORD"] = "", ["MONITOR_SMTP_HOST"] = "" }).Smtp;
        Assert.Equal("node-password", smtp.Password);
        Assert.Equal("smtp.node.invalid", smtp.Host);
    }

    [Fact]
    public void Neither_key_nor_node_still_stops_the_API_and_names_both()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load("{}"));
        Assert.Contains("MONITOR_SMTP_HOST", error.Message, StringComparison.Ordinal);
        Assert.Contains("Smtp:Host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_node_under_another_name_is_not_read()
    {
        // Only "Smtp" is the node: anything else is ignored, so the API refuses to start rather than mail nowhere.
        Assert.Throws<InvalidOperationException>(() => Load(Node.Replace("\"Smtp\"", "\"Email\"", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void StartTls_reads_a_JSON_boolean_or_a_key_in_any_case(string value, bool expected)
    {
        Assert.Equal(expected, Load(Node, new() { ["MONITOR_SMTP_TLS"] = value }).Smtp.StartTls);
        Assert.Equal(expected, Load(Node.Replace("\"StartTls\": true", $"\"StartTls\": {value.ToLowerInvariant()}",
            StringComparison.Ordinal)).Smtp.StartTls);
    }

    [Fact]
    public void Development_without_either_keeps_the_local_defaults()
    {
        var smtp = Load("{}", development: true).Smtp;
        Assert.Equal(("localhost", 1025, "monitor@localhost", false), (smtp.Host, smtp.Port, smtp.From, smtp.StartTls));
        Assert.Null(smtp.Password);
    }
}
