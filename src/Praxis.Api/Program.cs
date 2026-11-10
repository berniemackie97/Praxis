WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

WebApplication app = builder.Build();
app.MapHealthChecks("/health/live");
app.Run();

namespace Praxis.Api
{
    public partial class Program
    {
    }
}
