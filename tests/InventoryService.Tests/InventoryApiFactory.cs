using InventoryService.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryService.Tests;

public sealed class InventoryApiFactory(string connectionString, ProductStub products) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Services:Product"] = "http://product.test"
            }));
        builder.ConfigureServices(services => services.AddHttpClient<IProductClient, ProductClient>()
            .ConfigurePrimaryHttpMessageHandler(() => products));
    }
}
