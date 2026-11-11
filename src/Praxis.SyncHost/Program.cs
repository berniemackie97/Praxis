using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.SyncHost;

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
    throw new InvalidOperationException($"Missing required setting: {name}.");

static string Absolute(string name)
{
    string path = Required(name);
    if (!Path.IsPathFullyQualified(path))
    {
        throw new InvalidOperationException($"{name} must be an absolute path.");
    }

    return Path.GetFullPath(path);
}

static void RequirePrivateFile(string path)
{
    if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
    {
        throw new InvalidOperationException("A required credential file is missing or linked.");
    }

    if (!OperatingSystem.IsWindows())
    {
        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & others) != 0)
        {
            throw new UnauthorizedAccessException("Credential files must be owner-private.");
        }
    }
}

string registryPath = Absolute("PRAXIS_SYNC_IDENTITY_DIR");
string syncPath = Absolute("PRAXIS_SYNC_DATA_DIR");
using SecurityRegistry registry = SecurityRegistry.Open(registryPath);
using OpaqueSyncStore store = OpaqueSyncStore.Open(syncPath);

if (args is ["provision", var spaceText, var epochText] &&
    Guid.TryParse(spaceText, out Guid spaceId) &&
    Guid.TryParse(epochText, out Guid collectionEpoch))
{
    store.Provision(spaceId, collectionEpoch);
    return;
}

if (args.Length != 0)
{
    throw new ArgumentException("Use no arguments to serve, or provision <space-guid> <epoch-guid>.");
}

string serverPath = Absolute("PRAXIS_SYNC_TLS_PFX");
string passwordPath = Absolute("PRAXIS_SYNC_TLS_PASSWORD_FILE");
string rootPath = Absolute("PRAXIS_SYNC_DEVICE_CA");
RequirePrivateFile(serverPath);
RequirePrivateFile(passwordPath);
using X509Certificate2 serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(
    serverPath, File.ReadAllText(passwordPath).TrimEnd('\r', '\n'),
    X509KeyStorageFlags.EphemeralKeySet);
using X509Certificate2 deviceRoot = X509CertificateLoader.LoadCertificateFromFile(rootPath);

string addressSetting = Environment.GetEnvironmentVariable("PRAXIS_SYNC_BIND_IP") ?? "127.0.0.1";
if (!IPAddress.TryParse(addressSetting, out IPAddress? address))
{
    throw new ArgumentException("The sync listener requires a literal IP address.");
}

if (!IPAddress.IsLoopback(address) &&
    Environment.GetEnvironmentVariable("PRAXIS_SYNC_ALLOW_PRIVATE_BIND") != "1")
{
    throw new UnauthorizedAccessException("A non-loopback listener requires explicit operator approval.");
}

string portText = Environment.GetEnvironmentVariable("PRAXIS_SYNC_PORT") ?? "5443";
if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
    port is < 1024 or > 65535)
{
    throw new ArgumentException("PRAXIS_SYNC_PORT must be a decimal port between 1024 and 65535.");
}

await using WebApplication app = PrivateSyncHost.Build(
    address, port, serverCertificate, deviceRoot, registry, store);
await app.RunAsync();
