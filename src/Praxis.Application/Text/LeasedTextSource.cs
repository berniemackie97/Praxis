using Praxis.Domain.Originals;

namespace Praxis.Application.Text;

/// <summary>The source and verified original behind a leased text extraction job.</summary>
public sealed record LeasedTextSource(string SourceKind, string Locator, OriginalObject Original);
