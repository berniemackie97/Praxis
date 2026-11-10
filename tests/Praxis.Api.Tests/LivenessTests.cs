using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Praxis.Api.Tests;

public sealed class LivenessTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task EndpointReturnsOk()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
