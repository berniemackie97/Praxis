namespace Praxis.Infrastructure.FileSystem;

/// <summary>
/// Files macOS writes into any folder the owner opens in Finder or copies across volumes
/// (<c>.DS_Store</c>, AppleDouble <c>._name</c> files, <c>.localized</c>, custom folder icons). They
/// carry no catalog data, so private Praxis folders tolerate and ignore them instead of treating
/// them as tampering. Links and directories with these names are never benign.
/// </summary>
internal static class FinderMetadata
{
    internal static bool IsBenign(string path)
    {
        string name = Path.GetFileName(path);
        if (!(name is ".DS_Store" or ".localized" or "Icon\r" || (name.StartsWith("._", StringComparison.Ordinal) && name.Length > 2)))
        {
            return false;
        }

        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    }
}
