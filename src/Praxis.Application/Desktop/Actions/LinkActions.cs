using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Links;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Saved links for first-party clients: the owner's fetch settings and saving a link the owner typed.</summary>
public sealed class LinkActions(ILinkRepository links, ISavedLinkWriter writer) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("linkSettings", _ => new LinkSettingsResponse(links.ReadSettings())),
        DesktopAction.Of("setLinkSettings", fields =>
        {
            string mode = fields.RequireString("mode", 16);
            if (!LinkSettings.IsValidMode(mode))
            {
                throw new ArgumentException("The link mode must be off or saved_links.");
            }

            LinkSettings settings = new(mode, fields.OptionalStrings("blockedHosts", 253, LinkSettings.MaximumBlockedHosts)
                .Select(host => host.Trim().ToLowerInvariant()).Where(host => host.Length > 0).Distinct(StringComparer.Ordinal).ToArray(),
                fields.OptionalBoolean("studyMedia") ?? links.ReadSettings().StudyMedia,
                fields.OptionalBoolean("sampleFrames") ?? links.ReadSettings().SampleFrames,
                fields.OptionalBoolean("clearVisionModel") == true ? null : fields.OptionalText("visionModel", 100, "The vision model is invalid.") ?? links.ReadSettings().VisionModel,
                fields.IntegerOrDefault("maximumMediaMinutes", links.ReadSettings().MaximumMediaMinutes, 1, 240));
            links.SaveSettings(settings);
            return new LinkSettingsResponse(links.ReadSettings());
        }),
        DesktopAction.OfAsync("saveLink", async (fields, cancellationToken) =>
        {
            Uri url = SavedLink.Parse(fields.RequireString("url", SavedLink.MaximumUrlLength).Trim())
                      ?? throw new ArgumentException("Enter an http or https address without a user name or password.");
            string path = await writer.SaveAsync(url, fields.OptionalText("title", 120, "The title is too long."), cancellationToken);
            return new SavedLinkResponse(url.AbsoluteUri, Path.GetFileName(path));
        })
    ];
}
