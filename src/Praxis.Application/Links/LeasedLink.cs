namespace Praxis.Application.Links;

/// <summary>A saved link whose fetch job is leased: its source revision and the URL it points to.</summary>
public sealed record LeasedLink(Guid SourceId, Guid ItemId, int Revision, string Locator, Uri Url);
