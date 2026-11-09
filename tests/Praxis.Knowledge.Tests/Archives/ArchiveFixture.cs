using System.IO.Compression;

namespace Praxis.Knowledge.Tests;

internal static class ArchiveFixture
{
    public static void ReplaceEntry(MemoryStream archive, string name, Func<byte[], byte[]> rewrite)
    {
        archive.Position = 0;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidOperationException("Missing test entry.");
            byte[] original;
            using (var payload = entry.Open())
            using (var buffer = new MemoryStream())
            {
                payload.CopyTo(buffer);
                original = buffer.ToArray();
            }

            entry.Delete();
            using var updated = zip.CreateEntry(name).Open();
            updated.Write(rewrite(original));
        }

        archive.Position = 0;
    }
}
