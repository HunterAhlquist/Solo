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
/// The emails' words and markup, in the instance theme's colors and fonts.
/// Inline styles and tables, because that is what mail apps render; every
/// email also carries a plain-text part.
/// </summary>
/// <remarks>
/// Inline styles carry the light palette. The dark palette rides in a
/// <c>prefers-color-scheme</c> block keyed on classes, and the email declares
/// both schemes: without that declaration Apple Mail inverts every color
/// itself, which turned the header light and left the white art unreadable.
/// </remarks>
public static class EmailTemplates
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly string[] Mark =
    [
        ".###....###.",
        "#...#..#...#",
        "............",
        "............",
        "............",
        ".##########.",
        ".#........#.",
        "..#......#..",
        "...######...",
    ];

    public static EmailContent Invite(EmailBrand brand, string inviter, string project, string email, string publicAddress)
    {
        var m = new Markup(brand);
        var subject = $"{inviter} invited you to {project} on {brand.Name}";
        var body = $"{inviter} added you to the {project} project on {brand.Name}. Sign in with the account for {email} to see it.";
        var footer = $"Sent by {Host(publicAddress)} because {inviter} invited this address. If you weren't expecting it, you can ignore this email.";
        var html = m.Layout(
            m.Heading(subject) +
            m.Paragraph($"{E(inviter)} added you to the {E(project)} project on {E(brand.Name)}. Sign in with the account for <strong>{E(email)}</strong> to see it.") +
            m.Button($"Open {brand.Name}", publicAddress),
            E(footer));
        var text = $"{subject}\n\n{body}\n\nOpen {brand.Name}: {publicAddress}\n\n{footer}\n";
        return new EmailContent(subject, html, text);
    }

    public static EmailContent Test(EmailBrand brand)
    {
        var m = new Markup(brand);
        const string subject = "Solo test email";
        var body = $"Email from {brand.Name} works.";
        return new EmailContent(subject, m.Layout(m.Paragraph(E(body)), null), body + "\n");
    }

    public static EmailContent Digest(
        EmailBrand brand, bool weekly, DateOnly today, string publicAddress, string settingsLink,
        IReadOnlyList<DigestTask> overdue, IReadOnlyList<DigestTask> dueSoon, IReadOnlyList<DigestNote> assigned)
    {
        var m = new Markup(brand);
        var subject = weekly ? $"Your weekly {brand.Name} digest" : $"Your daily {brand.Name} digest";
        var html = new StringBuilder()
            .Append(m.Heading(subject))
            .Append(m.Subheading(today.ToString("dddd, MMMM d", English)));
        var text = new StringBuilder().Append(subject).Append('\n').Append(today.ToString("dddd, MMMM d", English)).Append('\n');

        Section("Overdue", Tone.Danger, overdue, t => Late(-t.DaysLeft), Tone.Danger);
        Section("Due in the next 2 weeks", Tone.Accent, dueSoon,
            t => $"{t.Deadline.ToString("ddd, MMM d", English)}<br>{m.Span(DeadlineReminderService.When(t.DaysLeft), Tone.Muted)}", Tone.Text);
        if (assigned.Count > 0)
        {
            html.Append(m.SectionHeading("Recently assigned to you", Tone.Text)).Append(m.Rows(assigned.Select(n => m.Row(n.Title, n.Body, n.Link, null, Tone.Muted))));
            text.Append("\nRecently assigned to you\n");
            foreach (var note in assigned)
            {
                text.Append($"- {note.Title} ({note.Body}): {note.Link}\n");
            }
        }

        html.Append(m.Button($"Open {brand.Name}", publicAddress));
        var footerText = "You get this email because of your notification settings. Change them or turn the digest off:";
        text.Append($"\nOpen {brand.Name}: {publicAddress}\n\n{footerText} {settingsLink}\n");
        return new EmailContent(subject,
            m.Layout(html.ToString(), $"{E(footerText)} {m.Link(settingsLink, LinkLabel(settingsLink))}"),
            text.ToString());

        void Section(string title, Tone tone, IReadOnlyList<DigestTask> tasks, Func<DigestTask, string> when, Tone whenTone)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            html.Append(m.SectionHeading(title, tone)).Append(m.Rows(tasks.Select(t => m.Row(t.Title, t.Where, t.Link, when(t), whenTone))));
            text.Append($"\n{title}\n");
            foreach (var task in tasks)
            {
                var plainWhen = task.DaysLeft < 0 ? Late(-task.DaysLeft)
                    : $"{task.Deadline.ToString("ddd, MMM d", English)}, {DeadlineReminderService.When(task.DaysLeft)}";
                text.Append($"- {task.Title} ({task.Where}), {plainWhen}: {task.Link}\n");
            }
        }
    }

    public static EmailContent AsItHappens(EmailBrand brand, string publicAddress, string settingsLink, IReadOnlyList<DigestNote> notes)
    {
        var m = new Markup(brand);
        var subject = $"As it happens: {notes.Count} new on {brand.Name}";
        var html = m.Heading($"{notes.Count} new on {brand.Name}") +
                   m.Rows(notes.Select(n => m.Row(n.Title, n.Body, n.Link, null, Tone.Muted))) +
                   m.Button($"Open {brand.Name}", publicAddress);
        var footerText = "You get this email because of your notification settings. Change them or turn the digest off:";
        var text = new StringBuilder().Append($"{notes.Count} new on {brand.Name}\n\n");
        foreach (var note in notes)
        {
            text.Append($"- {note.Title} ({note.Body}): {note.Link}\n");
        }

        text.Append($"\nOpen {brand.Name}: {publicAddress}\n\n{footerText} {settingsLink}\n");
        return new EmailContent(subject,
            m.Layout(html, $"{E(footerText)} {m.Link(settingsLink, LinkLabel(settingsLink))}"),
            text.ToString());
    }

    private static string Late(int days) => days == 1 ? "1 day late" : $"{days} days late";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Host(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority : address;

    private static string LinkLabel(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Authority + uri.AbsolutePath.TrimEnd('/') : address;

    private enum Tone { Text, Muted, Accent, Danger }

    /// <summary>One email's markup in one theme. Each colored element gets its light color inline and a class the dark block overrides.</summary>
    private sealed class Markup(EmailBrand brand)
    {
        private readonly EmailPalette _light = EmailPalette.Light(brand.Theme);
        private readonly EmailPalette _dark = EmailPalette.Dark(brand.Theme);
        private readonly EmailType _type = EmailType.From(brand.Theme);

        public string Layout(string content, string? footerHtml)
        {
            var l = _light;
            var footer = footerHtml is null ? "" :
                $"<tr><td class=\"e-muted e-rule\" style=\"padding:16px 28px;border-top:1px solid {l.Rule};font-size:12px;line-height:1.5;color:{l.Muted};\">{footerHtml}</td></tr>";
            return $"""
                <!doctype html>
                <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                <meta name="color-scheme" content="light dark"><meta name="supported-color-schemes" content="light dark">
                <style>{Styles()}</style></head>
                <body class="e-page" style="margin:0;padding:24px 0;background:{l.Page};font-family:{_type.Body};color:{l.Text};">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td class="e-page" align="center" style="background:{l.Page};">
                <table role="presentation" width="560" cellpadding="0" cellspacing="0" class="e-card" style="max-width:560px;width:100%;background:{l.Surface};border-radius:{_type.Radius}px;overflow:hidden;border:1px solid {l.Border};">
                <tr><td class="e-head" style="background:{l.Header};padding:0 0 0 28px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
                <td class="e-head-text" valign="middle" style="padding:18px 0;font-family:{_type.Heading};font-size:20px;font-weight:bold;color:{l.HeaderText};">{E(brand.Name)}</td>
                <td align="right" valign="bottom" style="padding:0 8px 8px 0;line-height:0;font-size:0;">{MarkTable()}</td>
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

        public string Heading(string text) =>
            $"<tr><td class=\"e-text\" style=\"padding:28px 28px 8px;font-family:{_type.Heading};font-size:20px;font-weight:600;line-height:1.35;color:{_light.Text};\">{E(text)}</td></tr>";

        public string Subheading(string text) =>
            $"<tr><td class=\"e-muted\" style=\"padding:0 28px 8px;font-size:14px;color:{_light.Muted};\">{E(text)}</td></tr>";

        public string Paragraph(string html) =>
            $"<tr><td class=\"e-text\" style=\"padding:8px 28px 16px;font-size:15px;line-height:1.6;color:{_light.Text};\">{html}</td></tr>";

        public string Button(string label, string href) =>
            $"<tr><td style=\"padding:20px 28px 24px;\"><a class=\"e-button\" href=\"{E(href)}\" style=\"display:inline-block;background:{_light.Accent};color:{_light.AccentText};text-decoration:none;font-weight:600;font-size:15px;padding:12px 22px;border-radius:{Math.Min(_type.Radius, 6)}px;\">{E(label)}</a></td></tr>";

        public string Link(string href, string label) =>
            $"<a class=\"e-accent\" href=\"{E(href)}\" style=\"color:{_light.Accent};\">{E(label)}</a>";

        public string Span(string text, Tone tone) =>
            $"<span class=\"{Class(tone)}\" style=\"color:{Color(tone)};\">{E(text)}</span>";

        public string SectionHeading(string text, Tone tone) =>
            $"<tr><td class=\"{Class(tone)}\" style=\"padding:20px 28px 6px;font-family:{_type.Heading};font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:{Color(tone)};\">{E(text)}</td></tr>";

        public string Rows(IEnumerable<string> rows) =>
            $"<tr><td style=\"padding:0 28px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">{string.Concat(rows)}</table></td></tr>";

        /// <param name="whenHtml">Already markup: it may hold a line break.</param>
        public string Row(string title, string? detail, string link, string? whenHtml, Tone whenTone)
        {
            var cell = $"padding:10px 0;border-top:1px solid {_light.Rule};";
            var main = $"<td class=\"e-rule\" valign=\"top\" style=\"{cell}\"><a class=\"e-text\" href=\"{E(link)}\" style=\"color:{_light.Text};text-decoration:none;font-size:15px;font-weight:600;\">{E(title)}</a>" +
                       (detail is null ? "" : $"<div class=\"e-muted\" style=\"font-size:13px;color:{_light.Muted};padding-top:2px;\">{E(detail)}</div>") + "</td>";
            var when = whenHtml is null ? "" :
                $"<td class=\"e-rule {Class(whenTone)}\" align=\"right\" valign=\"top\" style=\"{cell}padding-left:12px;font-size:13px;color:{Color(whenTone)};white-space:nowrap;\">{whenHtml}</td>";
            return $"<tr>{main}{when}</tr>";
        }

        private string Color(Tone tone) => tone switch
        {
            Tone.Muted => _light.Muted,
            Tone.Accent => _light.Accent,
            Tone.Danger => _light.Danger,
            _ => _light.Text,
        };

        private static string Class(Tone tone) => tone switch
        {
            Tone.Muted => "e-muted",
            Tone.Accent => "e-accent",
            Tone.Danger => "e-danger",
            _ => "e-text",
        };

        /// <summary>
        /// The pixel smiley as table cells rather than an image: it takes the
        /// header text color in either scheme, and no mail app can blow it up
        /// past its size the way Apple Mail did the attached picture.
        /// </summary>
        private string MarkTable()
        {
            const int pixel = 2;
            var rows = Mark.Select(line =>
                "<tr>" + string.Concat(line.Select(c => c == '#'
                    ? $"<td class=\"e-mark\" width=\"{pixel}\" height=\"{pixel}\" style=\"width:{pixel}px;height:{pixel}px;background:{_light.HeaderText};font-size:0;line-height:0;\"></td>"
                    : $"<td width=\"{pixel}\" height=\"{pixel}\" style=\"width:{pixel}px;height:{pixel}px;font-size:0;line-height:0;\"></td>")) + "</tr>");
            return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" align=\"right\" style=\"border-collapse:collapse;\">{string.Concat(rows)}</table>";
        }

        private string Styles()
        {
            var d = _dark;
            return $$"""
                :root{color-scheme:light dark;supported-color-schemes:light dark;}
                @media (prefers-color-scheme: dark){
                .e-page{background:{{d.Page}} !important;color:{{d.Text}} !important;}
                .e-card{background:{{d.Surface}} !important;border-color:{{d.Border}} !important;}
                .e-head{background:{{d.Header}} !important;}
                .e-head-text{color:{{d.HeaderText}} !important;}
                .e-mark{background:{{d.HeaderText}} !important;}
                .e-text{color:{{d.Text}} !important;}
                .e-muted{color:{{d.Muted}} !important;}
                .e-accent{color:{{d.Accent}} !important;}
                .e-danger{color:{{d.Danger}} !important;}
                .e-rule{border-color:{{d.Rule}} !important;}
                .e-button{background:{{d.Accent}} !important;color:{{d.AccentText}} !important;}
                }
                """;
        }
    }
}
