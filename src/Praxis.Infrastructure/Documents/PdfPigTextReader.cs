using Praxis.Application.Finance.Statements;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace Praxis.Infrastructure.Documents;

/// <summary>
/// Reconstructs visual text lines from PDF word positions with PdfPig (Apache-2.0, pure .NET).
/// Words sharing a baseline become one line in left-to-right order; lines are ordered top to
/// bottom. Encrypted, malformed, oversized and image-only documents fail with a bounded reason;
/// PdfPig does not execute scripts or fetch external content.
/// </summary>
public sealed class PdfPigTextReader : IPdfTextReader
{
    private const int MaximumLineCharacters = 1_024;
    private const int MaximumLines = 200_000;

    public PdfTextResult Read(byte[] document, int maximumPages)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            using PdfDocument pdf = PdfDocument.Open(document, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            if (pdf.NumberOfPages > maximumPages)
            {
                return PdfTextResult.Failed("pdf_too_many_pages");
            }

            List<PdfTextLine> lines = [];
            for (int pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                Page page = pdf.GetPage(pageNumber);
                int lineNumber = 0;
                foreach (string text in Lines(page.GetWords()))
                {
                    lines.Add(new PdfTextLine(pageNumber, ++lineNumber, text.Length <= MaximumLineCharacters ? text : text[..MaximumLineCharacters]));
                    if (lines.Count > MaximumLines)
                    {
                        return PdfTextResult.Failed("pdf_too_much_text");
                    }
                }
            }

            return lines.Count == 0 ? PdfTextResult.Failed("pdf_no_text") : PdfTextResult.Read(lines);
        }
        catch (PdfDocumentEncryptedException)
        {
            return PdfTextResult.Failed("pdf_encrypted");
        }
        catch (PdfDocumentFormatException)
        {
            return PdfTextResult.Failed("pdf_malformed");
        }
        catch (InvalidOperationException)
        {
            return PdfTextResult.Failed("pdf_malformed");
        }
        catch (ArgumentException)
        {
            return PdfTextResult.Failed("pdf_malformed");
        }
        catch (IndexOutOfRangeException)
        {
            return PdfTextResult.Failed("pdf_malformed");
        }
    }

    private static IEnumerable<string> Lines(IEnumerable<Word> words)
    {
        List<List<Word>> rows = [];
        foreach (Word word in words.Where(word => !string.IsNullOrWhiteSpace(word.Text))
                     .OrderByDescending(word => word.BoundingBox.Bottom).ThenBy(word => word.BoundingBox.Left))
        {
            List<Word>? row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= Tolerance(row[0], word))
            {
                row.Add(word);
            }
            else
            {
                rows.Add([word]);
            }
        }

        return rows.Select(row => string.Join(' ', row.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text.Trim())));
    }

    private static double Tolerance(Word first, Word next) =>
        Math.Max(1.0, 0.4 * Math.Min(Math.Abs(first.BoundingBox.Height), Math.Abs(next.BoundingBox.Height)));
}
