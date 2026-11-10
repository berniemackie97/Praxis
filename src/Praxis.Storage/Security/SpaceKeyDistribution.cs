using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

/// <summary>
/// Stores only owner-signed opaque space-key envelopes. Every operation requires a
/// current signed device proof; the operator cannot read or mint client content keys.
/// </summary>
public sealed class SpaceKeyDistribution(SecurityRegistry registry)
{
    public IReadOnlyList<RecipientWrappingKey> Recipients(VerifiedDeviceProof ownerProof)
    {
        RequireSpaceProof(ownerProof, SecurityPermission.Write);
        return registry.Execute(connection =>
        {
            RequireCurrent(ownerProof);
            using SqliteCommand owner = SecurityRegistry.Command(connection, null,
                "SELECT owner_id FROM security_spaces WHERE space_id=$space",
                ("$space", SecurityRegistry.Id(ownerProof.SpaceId)));
            if (!string.Equals(owner.ExecuteScalar() as string,
                SecurityRegistry.Id(ownerProof.PrincipalId), StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Only the space owner can distribute key material.");
            }

            return LoadRecipients(connection, null, ownerProof.SpaceId);
        });
    }

    public void Publish(VerifiedDeviceProof ownerProof, SpaceKeyPublication publication)
    {
        RequireSpaceProof(ownerProof, SecurityPermission.Write);
        ArgumentNullException.ThrowIfNull(publication);
        SpaceKeyPublication signed = Snapshot(publication);
        if (signed.SpaceId != ownerProof.SpaceId ||
            signed.SignerDeviceId != ownerProof.DeviceId ||
            signed.Epoch < 1 || signed.KeyCommitment.Length != 32 ||
            signed.Recovery.Nonce.Length != 12 ||
            signed.Recovery.CiphertextAndTag.Length != 48 ||
            signed.Recovery.Signature.Length != 64 ||
            signed.Recipients.Count is < 1 or > 64)
        {
            throw new ArgumentException("The space key publication is invalid.", nameof(publication));
        }

        registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            RequireCurrent(ownerProof);
            byte[] signer = LoadOwnerSigningKey(connection, transaction, ownerProof);
            IReadOnlyList<RecipientWrappingKey> expected =
                LoadRecipients(connection, transaction, signed.SpaceId);
            ValidateEnvelopes(signed, expected, signer);

            using (SqliteCommand epochCheck = SecurityRegistry.Command(connection, transaction,
                "SELECT COALESCE(MAX(epoch), 0) FROM space_key_epochs WHERE space_id=$space",
                ("$space", SecurityRegistry.Id(signed.SpaceId))))
            {
                if (signed.Epoch != checked(Convert.ToInt64(epochCheck.ExecuteScalar()) + 1))
                {
                    throw new InvalidOperationException("A key epoch cannot be reused or skipped.");
                }
            }

            using (SqliteCommand save = SecurityRegistry.Command(connection, transaction, """
                INSERT INTO space_key_epochs
                    (space_id, epoch, commitment, signer_device_id, recovery_nonce,
                     recovery_ciphertext, recovery_signature, created_utc)
                VALUES ($space, $epoch, $commit, $signer, $nonce, $cipher, $signature, $now)
                """,
                ("$space", SecurityRegistry.Id(signed.SpaceId)),
                ("$epoch", signed.Epoch), ("$commit", signed.KeyCommitment),
                ("$signer", SecurityRegistry.Id(signed.SignerDeviceId)),
                ("$nonce", signed.Recovery.Nonce),
                ("$cipher", signed.Recovery.CiphertextAndTag),
                ("$signature", signed.Recovery.Signature),
                ("$now", registry.UtcNow.UtcDateTime.ToString("O"))))
            {
                save.ExecuteNonQuery();
            }

            foreach (DeviceKeyEnvelope envelope in signed.Recipients)
            {
                using SqliteCommand save = SecurityRegistry.Command(connection, transaction, """
                    INSERT INTO device_key_envelopes
                        (space_id, epoch, device_id, wrapped_key, signature)
                    VALUES ($space, $epoch, $device, $key, $signature)
                    """,
                    ("$space", SecurityRegistry.Id(signed.SpaceId)),
                    ("$epoch", signed.Epoch),
                    ("$device", SecurityRegistry.Id(envelope.DeviceId)),
                    ("$key", envelope.WrappedKey), ("$signature", envelope.Signature));
                save.ExecuteNonQuery();
            }

            using (SqliteCommand complete = SecurityRegistry.Command(connection, transaction,
                "UPDATE security_spaces SET rekey_required=0 WHERE space_id=$space",
                ("$space", SecurityRegistry.Id(signed.SpaceId))))
            {
                if (complete.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("The security space is missing.");
                }
            }

            transaction.Commit();
            return 0;
        });
    }

    public AvailableSpaceKey? Download(VerifiedDeviceProof proof)
    {
        RequireSpaceProof(proof, SecurityPermission.Read);
        return registry.Execute(connection =>
        {
            RequireCurrent(proof);
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                SELECT e.epoch, e.signer_device_id, e.commitment, e.recovery_nonce,
                       e.recovery_ciphertext, e.recovery_signature, k.wrapped_key,
                       k.signature, s.owner_id
                FROM space_key_epochs e
                JOIN security_spaces s ON s.space_id=e.space_id AND s.rekey_required=0
                JOIN device_key_envelopes k ON k.space_id=e.space_id AND k.epoch=e.epoch
                                          AND k.device_id=$device
                WHERE e.space_id=$space
                ORDER BY e.epoch DESC LIMIT 1
                """,
                ("$space", SecurityRegistry.Id(proof.SpaceId)),
                ("$device", SecurityRegistry.Id(proof.DeviceId)));
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            bool isOwner = string.Equals(reader.GetString(8),
                SecurityRegistry.Id(proof.PrincipalId), StringComparison.Ordinal);
            return new AvailableSpaceKey(
                proof.SpaceId, reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                (byte[])reader.GetValue(2),
                new DeviceKeyEnvelope(proof.DeviceId,
                    (byte[])reader.GetValue(6), (byte[])reader.GetValue(7)),
                isOwner ? new RecoveryKeyEnvelope(
                    (byte[])reader.GetValue(3), (byte[])reader.GetValue(4),
                    (byte[])reader.GetValue(5)) : null);
        });
    }

    private void RequireCurrent(VerifiedDeviceProof proof)
    {
        if (!registry.DeviceProofs.IsCurrent(proof))
        {
            throw new UnauthorizedAccessException("The device proof is no longer valid.");
        }
    }

    private static void RequireSpaceProof(VerifiedDeviceProof proof, SecurityPermission permission)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.SourceId is not null || proof.Permission != permission)
        {
            throw new UnauthorizedAccessException("A space-scoped device proof is required.");
        }
    }

    private static byte[] LoadOwnerSigningKey(SqliteConnection connection,
        SqliteTransaction transaction, VerifiedDeviceProof proof)
    {
        using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
            SELECT d.public_key FROM devices d
            JOIN security_spaces s ON s.space_id=$space AND s.owner_id=d.principal_id
            WHERE d.device_id=$device AND d.revoked_utc IS NULL
              AND s.owner_id=$principal AND s.processing_class IN ('client-held','trusted-host')
            """,
            ("$space", SecurityRegistry.Id(proof.SpaceId)),
            ("$device", SecurityRegistry.Id(proof.DeviceId)),
            ("$principal", SecurityRegistry.Id(proof.PrincipalId)));
        return command.ExecuteScalar() as byte[] ??
            throw new UnauthorizedAccessException("Only the enrolled owner can sign a space key epoch.");
    }

    private static IReadOnlyList<RecipientWrappingKey> LoadRecipients(SqliteConnection connection,
        SqliteTransaction? transaction, Guid spaceId)
    {
        using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
            SELECT d.device_id, d.public_key, d.wrapping_public_key, d.wrapping_signature
            FROM devices d
            JOIN principals p ON p.principal_id=d.principal_id AND p.disabled_utc IS NULL
            JOIN security_spaces s ON s.space_id=$space
            LEFT JOIN space_grants g ON g.space_id=s.space_id AND g.principal_id=p.principal_id
            WHERE d.revoked_utc IS NULL
              AND (s.owner_id=p.principal_id OR
                   (g.revoked_utc IS NULL AND (g.permissions & 1)=1))
            ORDER BY d.device_id LIMIT 65
            """, ("$space", SecurityRegistry.Id(spaceId)));
        using SqliteDataReader reader = command.ExecuteReader();
        List<RecipientWrappingKey> recipients = new();
        while (reader.Read())
        {
            if (reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                throw new InvalidOperationException("An authorized device has no attested wrapping key.");
            }

            byte[] signing = (byte[])reader.GetValue(1);
            byte[] wrapping = (byte[])reader.GetValue(2);
            byte[] binding = (byte[])reader.GetValue(3);
            if (!DeviceWrappingKey.Verify(signing, wrapping, binding))
            {
                throw new InvalidDataException("An enrolled device wrapping key is not authenticated.");
            }

            recipients.Add(new RecipientWrappingKey(
                Guid.Parse(reader.GetString(0)), signing, wrapping, binding));
        }

        if (recipients.Count is < 1 or > 64)
        {
            throw new InvalidOperationException("A space requires one to sixty-four authorized devices.");
        }

        return recipients;
    }

    private void ValidateEnvelopes(SpaceKeyPublication signed,
        IReadOnlyList<RecipientWrappingKey> expected, byte[] signerPublicKey)
    {
        if (signed.Recipients.Count != expected.Count)
        {
            throw new UnauthorizedAccessException("The key distribution omits an authorized device.");
        }

        using ECDsa signer = ECDsa.Create();
        signer.ImportSubjectPublicKeyInfo(signerPublicKey, out int bytesRead);
        if (bytesRead != signerPublicKey.Length || signer.KeySize != 256)
        {
            throw new InvalidDataException("The enrollment signer key is invalid.");
        }

        HashSet<Guid> recipients = new();
        HashSet<Guid> allowed = expected.Select(x => x.DeviceId).ToHashSet();
        foreach (DeviceKeyEnvelope envelope in signed.Recipients)
        {
            if (!recipients.Add(envelope.DeviceId) || !allowed.Contains(envelope.DeviceId) ||
                envelope.WrappedKey.Length != 384 || envelope.Signature.Length != 64 ||
                !ClientSpaceKeys.Verify(signer, ClientSpaceKeys.DeviceMessage(
                    registry.InstallationId, signed.SpaceId, signed.Epoch,
                    signed.SignerDeviceId, envelope.DeviceId, signed.KeyCommitment,
                    envelope.WrappedKey), envelope.Signature))
            {
                throw new UnauthorizedAccessException("A device key envelope is not authorized.");
            }
        }

        if (!ClientSpaceKeys.Verify(signer, ClientSpaceKeys.RecoveryMessage(
            registry.InstallationId, signed.SpaceId, signed.Epoch, signed.SignerDeviceId,
            signed.KeyCommitment, signed.Recovery.Nonce,
            signed.Recovery.CiphertextAndTag), signed.Recovery.Signature))
        {
            throw new UnauthorizedAccessException("The owner recovery envelope is not authenticated.");
        }
    }

    private static SpaceKeyPublication Snapshot(SpaceKeyPublication value) =>
        new(value.SpaceId, value.Epoch, value.SignerDeviceId, value.KeyCommitment.ToArray(),
            value.Recipients.Select(x => new DeviceKeyEnvelope(x.DeviceId,
                x.WrappedKey.ToArray(), x.Signature.ToArray())).ToArray(),
            new RecoveryKeyEnvelope(value.Recovery.Nonce.ToArray(),
                value.Recovery.CiphertextAndTag.ToArray(), value.Recovery.Signature.ToArray()));
}
