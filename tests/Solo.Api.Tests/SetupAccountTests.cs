using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Solo.Api.Auth;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// Until the wizard hands off, the static setup account is the way in to a new
/// instance, with only a per-IP rate limit in front of it. These pin which
/// passwords it refuses to exist with. SetupFlowTests covers the same rule
/// through a running host.
/// </summary>
public sealed partial class SetupAccountTests
{
    [Theory]
    [InlineData("CHANGE_ME_SETUP_PASSWORD")]
    [InlineData("change_me_but_lowercase_and_long")]
    [InlineData("CHANGE_ME")]
    [InlineData("nineteen-characters")]
    [InlineData("hunter2")]
    [InlineData("")]
    [InlineData("                         ")]
    public void A_guessable_password_disables_the_account(string password)
    {
        var account = Account("setup", password);

        Assert.NotNull(account.DisabledReason);
        Assert.Contains("Setup__Password", account.DisabledReason);
        // Including when the caller typed exactly what is configured.
        Assert.False(account.Accepts("setup", password));
    }

    [Fact]
    public void An_unset_password_disables_the_account()
    {
        var account = new SetupAccount(new ConfigurationBuilder().Build());

        Assert.NotNull(account.DisabledReason);
        Assert.False(account.Accepts("", ""));
    }

    [Fact]
    public void An_unset_username_disables_the_account()
    {
        var account = Account("", "a-long-random-password-of-forty-characters");

        Assert.Contains("Setup__Username", account.DisabledReason);
        Assert.False(account.Accepts("", "a-long-random-password-of-forty-characters"));
    }

    [Fact]
    public void Twenty_characters_is_enough()
    {
        const string password = "twenty-characters-ok";
        Assert.Equal(SetupAccount.MinimumPasswordLength, password.Length);

        var account = Account("setup", password);

        Assert.Null(account.DisabledReason);
        Assert.True(account.Accepts("setup", password));
        Assert.False(account.Accepts("setup", password + "!"));
        Assert.False(account.Accepts("admin", password));
    }

    /// <summary>
    /// The values an operator copies are the ones that end up deployed
    /// unedited. If either template's placeholder ever stops starting with
    /// CHANGE_ME, this is what notices.
    /// </summary>
    [Theory]
    [InlineData("deploy/docker-compose.yml")]
    [InlineData("README.md")]
    public void The_templates_placeholder_is_refused(string file)
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), file));
        var placeholders = SetupPasswordLine().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, placeholder => Assert.NotNull(Account("setup", placeholder).DisabledReason));
    }

    private static SetupAccount Account(string username, string password) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Setup:Username"] = username,
                ["Setup:Password"] = password,
            })
            .Build());

    [GeneratedRegex("""Setup__Password:\s*"([^"]*)""")]
    private static partial Regex SetupPasswordLine();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Solo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Solo.slnx from the test output directory.");
    }
}
