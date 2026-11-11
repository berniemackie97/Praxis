using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed class OpaqueSyncGateway(SecurityRegistry registry, OpaqueSyncStore store)
{
    public OpaqueSyncReceipt Submit(VerifiedCertificateIdentity session,
        VerifiedDeviceProof proof, OpaqueSyncMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (proof.DeviceId != mutation.DeviceId || proof.SpaceId != mutation.SpaceId)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        return registry.ProtectedData.ExecuteSpace(session, proof, SecurityPermission.Write,
            mutation.ContentEpoch, scope => store.Submit(scope, mutation));
    }

    public OpaqueSyncPage Pull(VerifiedCertificateIdentity session,
        VerifiedDeviceProof proof, OpaqueSyncPull request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (proof.DeviceId != request.DeviceId || proof.SpaceId != request.SpaceId)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        return registry.ProtectedData.ExecuteSpace(session, proof, SecurityPermission.Read,
            request.ContentEpoch, scope => store.Pull(scope, request));
    }
}
