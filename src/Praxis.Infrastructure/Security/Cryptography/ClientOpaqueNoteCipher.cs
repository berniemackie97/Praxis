using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxis.Infrastructure.Persistence.Sync;

namespace Praxis.Infrastructure.Security.Cryptography;

public sealed record OpaqueNoteContent(string Path, string Markdown);

public static class ClientOpaqueNoteCipher
{
    private const byte Version = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumPayload = 65536;

    public static byte[] Encrypt(ReadOnlySpan<byte> contentKey, Guid spaceId,
        Guid collectionEpoch, Guid recordId, long revision, OpaqueSyncAction action,
        long keyEpoch, OpaqueNoteContent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        Validate(contentKey, spaceId, collectionEpoch, recordId, revision, action, keyEpoch);
        if (string.IsNullOrWhiteSpace(note.Path) || note.Markdown is null)
        {
            throw new ArgumentException("An authenticated note must contain a path and text.");
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(note);
        try
        {
            if (plaintext.Length > MaximumPayload - (1 + NonceLength + TagLength))
            {
                throw new ArgumentOutOfRangeException(nameof(note), "The encrypted note is too large.");
            }

            byte[] payload = new byte[1 + NonceLength + TagLength + plaintext.Length];
            payload[0] = Version;
            RandomNumberGenerator.Fill(payload.AsSpan(1, NonceLength));
            using AesGcm aes = new(contentKey, TagLength);
            aes.Encrypt(payload.AsSpan(1, NonceLength), plaintext,
                payload.AsSpan(1 + NonceLength + TagLength),
                payload.AsSpan(1 + NonceLength, TagLength),
                Context(spaceId, collectionEpoch, recordId, revision, action, keyEpoch));
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static OpaqueNoteContent Decrypt(ReadOnlySpan<byte> contentKey, Guid spaceId,
        Guid collectionEpoch, Guid recordId, long revision, OpaqueSyncAction action,
        long keyEpoch, ReadOnlySpan<byte> payload)
    {
        Validate(contentKey, spaceId, collectionEpoch, recordId, revision, action, keyEpoch);
        if (payload.Length is < (1 + NonceLength + TagLength) or > MaximumPayload ||
            payload[0] != Version)
        {
            throw new CryptographicException("The encrypted note envelope is invalid.");
        }

        byte[] plaintext = new byte[payload.Length - (1 + NonceLength + TagLength)];
        try
        {
            using AesGcm aes = new(contentKey, TagLength);
            aes.Decrypt(payload.Slice(1, NonceLength),
                payload.Slice(1 + NonceLength + TagLength),
                payload.Slice(1 + NonceLength, TagLength), plaintext,
                Context(spaceId, collectionEpoch, recordId, revision, action, keyEpoch));
            OpaqueNoteContent? note = JsonSerializer.Deserialize<OpaqueNoteContent>(plaintext);
            if (note is null || string.IsNullOrWhiteSpace(note.Path) || note.Markdown is null)
            {
                throw new CryptographicException("The decrypted note is invalid.");
            }

            return note;
        }
        catch (JsonException error)
        {
            throw new CryptographicException("The decrypted note is invalid.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(ReadOnlySpan<byte> key, Guid space, Guid epoch,
        Guid record, long revision, OpaqueSyncAction action, long keyEpoch)
    {
        if (key.Length != 32 || space == Guid.Empty || epoch == Guid.Empty ||
            record == Guid.Empty || revision < 1 || keyEpoch < 1 ||
            action is not (OpaqueSyncAction.Upsert or OpaqueSyncAction.Restore))
        {
            throw new ArgumentException("The encrypted note scope is invalid.");
        }
    }

    private static byte[] Context(Guid space, Guid epoch, Guid record,
        long revision, OpaqueSyncAction action, long keyEpoch) =>
        Encoding.ASCII.GetBytes("Praxis.OpaqueNote.v1\n" +
            $"{space:N}\n{epoch:N}\n{record:N}\n" +
            $"{revision.ToString(CultureInfo.InvariantCulture)}\n{(int)action}\n" +
            $"{keyEpoch.ToString(CultureInfo.InvariantCulture)}\n");
}
