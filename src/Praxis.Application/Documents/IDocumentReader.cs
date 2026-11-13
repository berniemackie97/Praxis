namespace Praxis.Application.Documents;

/// <summary>
/// Turns one family of formats into a <see cref="ReadDocument"/> entirely in code. Readers never
/// execute content, resolve external references or prompt for passwords, and enforce their own
/// size, depth and decompression limits.
/// </summary>
public interface IDocumentReader
{
    /// <summary>Extensions this reader claims, used to reopen sources once a reader exists.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>True when the content signature (preferred) or extension identifies a format this reader handles.</summary>
    bool CanRead(DocumentInput input);

    Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken);
}
