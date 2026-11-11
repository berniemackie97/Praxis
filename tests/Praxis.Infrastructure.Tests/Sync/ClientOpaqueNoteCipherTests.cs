using System.Security.Cryptography;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;
using Xunit;

namespace Praxis.Infrastructure.Tests.Sync;

public sealed class ClientOpaqueNoteCipherTests
{
    [Fact]
    public void CiphertextRoundTripsOnlyInsideExactSpaceAndRevision()
    {
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        Guid space = Guid.NewGuid();
        Guid collectionEpoch = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        OpaqueNoteContent original = new("notes/hello.md", "# Hello\nSome private text.");
        byte[] encrypted = ClientOpaqueNoteCipher.Encrypt(key, space, collectionEpoch,
            record, 1, OpaqueSyncAction.Upsert, 1, original);

        Assert.True(encrypted.Length > 29);
        Assert.DoesNotContain("Some private text", Convert.ToBase64String(encrypted));
        Assert.Equal(original, ClientOpaqueNoteCipher.Decrypt(key, space, collectionEpoch,
            record, 1, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, Guid.NewGuid(), collectionEpoch, record, 1, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, Guid.NewGuid(), record, 1, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, collectionEpoch, Guid.NewGuid(), 1, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, collectionEpoch, record, 2, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, collectionEpoch, record, 1, OpaqueSyncAction.Restore, 1, encrypted));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, collectionEpoch, record, 1, OpaqueSyncAction.Upsert, 2, encrypted));
    }

    [Fact]
    public void DamagedNonceTagCiphertextAndIncorrectKeyAreRejected()
    {
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        Guid space = Guid.NewGuid();
        Guid collectionEpoch = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        byte[] encrypted = ClientOpaqueNoteCipher.Encrypt(key, space, collectionEpoch,
            record, 1, OpaqueSyncAction.Upsert, 1, new("notes/a.md", "A"));
        foreach (int position in new[] { 1, 13, 29 })
        {
            byte[] damaged = encrypted.ToArray();
            damaged[position] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
                key, space, collectionEpoch, record, 1, OpaqueSyncAction.Upsert, 1, damaged));
        }

        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            ClientSpaceKeys.GenerateContentKey(), space, collectionEpoch,
            record, 1, OpaqueSyncAction.Upsert, 1, encrypted));
        Assert.Throws<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, collectionEpoch, record, 1, OpaqueSyncAction.Upsert, 1, encrypted[..15]));
        Assert.Throws<ArgumentException>(() => ClientOpaqueNoteCipher.Encrypt(
            key, space, collectionEpoch, record, 1, OpaqueSyncAction.Delete, 1,
            new("a.md", "A")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientOpaqueNoteCipher.Encrypt(
            key, space, collectionEpoch, record, 1, OpaqueSyncAction.Upsert, 1,
            new("a.md", new string('x', 65536))));
    }
}
