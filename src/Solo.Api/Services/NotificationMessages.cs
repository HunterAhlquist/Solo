using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Solo.Api.Services;

/// <summary>
/// What a notification says: a short title and a longer body, picked from the
/// built-in catalog in <c>NotificationMessages.json</c> beside this file (an
/// embedded resource, not something an instance can change). Each situation
/// lists any number of lines; to add one, add an object with a <c>title</c> and
/// a <c>body</c> to its list, using only that situation's placeholders
/// (<see cref="Placeholders"/>). The tests hold every line to those, and every
/// title to <see cref="TitleBudget"/> with typical values, since a phone's
/// banner cuts a long title off.
/// </summary>
/// <remarks>
/// The text is picked when a notification is raised and stored with it, so the
/// bell, the device and the email digest all say the same thing, and changing a
/// line here leaves the ones already raised as they were. An assignment picks
/// at random. A deadline reminder picks by its dedupe key, so the same
/// reminder always reads the same.
/// </remarks>
public static class NotificationMessages
{
    public enum Situation
    {
        Assigned,
        AssignedEpic,
        DueInTwoWeeks,
        DueThisWeek,
    }

    public sealed record Template(string Title, string Body);

    public sealed record Message(string Title, string Body);

    /// <summary>
    /// What each situation's lines may use. <c>{when}</c> is
    /// <see cref="DeadlineReminderService.When"/>: "today", "tomorrow", "in 1
    /// week", "in 2 weeks", or "in N days" for a deadline set late.
    /// </summary>
    public static readonly IReadOnlyDictionary<Situation, IReadOnlyList<string>> Placeholders =
        new Dictionary<Situation, IReadOnlyList<string>>
        {
            [Situation.Assigned] = ["assigner", "task", "serial", "project", "board"],
            [Situation.AssignedEpic] = ["assigner", "epic", "project", "board"],
            [Situation.DueInTwoWeeks] = ["serial", "task", "when", "project", "board"],
            [Situation.DueThisWeek] = ["serial", "task", "when", "project", "board"],
        };

    /// <summary>The longest a title should run with typical values; enforced by the tests.</summary>
    public const int TitleBudget = 40;

    private static readonly Lazy<IReadOnlyDictionary<Situation, IReadOnlyList<Template>>> Catalog = new(Load);

    public static IReadOnlyList<Template> Templates(Situation situation) => Catalog.Value[situation];

    /// <summary>
    /// A line for <paramref name="situation"/>, filled in: chosen by
    /// <paramref name="key"/> when there is one, so the same key always gets
    /// the same line, or at random otherwise.
    /// </summary>
    public static Message Pick(Situation situation, IReadOnlyDictionary<string, string> values, string? key = null)
    {
        var templates = Templates(situation);
        var index = key is null
            ? Random.Shared.Next(templates.Count)
            : (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) % (uint)templates.Count);
        return Fill(templates[index], values);
    }

    /// <summary>Every way <paramref name="situation"/> could read with these values.</summary>
    public static IReadOnlyList<Message> Possible(Situation situation, IReadOnlyDictionary<string, string> values) =>
        Templates(situation).Select(t => Fill(t, values)).ToList();

    public static Message Fill(Template template, IReadOnlyDictionary<string, string> values) =>
        new(Render(template.Title, values), Render(template.Body, values));

    private static string Render(string text, IReadOnlyDictionary<string, string> values)
    {
        var result = new StringBuilder(text);
        foreach (var (name, value) in values)
        {
            result.Replace("{" + name + "}", value);
        }

        return result.ToString();
    }

    private static IReadOnlyDictionary<Situation, IReadOnlyList<Template>> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Solo.Api.Services.NotificationMessages.json")
            ?? throw new InvalidOperationException("The notification message catalog isn't embedded.");
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<Template>>>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The notification message catalog is empty.");
        return Enum.GetValues<Situation>().ToDictionary(
            s => s,
            s => (IReadOnlyList<Template>)(raw.TryGetValue(JsonNamingPolicy.CamelCase.ConvertName(s.ToString()), out var list) && list.Count > 0
                ? list
                : throw new InvalidOperationException($"The notification message catalog has no lines for {s}.")));
    }
}
