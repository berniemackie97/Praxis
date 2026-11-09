WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

WebApplication app = builder.Build();
app.MapHealthChecks("/health/live");
app.Run();

public partial class Program
{
}
