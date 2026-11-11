using System.IO.Compression;

namespace Praxis.Notes.Tests.Archives;

internal static class ArchiveFixture
{
    public static void ReplaceEntry(MemoryStream archive, string name, Func<byte[], byte[]> rewrite)
    {
        archive.Position = 0;
        using (ZipArchive zip = new(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.GetEntry(name) ?? throw new InvalidOperationException("Missing test entry.");
            byte[] original;
            using (Stream payload = entry.Open())
            using (MemoryStream buffer = new())
            {
                payload.CopyTo(buffer);
                original = buffer.ToArray();
            }

            entry.Delete();
            using Stream updated = zip.CreateEntry(name).Open();
            updated.Write(rewrite(original));
        }

        archive.Position = 0;
    }
}
