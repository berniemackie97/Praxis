using System.Text;
using System.Text.Json;
using Praxis.Application.Documents;
using Praxis.Application.Documents.Pdf;
using Praxis.Application.Finance;
using Praxis.Application.Media;
using Praxis.Application.Originals;
using Praxis.Application.Sources;
using Praxis.Application.Text;
using Praxis.Domain.Agents;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;
using Praxis.Domain.Sources;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Opens any citation at its location with surrounding context: a character range of extracted
/// text, a PDF page, a delimited row, a line of a statement export, or a phrase. Reads only the
/// current revision of an active source and never more than a bounded window.
/// </summary>
public sealed class KnowledgeOpenTool(
    ITextDerivativeRepository text, ISourceRepository sources, IOriginalStore originals, IPdfTextReader pdf, MediaMoments? moments = null) : IAgentTool
{
    private const int Window = 6_000;
    private const int MaximumOriginalBytes = 32 * 1024 * 1024;

    public AgentToolDefinition Definition { get; } = new("knowledge_open",
        """
        Open a source to read it, using sourceId (or itemId+revision) from another tool's result. Optional locator from a citation:
        chars:START-END, page:N[:line:M], row:N, line:N, stmttrn:N, time:START-END (seconds, media). Optional 'around' centres on a phrase. Returns up to 6000 characters
        with offsets so you can page with 'offset'.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{"sourceId":{"type":"string"},"itemId":{"type":"string"},"revision":{"type":"integer","minimum":1},
              "locator":{"type":"string"},"around":{"type":"string"},"offset":{"type":"integer","minimum":0}}}
            """), ToolTiers.Read);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        Guid sourceId = args.Guid("sourceId") ?? ResolveSource(args);
        CurrentFileVersion current = sources.CurrentFile(sourceId) ?? throw new ArgumentException("That source is not an active file source.");
        if (!context.MayDisclose(sourceId))
        {
            return AgentToolOutput.Of(new { error = "withheld_by_disclosure_policy", sourceId },
                withheld: [new WithheldNotice(1, WithheldNotice.DisclosurePolicy)]);
        }

        string? locator = args.Text("locator", 64);
        string? around = args.Text("around", 200);
        (string body, string unit) = await ReadAsync(current, locator, cancellationToken);
        int start = around is not null && body.IndexOf(around, StringComparison.OrdinalIgnoreCase) is var found and >= 0
            ? Math.Max(0, found - Window / 4)
            : TimeStart(sourceId, current, locator) ?? LocatorStart(body, locator, unit) ?? Math.Min(body.Length, args.Integer("offset", 0, 0, int.MaxValue));
        string excerpt = body.Substring(start, Math.Min(Window, body.Length - start));
        string name = Path.GetFileName(current.Locator);
        return AgentToolOutput.Of(new
        {
            sourceId,
            current.ItemId,
            current.Revision,
            source = name,
            unit,
            offset = start,
            totalLength = body.Length,
            nextOffset = start + excerpt.Length < body.Length ? start + excerpt.Length : (int?)null,
            text = excerpt
        }, [new AgentCitation(sourceId, current.ItemId, current.Revision, locator ?? $"chars:{start}-{start + excerpt.Length}", name)]);
    }

    private Guid ResolveSource(ToolArguments args)
    {
        Guid item = args.Guid("itemId") ?? throw new ArgumentException("Provide sourceId, or itemId and revision.");
        int revision = args.Integer("revision", 1, 1, int.MaxValue);
        return text.Read(item, revision)?.SourceId ?? throw new ArgumentException("No readable source has that itemId and revision.");
    }

    private async Task<(string Body, string Unit)> ReadAsync(CurrentFileVersion current, string? locator, CancellationToken cancellationToken)
    {
        if (text.ReadCurrent(current.ItemId, current.Revision) is { Outcome: "completed", Content: { } extracted })
        {
            return (extracted, "text");
        }

        if (current.Length > MaximumOriginalBytes)
        {
            throw new InvalidOperationException("The original is too large to open.");
        }

        byte[] bytes = await originals.ReadVerifiedBytesAsync(new OriginalObject(current.Digest, current.Length), MaximumOriginalBytes, cancellationToken);
        if (FinancialDocumentReader.IsPdf(bytes))
        {
            PdfTextResult pages = pdf.Read(bytes, 200);
            if (pages.Lines is null)
            {
                throw new InvalidOperationException($"The PDF could not be read ({pages.ReasonCode}).");
            }

            StringBuilder builder = new();
            foreach (PdfTextLine line in pages.Lines)
            {
                builder.Append('[').Append(line.Page).Append(':').Append(line.Line).Append("] ").Append(line.Text).Append('\n');
            }

            return (builder.ToString(), "pdf_lines");
        }

        return (TextDecoder.Decode(bytes) ?? throw new InvalidOperationException("This source is binary and has no readable text."), "lines");
    }

    /// <summary>Where a <c>time:</c> locator's start is spoken in a transcript, if the source has timing.</summary>
    private int? TimeStart(Guid sourceId, CurrentFileVersion current, string? locator) =>
        TimeLocator.TryParse(locator, out long start, out long _) &&
        moments?.OffsetAt(sourceId, current.ItemId, current.Revision, current.Locator, start) is { } offset
            ? offset
            : null;

    /// <summary>Where a citation locator points inside the opened body, if it can be resolved.</summary>
    private static int? LocatorStart(string body, string? locator, string unit)
    {
        if (locator is null)
        {
            return null;
        }

        string[] parts = locator.Split(':');
        if (parts[0] == "chars" && parts.Length == 2 && int.TryParse(parts[1].Split('-')[0], out int character))
        {
            return Math.Clamp(character - 200, 0, body.Length);
        }

        if (parts[0] == "page" && unit == "pdf_lines" && int.TryParse(parts[1], out int page))
        {
            string marker = parts.Length >= 4 && parts[2] == "line" ? $"[{page}:{parts[3]}]" : $"[{page}:1]";
            int index = body.IndexOf(marker, StringComparison.Ordinal);
            return index < 0 ? null : Math.Max(0, index - 300);
        }

        if (parts[0] is "row" or "line" && parts.Length == 2 && int.TryParse(parts[1], out int line) && line >= 1)
        {
            int index = 0;
            for (int current = 1; current < Math.Max(1, line - 3) && index >= 0; current++)
            {
                index = body.IndexOf('\n', index) is var next and >= 0 ? next + 1 : -1;
            }

            return index < 0 ? null : index;
        }

        return null;
    }
}
