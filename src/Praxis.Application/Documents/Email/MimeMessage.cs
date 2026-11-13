namespace Praxis.Application.Documents.Email;

/// <summary>The headers that identify a message and its decoded leaf parts in order.</summary>
public sealed record MimeMessage(IReadOnlyDictionary<string, string> Headers, IReadOnlyList<MimePart> Parts)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}
