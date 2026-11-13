using System.Text;
using Praxis.Application.Documents.Html;

namespace Praxis.Application.Documents.Email;

/// <summary>
/// Saved email (EML): identifying headers, the plain-text body (or the HTML body as text when
/// no plain part exists) and an inventory of attachments with type and size. The subject is the
/// title. Attachment content is not read here.
/// </summary>
public sealed class EmailDocumentReader : IDocumentReader
{
    private static readonly string[] Shown = ["From", "To", "Cc", "Date", "Subject"];

    public IReadOnlyCollection<string> Extensions { get; } = [".eml", ".emlx"];

    public bool CanRead(DocumentInput input)
    {
        if (BinarySignature.Matches(input.Bytes) || BinarySignature.BinaryExtensions.Contains(input.Extension))
        {
            return false;
        }

        if (Extensions.Contains(input.Extension))
        {
            return true;
        }

        string head = Encoding.Latin1.GetString(input.Bytes, 0, Math.Min(input.Bytes.Length, 4_096));
        bool startsLikeMail = head.StartsWith("Return-Path:", StringComparison.OrdinalIgnoreCase) || head.StartsWith("Received:", StringComparison.OrdinalIgnoreCase)
                              || head.StartsWith("From:", StringComparison.OrdinalIgnoreCase) || head.StartsWith("MIME-Version:", StringComparison.OrdinalIgnoreCase)
                              || head.StartsWith("Delivered-To:", StringComparison.OrdinalIgnoreCase);
        return startsLikeMail && head.Contains("\nSubject:", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        byte[] bytes = input.Bytes;
        // Apple Mail .emlx prefixes the message with its byte length on the first line.
        if (input.Extension == ".emlx" && Array.IndexOf(bytes, (byte)'\n') is var newline and > 0 and < 16)
        {
            bytes = bytes[(newline + 1)..];
        }

        MimeMessage? message;
        try
        {
            message = MimeParser.Parse(bytes);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Task.FromResult(ReadOutcome.Unreadable("email_too_complex"));
        }

        if (message is null)
        {
            return Task.FromResult(ReadOutcome.Unreadable("email_malformed"));
        }

        StringBuilder text = new();
        foreach (string header in Shown)
        {
            if (message.Header(header) is { Length: > 0 } value)
            {
                text.Append(header).Append(": ").Append(value).Append('\n');
            }
        }

        text.Append('\n');
        MimePart? plain = message.Parts.FirstOrDefault(part => !part.IsAttachment && part.MediaType == "text/plain");
        MimePart? html = message.Parts.FirstOrDefault(part => !part.IsAttachment && part.MediaType == "text/html");
        if (plain is not null)
        {
            text.Append(MimeParser.Text(plain));
        }
        else if (html is not null)
        {
            text.Append(HtmlText.Convert(MimeParser.Text(html)));
        }

        MimePart[] attachments = message.Parts.Where(part => part.IsAttachment).ToArray();
        if (attachments.Length > 0)
        {
            text.Append("\n\nAttachments:\n");
            foreach (MimePart attachment in attachments)
            {
                text.Append("- ").Append(attachment.FileName ?? "unnamed").Append(" (").Append(attachment.MediaType).Append(", ")
                    .Append(attachment.Content.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)).Append(" bytes)\n");
            }
        }

        return Task.FromResult(ReadOutcome.Read(new ReadDocument(DocumentFormats.Email, text.ToString(), Title: message.Header("Subject"))));
    }
}
