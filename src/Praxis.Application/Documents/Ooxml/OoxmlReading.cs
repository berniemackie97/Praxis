using System.Xml;

namespace Praxis.Application.Documents.Ooxml;

/// <summary>Maps malformed-package exceptions to bounded reasons for every OOXML reader.</summary>
public static class OoxmlReading
{
    public static ReadOutcome Guard(Func<ReadOutcome> read)
    {
        try
        {
            return read();
        }
        catch (XmlException)
        {
            return ReadOutcome.Unreadable("document_malformed");
        }
        catch (InvalidDataException)
        {
            return ReadOutcome.Unreadable("archive_malformed");
        }
    }

    public static bool IsZip(DocumentInput input) => input.StartsWith([0x50, 0x4b, 0x03, 0x04]);
}
