using System.Security;
using System.Text;
using Praxis.Application.Links;
using Praxis.Application.Originals;

namespace Praxis.Infrastructure.Links;

/// <summary>
/// Saves a link the owner asked for as a standard <c>.webloc</c> file in "Praxis Links" inside the
/// primary intake folder, so it is a real file the owner can open in a browser, and captures it.
/// </summary>
public sealed class SavedLinkWriter(IOriginalStore originals, string catalogDirectory, string intakeDirectory, Action? ensureInboxApproved = null) : ISavedLinkWriter
{
    public const string FolderName = "Praxis Links";
    private readonly PraxisFolderWriter writer = new(originals, catalogDirectory, intakeDirectory, FolderName, ensureInboxApproved);

    public Task<string> SaveAsync(Uri url, string? title, CancellationToken cancellationToken)
    {
        if (SavedLink.Parse(url.AbsoluteUri) is null)
        {
            throw new ArgumentException("Only http and https links without credentials can be saved.", nameof(url));
        }

        string plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>URL</key>
            	<string>{SecurityElement.Escape(url.AbsoluteUri)}</string>
            </dict>
            </plist>

            """;
        string name = title is { Length: > 0 } ? title : url.Host + url.AbsolutePath.TrimEnd('/');
        return writer.WriteAsync(name, ".webloc", Encoding.UTF8.GetBytes(plist), cancellationToken);
    }
}
