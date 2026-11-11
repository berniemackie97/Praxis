using Praxis.Domain.Library;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>A page of the library.</summary>
public sealed record LibraryResponse(IReadOnlyList<LibraryItem> Items);
