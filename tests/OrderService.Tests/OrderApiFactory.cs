using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Clients;

namespace OrderService.Tests;

public sealed class OrderApiFactory(string connectionString, DependencyStub dependencies) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Services:Product"] = "http://dependencies.test",
                ["Services:Inventory"] = "http://dependencies.test",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<IProductClient, ProductClient>().ConfigurePrimaryHttpMessageHandler(() => dependencies);
            services.AddHttpClient<IInventoryClient, InventoryClient>().ConfigurePrimaryHttpMessageHandler(() => dependencies);
        });
    }
}
