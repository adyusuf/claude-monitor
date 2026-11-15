using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The limits and the canonical form of an access policy document (ADR-0020 §4): what the API refuses with which code, what a valid path is on each
/// OS, and the one canonical form the browser signs and the API insists on. Pure; the OS is a parameter.
/// </summary>
public sealed class AccessPolicyRulesTests
{
    private const string Linux = OsKinds.Linux;
    private const string Mac = OsKinds.MacOs;
    private const string Win = OsKinds.Windows;
    private const string Key = "git:git.example.invalid/team/shop";
    private static readonly AccessOsRules None = new([], [], []);

    private static AccessPolicyDocument Doc(AccessOsRules? linux = null, AccessOsRules? mac = null, AccessOsRules? windows = null, string[]? keys = null,
        bool shell = false, bool cleared = false, int format = AccessPolicyRules.Format) =>
        new(format, keys ?? [Key], true, linux ?? None, mac ?? None, windows ?? None, shell, cleared);

    // ---- validate ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_document_and_a_full_valid_document_pass()
    {
        Assert.Null(AccessPolicyRules.Validate(AccessPolicyRules.DefaultDocument([Key])));
        var full = Doc(new AccessOsRules(["/srv/app"], ["/srv/app/secret"], [new("/usr/bin/tail", new string('0', 64))]),
            new AccessOsRules(["/Users/dev/work"], [], [new("/usr/bin/make", null)]),
            new AccessOsRules([@"C:\Work\Shop"], [@"C:\Work\Shop\keys"], [new(@"C:\Tools\tool.exe", null)]), shell: true);
        Assert.Null(AccessPolicyRules.Validate(full));
    }

    [Fact]
    public void A_missing_part_is_malformed_and_another_format_is_refused()
    {
        Assert.Equal(AccessPolicyErrors.Malformed, AccessPolicyRules.Validate(null));
        Assert.Equal(AccessPolicyErrors.Malformed, AccessPolicyRules.Validate(Doc() with { ProjectKeys = null! }));
        Assert.Equal(AccessPolicyErrors.Malformed, AccessPolicyRules.Validate(Doc(linux: new AccessOsRules(null!, [], []))));
        Assert.Equal(AccessPolicyErrors.Malformed, AccessPolicyRules.Validate(Doc(mac: new AccessOsRules([], [], [null!]))));
        Assert.Equal(AccessPolicyErrors.Malformed, AccessPolicyRules.Validate(Doc() with { Windows = null! }));
        Assert.Equal(AccessPolicyErrors.Format, AccessPolicyRules.Validate(Doc(format: 2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void A_policy_names_one_to_twenty_repositories(int count) =>
        Assert.Equal(AccessPolicyErrors.Keys, AccessPolicyRules.Validate(Doc(keys: [.. Enumerable.Range(0, count).Select(i => Key + i)])));

    [Theory]
    [InlineData("")]
    [InlineData("git:shop\u202e")]
    [InlineData("git:shop\u200b")]
    [InlineData("git:shop\n")]
    public void A_repo_key_with_a_hidden_control_or_broken_character_or_none_at_all_is_refused(string key) =>
        Assert.Equal(AccessPolicyErrors.Keys, AccessPolicyRules.Validate(Doc(keys: [key])));

    [Fact]
    public void A_repo_key_is_at_most_two_hundred_characters()
    {
        Assert.True(AccessPolicyRules.IsValidKey(new string('k', AccessPolicyRules.MaxProjectKeyLength)));
        Assert.False(AccessPolicyRules.IsValidKey(new string('k', AccessPolicyRules.MaxProjectKeyLength + 1)));
        Assert.False(AccessPolicyRules.IsValidKey(null));
        Assert.False(AccessPolicyRules.IsValidKey("git:shop" + (char)0xD800)); // an unpaired surrogate has no NFC form
    }

    [Fact]
    public void Each_list_holds_at_most_thirty_two_entries()
    {
        var folders = Enumerable.Range(0, AccessPolicyRules.MaxFolders + 1).Select(i => $"/srv/f{i}").ToList();
        var apps = Enumerable.Range(0, AccessPolicyRules.MaxApplications + 1).Select(i => new AccessApp($"/usr/bin/t{i}", null)).ToList();

        Assert.Null(AccessPolicyRules.Validate(Doc(new AccessOsRules(folders[..^1], folders[..^1], apps[..^1]))));
        Assert.Equal(AccessPolicyErrors.TooMany, AccessPolicyRules.Validate(Doc(new AccessOsRules(folders, [], []))));
        Assert.Equal(AccessPolicyErrors.TooMany, AccessPolicyRules.Validate(Doc(mac: new AccessOsRules([], folders, []))));
        Assert.Equal(AccessPolicyErrors.TooMany, AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([], [], apps))));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/etc/app")]
    [InlineData("/proc/self")]
    public void An_allowed_folder_that_is_a_system_or_top_level_root_is_refused_on_linux_but_a_denied_one_may_name_it(string folder)
    {
        Assert.Equal(AccessPolicyErrors.ForbiddenRoot, AccessPolicyRules.Validate(Doc(new AccessOsRules([folder], [], []))));
        Assert.Null(AccessPolicyRules.Validate(Doc(new AccessOsRules([], [folder], []))));
    }

    [Fact]
    public void The_forbidden_roots_follow_the_os_of_their_section()
    {
        Assert.Equal(AccessPolicyErrors.ForbiddenRoot, AccessPolicyRules.Validate(Doc(mac: new AccessOsRules(["/private/etc/ssh"], [], []))));
        Assert.Equal(AccessPolicyErrors.ForbiddenRoot, AccessPolicyRules.Validate(Doc(mac: new AccessOsRules(["/ETC/ssh"], [], []))));
        Assert.Equal(AccessPolicyErrors.ForbiddenRoot, AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([@"C:\Windows\System32"], [], []))));
        Assert.Equal(AccessPolicyErrors.ForbiddenRoot, AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([@"C:\Users"], [], []))));
        Assert.Null(AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([@"C:\Users\dev"], [], []))));
        Assert.Null(AccessPolicyRules.Validate(Doc(new AccessOsRules(["/ETC/app"], [], [])))); // Linux is case-sensitive: /ETC is not /etc
    }

    [Fact]
    public void An_application_must_be_an_absolute_executable_of_its_os_and_its_sha256_lowercase_hex()
    {
        Assert.Equal(AccessPolicyErrors.App, AccessPolicyRules.Validate(Doc(new AccessOsRules([], [], [new("tail", null)]))));
        Assert.Equal(AccessPolicyErrors.App, AccessPolicyRules.Validate(Doc(new AccessOsRules([], [], [new("/usr/bin/t$il", null)]))));
        Assert.Equal(AccessPolicyErrors.App, AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([], [], [new(@"C:\Tools\tool.bat", null)]))));
        Assert.Equal(AccessPolicyErrors.Sha256, AccessPolicyRules.Validate(Doc(new AccessOsRules([], [], [new("/usr/bin/tail", new string('A', 64))]))));
        Assert.Equal(AccessPolicyErrors.Sha256, AccessPolicyRules.Validate(Doc(new AccessOsRules([], [], [new("/usr/bin/tail", new string('a', 63))]))));
        Assert.Equal(AccessPolicyErrors.Sha256, AccessPolicyRules.Validate(Doc(new AccessOsRules([], [], [new("/usr/bin/tail", new string('g', 64))]))));
    }

    [Fact]
    public void A_cleared_policy_holds_no_entry_and_no_shell()
    {
        Assert.Null(AccessPolicyRules.Validate(Doc(cleared: true)));
        Assert.Equal(AccessPolicyErrors.Cleared, AccessPolicyRules.Validate(Doc(cleared: true, shell: true)));
        Assert.Equal(AccessPolicyErrors.Cleared, AccessPolicyRules.Validate(Doc(new AccessOsRules([], ["/srv/x"], []), cleared: true)));
        Assert.Equal(AccessPolicyErrors.Cleared, AccessPolicyRules.Validate(Doc(windows: new AccessOsRules([], [], [new(@"C:\T\t.exe", null)]), cleared: true)));
    }

    [Fact]
    public void A_document_whose_encoding_exceeds_sixty_four_kibibytes_is_too_large()
    {
        List<string> Long(string root) => [.. Enumerable.Range(0, AccessPolicyRules.MaxFolders).Select(i => $"{root}{i:D2}" + new string('a', 990))];
        var large = Doc(new AccessOsRules(Long("/srv/a"), [], []), new AccessOsRules(Long("/srv/b"), [], []), new AccessOsRules(Long(@"C:\Work\c"), [], []));

        Assert.True(AccessPolicySigning.Encode(large).Length > AccessPolicyRules.MaxEncodedBytes);
        Assert.Equal(AccessPolicyErrors.TooLarge, AccessPolicyRules.Validate(large));
    }

    // ---- valid paths --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/", Linux, true)]
    [InlineData("/srv/app", Linux, true)]
    [InlineData("/srv/caf\u00e9", Mac, true)]
    [InlineData("srv/app", Linux, false)]
    [InlineData("/srv/app/", Linux, false)]
    [InlineData("/srv//app", Linux, false)]
    [InlineData("/srv/./app", Linux, false)]
    [InlineData("/srv/../etc", Mac, false)]
    [InlineData("/srv/a\u202eb", Linux, false)]
    [InlineData("/srv/a\u0007", Linux, false)]
    [InlineData(@"C:\", Win, true)]
    [InlineData(@"C:\Work\Shop", Win, true)]
    [InlineData("C:/Work", Win, false)]
    [InlineData(@"C:\Work\", Win, false)]
    [InlineData(@"\\server\share", Win, false)]
    [InlineData(@"\\?\C:\Work", Win, false)]
    [InlineData(@"C:\Work\a:stream", Win, false)]
    [InlineData(@"C:\Work\a*", Win, false)]
    [InlineData(@"C:\PROGRA~1\Tool", Win, false)]
    [InlineData(@"C:\Work\a.", Win, false)]
    [InlineData(@"C:\Work\a ", Win, false)]
    [InlineData(@"C:\Work\a/b", Win, false)]
    [InlineData("/srv/app", Win, false)]
    [InlineData(@"C:\Work", Linux, false)]
    [InlineData("/srv/app", "freebsd", false)]
    public void A_valid_path_is_absolute_for_its_os_with_no_hidden_character_or_ambiguous_segment(string path, string os, bool valid) =>
        Assert.Equal(valid, AccessPolicyRules.IsValidPath(path, os));

    [Fact]
    public void A_path_is_at_most_1024_characters()
    {
        Assert.True(AccessPolicyRules.IsValidPath("/" + new string('a', AccessPolicyRules.MaxPathLength - 1), Linux));
        Assert.False(AccessPolicyRules.IsValidPath("/" + new string('a', AccessPolicyRules.MaxPathLength), Linux));
        Assert.False(AccessPolicyRules.IsValidPath(null, Linux));
    }

    // ---- canonical form -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("c:/work/shop/", Win, @"C:\work\shop")]
    [InlineData(@"C:\", Win, @"C:\")]
    [InlineData("/srv/app///", Linux, "/srv/app")]
    [InlineData("/", Linux, "/")]
    [InlineData("/srv/cafe\u0301", Mac, "/srv/caf\u00e9")]
    public void One_path_in_canonical_form_is_nfc_without_a_trailing_separator_and_on_windows_with_backslashes_and_an_upper_case_drive(string path,
        string os, string canonical) =>
        Assert.Equal(canonical, AccessPolicyRules.CanonicalPath(path, os));

    [Fact]
    public void The_canonical_document_sorts_and_dedupes_every_list_ordinally_lowercases_hashes_and_never_folds_case()
    {
        var raw = Doc(new AccessOsRules(["/srv/b", "/srv/a/", "/srv/b", "/srv/B"], ["/x/z", "/x/y"], [new("/usr/bin/z", null), new("/usr/bin/a", new string('C', 64)),
            new("/usr/bin/a", new string('c', 64))]), keys: ["git:b", "git:cafe\u0301", "git:a", "git:a"]);

        var canonical = AccessPolicyRules.Canonical(raw);

        Assert.Equal(["git:a", "git:b", "git:caf\u00e9"], canonical.ProjectKeys);
        Assert.Equal(["/srv/B", "/srv/a", "/srv/b"], canonical.Linux.AllowedFolders);
        Assert.Equal(["/x/y", "/x/z"], canonical.Linux.DeniedFolders);
        Assert.Equal([new AccessApp("/usr/bin/a", new string('c', 64)), new AccessApp("/usr/bin/z", null)], canonical.Linux.Applications);
        Assert.False(AccessPolicyRules.IsCanonical(raw));
        Assert.True(AccessPolicyRules.IsCanonical(canonical));
    }

    [Fact]
    public void A_missing_list_becomes_empty_in_canonical_form_and_an_incomplete_document_is_never_canonical()
    {
        var holed = Doc() with { Linux = null!, ProjectKeys = [Key, null!] };

        var canonical = AccessPolicyRules.Canonical(holed);

        Assert.Equal([Key], canonical.ProjectKeys);
        Assert.Empty(canonical.Linux.AllowedFolders);
        Assert.False(AccessPolicyRules.IsCanonical(holed));
    }

    [Fact]
    public void The_section_of_an_unknown_os_is_null()
    {
        var doc = Doc(new AccessOsRules(["/srv"], [], []));
        Assert.Same(doc.Linux, AccessPolicyRules.Section(doc, Linux));
        Assert.Same(doc.MacOs, AccessPolicyRules.Section(doc, Mac));
        Assert.Same(doc.Windows, AccessPolicyRules.Section(doc, Win));
        Assert.Null(AccessPolicyRules.Section(doc, "freebsd"));
    }
}
