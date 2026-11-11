using Praxis.Domain.Originals;
using Praxis.Infrastructure.FileSystem;

namespace Praxis.Infrastructure.Intake;

/// <summary>Capture contexts for the owner's intake routes, scoped to the canonical spelling of their root.</summary>
internal static class CaptureContexts
{
    internal static CaptureRequestContext SelectedFile(string path) =>
        new("selected_file", "owner_selected", FileSystemPaths.Canonicalize(path));

    internal static CaptureRequestContext SelectedFolder(string root) =>
        new("selected_folder", "owner_selected", FileSystemPaths.Canonicalize(root));

    internal static CaptureRequestContext WatchedFolder(string root) =>
        new("watched_folder", "owner_configured", FileSystemPaths.Canonicalize(root));

    internal static CaptureRequestContext QuickNote(string root) =>
        new("quick_note", "owner_submitted", FileSystemPaths.Canonicalize(root));
}
