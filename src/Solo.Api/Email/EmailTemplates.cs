using System.Globalization;
using System.Net;
using System.Text;
using Solo.Api.Services;

namespace Solo.Api.Email;

public sealed record EmailContent(string Subject, string Html, string Text);

/// <summary>A task in a digest's due lists.</summary>
public sealed record DigestTask(string Title, string Where, string Link, DateOnly Deadline, int DaysLeft);

/// <summary>A notification in a digest.</summary>
public sealed record DigestNote(string Title, string? Body, string Link);

/// <summary>
/// The emails' words and markup. Inline styles and tables, because that is what
/// mail apps render; every email also carries a plain-text part.
/// </summary>
public static class EmailTemplates
{
    /// <summary>The Content-ID of the header art, attached to every email rather than linked, so it shows without "load images".</summary>
    public const string MarkContentId = "solo-mark";

    private const string Ink = "#1d1b16";
    private const string Muted = "#77736a";
    private const string Rule = "#ece8dc";
    private const string Accent = "#df6610";
    private const string Danger = "#b3261e";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static EmailContent Invite(string instanceName, string inviter, string project, string email, string publicAddress)
    {
        var subject = $"{inviter} invited you to {project} on {instanceName}";
        var body = $"{inviter} added you to the {project} project on {instanceName}. Sign in with the account for {email} to see it.";
        var footer = $"Sent by {Host(publicAddress)} because {inviter} invited this address. If you weren't expecting it, you can ignore this email.";
        var html = Layout(instanceName,
            Heading(subject) +
            Paragraph($"{E(inviter)} added you to the {E(project)} project on {E(instanceName)}. Sign in with the account for <strong>{E(email)}</strong> to see it.") +
            Button($"Open {instanceName}", publicAddress),
            E(footer));
        var text = $"{subject}\n\n{body}\n\nOpen {instanceName}: {publicAddress}\n\n{footer}\n";
        return new EmailContent(subject, html, text);
    }

    public static EmailContent Test(string instanceName)
    {
        const string subject = "Solo test email";
        var body = $"Email from {instanceName} works.";
        return new EmailContent(subject, Layout(instanceName, Paragraph(E(body)), null), body + "\n");
    }

    public static EmailContent Digest(
        string instanceName, bool weekly, DateOnly today, string publicAddress, string settingsLink,
        IReadOnlyList<DigestTask> overdue, IReadOnlyList<DigestTask> dueSoon, IReadOnlyList<DigestNote> assigned)
    {
        var subject = weekly ? $"Your weekly {instanceName} digest" : $"Your daily {instanceName} digest";
        var html = new StringBuilder()
            .Append(Heading(subject))
            .Append(Subheading(today.ToString("dddd, MMMM d", English)));
        var text = new StringBuilder().Append(subject).Append('\n').Append(today.ToString("dddd, MMMM d", English)).Append('\n');

        Section("Overdue", Danger, overdue, t => Late(-t.DaysLeft), Danger);
        Section("Due in the next 2 weeks", "#c25509", dueSoon, t => $"{t.Deadline.ToString("ddd, MMM d", English)}<br><span style=\"color:{Muted};\">{DeadlineReminderService.When(t.DaysLeft)}</span>", "#3b382f");
        if (assigned.Count > 0)
        {
            html.Append(SectionHeading("Recently assigned to you", "#3b382f")).Append(Rows(assigned.Select(n => Row(n.Title, n.Body, n.Link, null, null))));
            text.Append("\nRecently assigned to you\n");
            foreach (var note in assigned)
            {
                text.Append(NoteLine(note));
            }
        }

        html.Append(Button($"Open {instanceName}", publicAddress));
        var footerText = "You get this email because of your notification settings. Change them or turn the digest off:";
        text.Append($"\nOpen {instanceName}: {publicAddress}\n\n{footerText} {settingsLink}\n");
        return new EmailContent(subject,
            Layout(instanceName, html.ToString(), $"{E(footerText)} {Link(settingsLink, LinkLabel(settingsLink))}"),
            text.ToString());

        void Section(string title, string color, IReadOnlyList<DigestTask> tasks, Func<DigestTask, string> when, string whenColor)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            html.Append(SectionHeading(title, color)).Append(Rows(tasks.Select(t => Row(t.Title, t.Where, t.Link, when(t), whenColor))));
            text.Append($"\n{title}\n");
            foreach (var task in tasks)
            {
                var plainWhen = task.DaysLeft < 0 ? Late(-task.DaysLeft)
                    : $"{task.Deadline.ToString("ddd, MMM d", English)}, {DeadlineReminderService.When(task.DaysLeft)}";
                text.Append($"- {task.Title} ({task.Where}), {plainWhen}: {task.Link}\n");
            }
        }
    }

    // A notification's body is a sentence of its own (NotificationMessages),
    // so it follows the title rather than sitting in brackets, and the link
    // gets its own line.
    private static string NoteLine(DigestNote note) =>
        note.Body is { Length: > 0 } body
            ? $"- {note.Title} — {body}\n  {note.Link}\n"
            : $"- {note.Title}\n  {note.Link}\n";

    public static EmailContent AsItHappens(string instanceName, string publicAddress, string settingsLink, IReadOnlyList<DigestNote> notes)
    {
        var subject = $"As it happens: {notes.Count} new on {instanceName}";
        var html = Heading($"{notes.Count} new on {instanceName}") +
                   Rows(notes.Select(n => Row(n.Title, n.Body, n.Link, null, null))) +
                   Button($"Open {instanceName}", publicAddress);
        var footerText = "You get this email because of your notification settings. Change them or turn the digest off:";
        var text = new StringBuilder().Append($"{notes.Count} new on {instanceName}\n\n");
        foreach (var note in notes)
        {
            text.Append(NoteLine(note));
        }

        text.Append($"\nOpen {instanceName}: {publicAddress}\n\n{footerText} {settingsLink}\n");
        return new EmailContent(subject,
            Layout(instanceName, html, $"{E(footerText)} {Link(settingsLink, LinkLabel(settingsLink))}"),
            text.ToString());
    }

    private static string Late(int days) => days == 1 ? "1 day late" : $"{days} days late";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Host(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;

    private static string LinkLabel(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority + uri.AbsolutePath.TrimEnd('/') : address;

    private static string Link(string href, string label) =>
        $"<a href=\"{E(href)}\" style=\"color:#c25509;\">{E(label)}</a>";

    private static string Layout(string instanceName, string content, string? footerHtml)
    {
        var footer = footerHtml is null ? "" :
            $"<tr><td style=\"padding:16px 28px;border-top:1px solid {Rule};font-size:12px;line-height:1.5;color:{Muted};\">{footerHtml}</td></tr>";
        return $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"></head>
            <body style="margin:0;padding:24px 0;background:#f2f0ea;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:{Ink};">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center">
            <table role="presentation" width="560" cellpadding="0" cellspacing="0" style="max-width:560px;width:100%;background:#fffdf8;border-radius:8px;overflow:hidden;border:1px solid #e3dfd3;">
            <tr><td style="background:#1b1a14;padding:0 0 0 28px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
            <td valign="middle" style="padding:18px 0;font-family:'Courier New',Courier,monospace;font-size:20px;font-weight:bold;color:#fff600;">{E(instanceName)}</td>
            <td align="right" valign="bottom" style="padding:0 8px 8px 0;line-height:0;"><img src="cid:{MarkContentId}" width="24" height="18" alt="" style="display:block;border:0;"></td>
            </tr></table>
            </td></tr>
            {content}
            <tr><td style="height:8px;"></td></tr>
            {footer}
            </table>
            </td></tr></table>
            </body></html>
            """;
    }

    private static string Heading(string text) =>
        $"<tr><td style=\"padding:28px 28px 8px;font-size:20px;font-weight:600;line-height:1.35;\">{E(text)}</td></tr>";

    private static string Subheading(string text) =>
        $"<tr><td style=\"padding:0 28px 8px;font-size:14px;color:{Muted};\">{E(text)}</td></tr>";

    private static string Paragraph(string html) =>
        $"<tr><td style=\"padding:8px 28px 16px;font-size:15px;line-height:1.6;color:#3b382f;\">{html}</td></tr>";

    private static string Button(string label, string href) =>
        $"<tr><td style=\"padding:20px 28px 24px;\"><a href=\"{E(href)}\" style=\"display:inline-block;background:{Accent};color:#ffffff;text-decoration:none;font-weight:600;font-size:15px;padding:12px 22px;border-radius:6px;\">{E(label)}</a></td></tr>";

    private static string SectionHeading(string text, string color) =>
        $"<tr><td style=\"padding:20px 28px 6px;font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:{color};\">{E(text)}</td></tr>";

    private static string Rows(IEnumerable<string> rows) =>
        $"<tr><td style=\"padding:0 28px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">{string.Concat(rows)}</table></td></tr>";

    /// <param name="whenHtml">Already markup: it may hold a line break.</param>
    private static string Row(string title, string? detail, string link, string? whenHtml, string? whenColor)
    {
        var cell = $"padding:10px 0;border-top:1px solid {Rule};";
        var main = $"<td valign=\"top\" style=\"{cell}\"><a href=\"{E(link)}\" style=\"color:{Ink};text-decoration:none;font-size:15px;font-weight:600;\">{E(title)}</a>" +
                   (detail is null ? "" : $"<div style=\"font-size:13px;color:{Muted};padding-top:2px;\">{E(detail)}</div>") + "</td>";
        var when = whenHtml is null ? "" :
            $"<td align=\"right\" valign=\"top\" style=\"{cell}padding-left:12px;font-size:13px;color:{whenColor};white-space:nowrap;\">{whenHtml}</td>";
        return $"<tr>{main}{when}</tr>";
    }
}
