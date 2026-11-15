using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The web judges paths and executables early with copies of the C# lists (web/src/lib/accessPaths.ts mirrors GrantTemplateRules and
/// AccessPolicyRules, ADR-0020 §4). The API decides, but a copy that drifts tells a person the wrong thing, so each copy is read from the web
/// source and compared with the list the contracts use.
/// </summary>
public sealed partial class AccessListParityTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Rules = typeof(GrantMatcher).Assembly.GetType("Podium.Contracts.GrantTemplateRules", throwOnError: true)!;
    private static readonly Lazy<string> Web = new(() => File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "lib", "accessPaths.ts")));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Podium.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("the repository root was not found");
    }

    /// <summary>The quoted strings of the web constant <paramref name="name"/> (a Set or an array literal).</summary>
    private static List<string> WebList(string name)
    {
        var block = Regex.Match(Web.Value, $@"const {name} = (?:new Set\()?\[(?<body>[^\]]*)\]", RegexOptions.Singleline);
        Assert.True(block.Success, $"{name} was not found in accessPaths.ts");
        return [.. Quoted().Matches(block.Groups["body"].Value).Select(m => m.Groups["text"].Value)];
    }

    /// <summary>The web string constant <paramref name="name"/>, its escapes undone.</summary>
    private static string WebString(string name)
    {
        var match = Regex.Match(Web.Value, $@"const {name} = ""(?<text>(?:[^""\\]|\\.)*)"";");
        Assert.True(match.Success, $"{name} was not found in accessPaths.ts");
        return Regex.Unescape(match.Groups["text"].Value);
    }

    private static List<string> CSharpList(Type type, string field) =>
        [.. ((IEnumerable)type.GetField(field, Private)!.GetValue(null)!).Cast<string>()];

    private static string CSharpConst(Type type, string field) => (string)type.GetField(field, Private)!.GetRawConstantValue()!;

    [Theory]
    [InlineData("INTERPRETERS", "NeverGrantable")]
    [InlineData("INTERPRETER_PREFIXES", "NeverGrantablePrefixes")]
    [InlineData("FORBIDDEN_UNIX_TOP", "ForbiddenUnixTop")]
    [InlineData("WINDOWS_DEVICES", "WindowsDeviceNames")]
    public void Each_web_list_holds_exactly_the_entries_of_its_csharp_list(string web, string csharp)
    {
        var copy = WebList(web);
        var original = CSharpList(Rules, csharp);

        Assert.NotEmpty(original);
        Assert.Equal(original.Order(StringComparer.Ordinal), copy.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("META_CHARS", "MetaChars")]
    public void The_web_metacharacters_are_the_csharp_ones(string web, string csharp) =>
        Assert.Equal(CSharpConst(Rules, csharp), WebString(web));

    [Fact]
    public void The_web_windows_refused_characters_are_the_csharp_ones() =>
        Assert.Equal(CSharpConst(typeof(AccessPolicyRules), "WindowsRefused"), WebString("WINDOWS_REFUSED"));

    [Fact]
    public void The_web_limits_are_the_csharp_ones()
    {
        Assert.Matches($@"export const MAX_PATH_LENGTH = {AccessPolicyRules.MaxPathLength};", Web.Value);
        Assert.Matches($@"const MIN_ROOT_DEPTH = {Rules.GetField("MinRootDepth")!.GetRawConstantValue()};", Web.Value);
    }

    [GeneratedRegex(@"""(?<text>[^""]*)""")]
    private static partial Regex Quoted();
}
