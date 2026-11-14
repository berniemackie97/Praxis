using System.Text;
using Praxis.Application.Documents.Html;
using Praxis.Application.Processing;
using Praxis.Domain.Processing;

namespace Praxis.Application.Links;

/// <summary>
/// The link stage: for a link the owner saved, fetch what it points to through the safe fetcher (or
/// the video resolver for online videos) and capture it as a snapshot source in "Praxis Web", which
/// the generic intake then reads, triages and understands like any document.
/// </summary>
public sealed class LinkFetchHandler(ILinkRepository links, ILinkFetcher fetcher, ILinkSnapshotWriter? snapshots, IReadOnlyList<IVideoLinkResolver> videos,
    TimeProvider clock) : IProcessingHandler
{
    private const int MaximumSnapshotCharacters = 2 * 1024 * 1024;

    public string ProcessorKey => ProcessorKeys.LinkFetch;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
    public int MaxConcurrency => 2;

    public bool TryReopenWork() => false;

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        if (links.ReadLeased(lease) is not { } link)
        {
            return;
        }

        if (snapshots is null)
        {
            links.Close(lease, "no_intake_folder");
            return;
        }

        LinkSettings settings = links.ReadSettings();
        if (settings.Mode == LinkSettings.Off)
        {
            links.Close(lease, "links_off");
            return;
        }

        if (settings.Blocks(link.Url))
        {
            links.Close(lease, "host_blocked");
            return;
        }

        string name = Path.GetFileNameWithoutExtension(link.Locator);
        if (videos.FirstOrDefault(resolver => resolver.Handles(link.Url)) is { } video)
        {
            WebVideoSnapshot? snapshot = await video.ResolveAsync(link.Url, cancellationToken);
            if (snapshot is null)
            {
                links.Close(lease, "video_unavailable");
                return;
            }

            byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot);
            string written = await snapshots.WriteAsync(snapshot.Title.Length > 0 ? snapshot.Title : name, WebVideoSnapshot.Extension, json, cancellationToken);
            links.Complete(lease, link.Url, 200, "video", written);
            return;
        }

        FetchedPage page = await fetcher.FetchAsync(link.Url, cancellationToken);
        if (page is not { Body: { } body, FinalUrl: { } finalUrl, ContentType: { } contentType })
        {
            links.Close(lease, page.ReasonCode ?? "fetch_failed", page.Status);
            return;
        }

        (string extension, byte[] content, string title) = Snapshot(finalUrl, contentType, body, name);
        string path = await snapshots.WriteAsync(title, extension, content, cancellationToken);
        links.Complete(lease, finalUrl, page.Status, contentType, path);
    }

    /// <summary>
    /// The snapshot file for a fetched resource: readable Markdown for pages (title, address, fetch
    /// time, page text), the bytes as fetched for PDFs and images.
    /// </summary>
    private (string Extension, byte[] Content, string Title) Snapshot(Uri url, string contentType, byte[] body, string fallbackTitle)
    {
        string type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        switch (type)
        {
            case "application/pdf":
                return (".pdf", body, fallbackTitle);
            case "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/heic":
                return ("." + type[6..].Replace("jpeg", "jpg", StringComparison.Ordinal), body, fallbackTitle);
        }

        string raw = Encoding.UTF8.GetString(body);
        bool html = type is "text/html" or "application/xhtml+xml";
        string text = html ? HtmlText.Convert(raw) : raw;
        string title = (html ? HtmlText.Title(raw) : null) is { Length: > 0 } pageTitle ? pageTitle : fallbackTitle;
        if (text.Length > MaximumSnapshotCharacters)
        {
            text = text[..MaximumSnapshotCharacters];
        }

        string fetched = clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        string markdown = $"# {title}\n\nSource: {url.AbsoluteUri}\nSaved: {fetched}\n\n{(type is "application/json" ? "```json\n" + text + "\n```" : text)}\n";
        return (".md", Encoding.UTF8.GetBytes(markdown), title);
    }
}
