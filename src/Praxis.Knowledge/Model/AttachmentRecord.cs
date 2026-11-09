using System.Security.Cryptography;

namespace Praxis.Knowledge;

public sealed class AttachmentRecord
{
    private readonly byte[] content;

    private AttachmentRecord(Guid id, string path, byte[] content)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An attachment must have an identity.", nameof(id));
        }

        PortablePath.Validate(path);
        Id = id;
        Path = path;
        this.content = content;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public Guid Id { get; }

    public string Path { get; }

    public int Length => content.Length;

    public string Sha256 { get; }

    public byte[] ReadContent() => (byte[])content.Clone();

    public static AttachmentRecord Create(string path, ReadOnlySpan<byte> content)
        => new(Guid.NewGuid(), path, content.ToArray());

    public AttachmentRecord Rename(string path)
        => new(Id, path, content);

    internal ReadOnlySpan<byte> ContentSpan => content;

    internal static AttachmentRecord Restore(Guid id, string path, byte[] content)
        => new(id, path, content);
}
