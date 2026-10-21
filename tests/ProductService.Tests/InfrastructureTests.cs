using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace ProductService.Tests;

public sealed class InfrastructureTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK, "Healthy")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task HealthReflectsDependencyReadiness(bool ready, HttpStatusCode status, string body)
    {
        await using var factory = new ApiFactory(ready);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SwaggerIsUnavailableOutsideDevelopment()
    {
        await using var factory = new ApiFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/products/lookup")]
    public async Task OversizedRequestsReturn413WithoutExceptionDetails(string path)
    {
        await using var factory = new ApiFactory(true);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(path, new { padding = new string('x', 70000) });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(413, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("REQUEST_TOO_LARGE", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.False(problem.RootElement.TryGetProperty("detail", out _));
        Assert.False(problem.RootElement.TryGetProperty("stackTrace", out _));
    }

    private sealed class ApiFactory(bool ready) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = "Server=localhost;Database=Unused;Integrated Security=True;TrustServerCertificate=True"
                }));
            builder.ConfigureServices(services => services.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration("database",
                    new StubHealthCheck(ready), HealthStatus.Unhealthy, null));
            }));
        }
    }

    private sealed class StubHealthCheck(bool ready) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }
}
