using System.Text.RegularExpressions;
using Solo.Api.Services;
using Xunit;
using static Solo.Api.Services.NotificationMessages;

namespace Solo.Api.Tests;

/// <summary>
/// The notification wording catalog is edited by hand, so these hold every
/// line to what the code can fill in, and every title to what a phone's
/// banner shows before it cuts the rest off.
/// </summary>
public sealed partial class NotificationMessagesTests
{
    // Typical values, on the long side of typical: a full name, a three-part serial.
    private static readonly IReadOnlyDictionary<string, string> Typical = new Dictionary<string, string>
    {
        ["assigner"] = "Alex Johnson",
        ["task"] = "Ship the demo build",
        ["epic"] = "Act one",
        ["serial"] = "GAME-SP-12",
        ["project"] = "Game",
        ["board"] = "Sprint",
    };

    private static readonly string[] Whens =
        ["today", "tomorrow", "in 1 week", "in 2 weeks", DeadlineReminderService.When(10)];

    public static TheoryData<Situation> Situations => new(Enum.GetValues<Situation>());

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [Theory]
    [MemberData(nameof(Situations))]
    public void Every_situation_has_lines_that_use_only_its_placeholders(Situation situation)
    {
        Assert.NotEmpty(Templates(situation));
        foreach (var template in Templates(situation))
        {
            foreach (Match match in Placeholder().Matches(template.Title + " " + template.Body))
            {
                Assert.Contains(match.Groups[1].Value, Placeholders[situation]);
            }

            Assert.False(string.IsNullOrWhiteSpace(template.Title));
            Assert.False(string.IsNullOrWhiteSpace(template.Body));
        }
    }

    [Theory]
    [MemberData(nameof(Situations))]
    public void Every_line_fills_in_completely_and_its_title_fits_a_banner(Situation situation)
    {
        foreach (var when in Whens)
        {
            var values = new Dictionary<string, string>(Typical) { ["when"] = when };
            foreach (var message in Possible(situation, values))
            {
                Assert.DoesNotMatch(Placeholder(), message.Title + message.Body);
                Assert.True(message.Title.Length <= TitleBudget, $"Too long for a banner ({message.Title.Length}): {message.Title}");
                Assert.True(message.Body.Length <= 120, $"Too long: {message.Body}");
            }
        }
    }

    [Fact]
    public void Fill_puts_the_values_in()
    {
        var message = Fill(new Template("{assigner} assigned you a task", "Task '{task}' was assigned to you."), Typical);

        Assert.Equal(new Message("Alex Johnson assigned you a task", "Task 'Ship the demo build' was assigned to you."), message);
    }

    [Fact]
    public void A_key_always_picks_the_same_line()
    {
        var values = new Dictionary<string, string>(Typical) { ["when"] = "in 1 week" };
        const string key = "DueThisWeek:01a1d111-f603-7175-83ae-84afd1331f30:2026-12-12";

        var first = Pick(Situation.DueThisWeek, values, key);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, Pick(Situation.DueThisWeek, values, key));
        }

        // And keys spread over the lines rather than all landing on one.
        var picked = Enumerable.Range(0, 200)
            .Select(i => Pick(Situation.DueThisWeek, values, $"DueThisWeek:{Guid.NewGuid()}:2026-12-12"))
            .Distinct()
            .Count();
        Assert.Equal(Templates(Situation.DueThisWeek).Distinct().Count(), picked);
    }

    [Fact]
    public void The_requested_lines_are_in_the_catalog()
    {
        Assert.Contains(new Template("{assigner} assigned you a task", "Task '{task}' was assigned to you."), Templates(Situation.Assigned));
        Assert.Contains(new Template("{serial} is due {when}", "Deadline for '{task}' is creeping up soon!"), Templates(Situation.DueThisWeek));
        Assert.Contains(new Template("{serial} is due {when}", "Deadline for '{task}' is creeping up soon!"), Templates(Situation.DueInTwoWeeks));
    }
}
