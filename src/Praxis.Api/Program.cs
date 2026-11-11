using System.Globalization;
using System.Net;

const int defaultPort = 5080;
string? configuredPort = Environment.GetEnvironmentVariable("PRAXIS_HEALTH_PORT");
int port = defaultPort;
if (configuredPort is not null &&
    (!int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
     port is < 1024 or > 65535))
{
    throw new InvalidOperationException("PRAXIS_HEALTH_PORT must be a decimal port between 1024 and 65535.");
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server =>
{
    server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
    server.Listen(IPAddress.Loopback, port);
    server.Limits.MaxConcurrentConnections = 64;
    server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Services.AddHealthChecks();

WebApplication app = builder.Build();
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", (IHostApplicationLifetime lifetime) =>
    lifetime.ApplicationStarted.IsCancellationRequested &&
    !lifetime.ApplicationStopping.IsCancellationRequested
        ? Results.Text("Ready", "text/plain")
        : Results.Text("Not ready", "text/plain", statusCode: 503));
app.Run();

namespace Praxis.Api
{
    public partial class Program
    {
    }
}
