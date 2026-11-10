using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Praxis.Api.Tests;

public sealed class LocalCallerBoundaryTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task LivenessDoesNotReturnPrivateMetadata()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("GET", "/v1/library")]
    [InlineData("GET", "/v1/sources/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/mcp")]
    [InlineData("POST", "/v1/captures")]
    [InlineData("POST", "/v1/sources")]
    [InlineData("POST", "/mcp")]
    public async Task PrivateIntegrationRoutesAreNotRegistered(string method, string route)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = new StringContent(
                """{"content":"untrusted input"}""", Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task HostilePreflightDoesNotEnableBrowserCapture()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/v1/captures");
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task SpoofedHostOriginAndTokenDoNotExposeAnUnregisteredSource()
    {
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using HttpRequestMessage request = new(
            HttpMethod.Get, "/v1/sources/00000000-0000-0000-0000-000000000001");
        request.Headers.Host = "attacker.example:5000";
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer invalid");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
