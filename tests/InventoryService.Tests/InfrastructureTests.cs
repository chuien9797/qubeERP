using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace InventoryService.Tests;

public sealed class InfrastructureTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK, "Healthy")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task HealthReflectsDependencyReadiness(bool ready, HttpStatusCode status, string body)
    {
        await using var factory = new ApiFactory(ready);
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow;
        using var response = await client.GetAsync("/health");
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        var malaysiaTime = DateTimeOffset.Parse(Assert.Single(response.Headers.GetValues("X-Malaysia-Time")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(8), malaysiaTime.Offset);
        Assert.InRange(malaysiaTime.ToUniversalTime(), before, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/request-builder.js")]
    [InlineData("/swagger/request-builder.css")]
    [InlineData("/swagger/products")]
    public async Task SwaggerIsUnavailableOutsideDevelopment(string path)
    {
        await using var factory = new ApiFactory(true);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
