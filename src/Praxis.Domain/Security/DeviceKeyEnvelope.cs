namespace Praxis.Domain.Security;

public sealed record DeviceKeyEnvelope(Guid DeviceId, byte[] WrappedKey, byte[] Signature);
