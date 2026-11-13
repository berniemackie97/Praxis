using System.IO.Compression;
using System.Xml;

namespace Praxis.Application.Documents.Ooxml;

/// <summary>
/// A bounded view of an Office Open XML package. Entry count, per-entry and total uncompressed
/// size and compression ratio are checked before any part is read; XML parts are read with DTDs
/// prohibited and no resolver, so external entities and entity expansion are impossible.
/// </summary>
public sealed class OoxmlPackage : IDisposable
{
    private readonly ZipArchive archive;
    private readonly Dictionary<string, ZipArchiveEntry> entries;

    private OoxmlPackage(ZipArchive archive)
    {
        this.archive = archive;
        entries = archive.Entries.GroupBy(entry => entry.FullName.TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Opens the package, or returns null with a reason when it is not a safe, well-formed archive.</summary>
    public static OoxmlPackage? Open(byte[] bytes, out string? reasonCode)
    {
        reasonCode = null;
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            reasonCode = "archive_malformed";
            return null;
        }

        long total = 0;
        if (archive.Entries.Count > DocumentLimits.MaximumArchiveEntries)
        {
            reasonCode = "archive_too_many_entries";
        }

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            total += entry.Length;
            if (entry.Length > DocumentLimits.MaximumOriginalBytes || total > DocumentLimits.MaximumArchiveBytes ||
                (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > DocumentLimits.MaximumCompressionRatio))
            {
                reasonCode = "archive_too_large";
                break;
            }
        }

        if (reasonCode is not null)
        {
            archive.Dispose();
            return null;
        }

        return new OoxmlPackage(archive);
    }

    public bool Contains(string part) => entries.ContainsKey(part);

    public IEnumerable<string> Parts(string prefix) =>
        entries.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>A hardened reader over an XML part, or null when the part is missing.</summary>
    public XmlReader? Xml(string part)
    {
        if (!entries.TryGetValue(part.TrimStart('/'), out ZipArchiveEntry? entry))
        {
            return null;
        }

        return XmlReader.Create(entry.Open(), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = DocumentLimits.MaximumOriginalBytes,
            CloseInput = true
        });
    }

    /// <summary>The <c>dc:title</c> of the package's core properties, when present.</summary>
    public string? Title()
    {
        using XmlReader? reader = Xml("docProps/core.xml");
        while (reader is not null && reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "title")
            {
                string title = reader.ReadElementContentAsString().Trim();
                return title.Length == 0 ? null : title;
            }
        }

        return null;
    }

    public void Dispose() => archive.Dispose();
}
