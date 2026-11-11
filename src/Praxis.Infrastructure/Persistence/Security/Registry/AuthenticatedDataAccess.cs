using Microsoft.Data.Sqlite;
using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

/// <summary>
/// Synchronous execution fence for future private-data transport. Caller supplies
/// a transactional data operation; no private HTTP endpoint is created here.
/// </summary>
public sealed class AuthenticatedDataAccess(SecurityRegistry registry)
{
    public T Execute<T>(VerifiedCertificateIdentity session, VerifiedDeviceProof proof,
        Guid sourceId, SecurityPermission permission, long contentEpoch, Func<AuthorizedDataScope, T> operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(operation);
        string source = SecurityRegistry.Id(sourceId);
        if (permission is not (SecurityPermission.Read or SecurityPermission.Write) ||
            contentEpoch < 1 || typeof(System.Threading.Tasks.Task).IsAssignableFrom(typeof(T)) ||
            typeof(T) == typeof(System.Threading.Tasks.ValueTask) ||
            (typeof(T).IsGenericType &&
             typeof(T).GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>)))
        {
            throw new ArgumentException("Private data operations must be synchronous and scoped.");
        }

        return registry.Execute(connection =>
        {
            if (proof.SourceId != sourceId || proof.Permission != permission ||
                !registry.Certificates.MatchesOperation(session, proof))
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            using SqliteCommand scope = SecurityRegistry.Command(connection, null, """
                SELECT s.processing_class, s.rekey_required,
                    (SELECT COALESCE(MAX(e.epoch), 0)
                     FROM space_key_epochs e WHERE e.space_id=s.space_id)
                FROM security_spaces s
                JOIN source_spaces x ON x.space_id=s.space_id
                WHERE x.source_id=$source AND s.space_id=$space
                """,
                ("$source", source), ("$space", SecurityRegistry.Id(proof.SpaceId)));
            using SqliteDataReader reader = scope.ExecuteReader();
            if (!reader.Read())
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            string processingClass = reader.GetString(0);
            bool rekeyRequired = reader.GetInt64(1) != 0;
            long currentEpoch = reader.GetInt64(2);
            reader.Close();

            // Trusted-host plaintext processing needs its own explicit key-custody
            // contract; never silently treat it as client-held ciphertext.
            if (processingClass != "client-held" ||
                rekeyRequired || currentEpoch != contentEpoch)
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            // Hold the registry lease/gate throughout the caller's synchronous,
            // transactional data operation. Revocation cannot interleave here.
            return operation(new AuthorizedDataScope(session.PrincipalId, session.DeviceId,
                proof.SpaceId, sourceId, permission, contentEpoch));
        });
    }
}
