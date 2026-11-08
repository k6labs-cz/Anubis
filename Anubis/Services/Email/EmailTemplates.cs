using System.Globalization;
using System.Net;
using System.Text;

namespace k6labs.Anubis.Services.Email;

/// <summary>
/// Builds simple, client-safe HTML emails (inline styles, one button) plus a text version,
/// in the default light palette. Call it with the recipient's UI culture set
/// (see <see cref="Anubis.Localization.Cultures.With{T}"/>): the fixed texts come from Translations.
/// </summary>
public static class EmailTemplates
{
    // Fixed light palette (email clients don't support CSS variables), close to the Hathor light theme.
    private const string Background = "#f6f8fa";
    private const string Surface = "#ffffff";
    private const string Border = "#d0d7de";
    private const string Text = "#1f2328";
    private const string Muted = "#57606a";
    private const string Primary = "#00839c";   // dark teal — readable on white (cyan #00c8e6 is not)
    private const string Font = "ui-monospace,SFMono-Regular,Menlo,Consolas,'Liberation Mono',monospace";

    public static EmailMessage Action(string to, string subject, string heading, IEnumerable<string> paragraphs, string buttonText, string url, string footer)
    {
        var paragraphList = paragraphs.ToList();
        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        var html = new StringBuilder();
        html.Append($"<!DOCTYPE html><html lang=\"{lang}\"><body style=\"margin:0;padding:24px;background:{Background};font-family:{Font};color:{Text}\">");
        html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr><td align=\"center\">");
        html.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:480px;background:{Surface};border:1px solid {Border};border-radius:6px;padding:32px\">");
        html.Append("<tr><td>");
        html.Append($"<p style=\"margin:0 0 24px;font-size:18px;font-weight:700;color:{Primary}\">Anubis</p>");
        html.Append($"<h1 style=\"margin:0 0 16px;font-size:20px\">{Encode(heading)}</h1>");
        foreach (var paragraph in paragraphList)
        {
            html.Append($"<p style=\"margin:0 0 16px;font-size:15px;line-height:1.5\">{Encode(paragraph)}</p>");
        }

        html.Append($"<p style=\"margin:24px 0\"><a href=\"{Encode(url)}\" style=\"display:inline-block;padding:12px 20px;background:{Primary};color:#ffffff;text-decoration:none;border-radius:4px;font-weight:600\">{Encode(buttonText)}</a></p>");
        html.Append($"<p style=\"margin:0 0 16px;font-size:13px;color:{Muted};word-break:break-all\">{Encode(Translations.Email_LinkFallback)}<br><a href=\"{Encode(url)}\" style=\"color:{Primary}\">{Encode(url)}</a></p>");
        html.Append($"<p style=\"margin:24px 0 0;font-size:13px;color:{Muted}\">{Encode(footer)}</p>");
        html.Append("</td></tr></table></td></tr></table></body></html>");

        var text = new StringBuilder();
        text.AppendLine(heading).AppendLine();
        foreach (var paragraph in paragraphList)
        {
            text.AppendLine(paragraph).AppendLine();
        }

        text.AppendLine($"{buttonText}: {url}").AppendLine().AppendLine(footer);

        return new EmailMessage(to, subject, html.ToString(), text.ToString());
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
