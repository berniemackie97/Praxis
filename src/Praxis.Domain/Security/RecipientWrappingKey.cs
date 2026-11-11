namespace Praxis.Domain.Security;

public sealed record RecipientWrappingKey(Guid DeviceId, byte[] SigningPublicKey, byte[] PublicKey, byte[] BindingSignature);
