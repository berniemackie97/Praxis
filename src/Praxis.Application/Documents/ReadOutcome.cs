namespace Praxis.Application.Documents;

/// <summary>A reader's result: a document, or the bounded reason none could be produced.</summary>
public sealed record ReadOutcome(ReadDocument? Document, string? ReasonCode)
{
    public static ReadOutcome Read(ReadDocument document) => new(document, null);

    public static ReadOutcome Unreadable(string reasonCode) => new(null, reasonCode);
}
