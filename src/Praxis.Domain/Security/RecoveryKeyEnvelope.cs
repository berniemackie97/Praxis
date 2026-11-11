namespace Praxis.Domain.Security;

public sealed record RecoveryKeyEnvelope(byte[] Nonce, byte[] CiphertextAndTag, byte[] Signature);
