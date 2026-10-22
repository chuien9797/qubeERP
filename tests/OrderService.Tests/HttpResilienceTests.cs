using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using OrderService.Clients;
using QubeERP.HttpCommunication;
using Shared.Contracts.Inventory;
using Shared.Contracts.Products;
using Xunit;

namespace OrderService.Tests;

public sealed class HttpResilienceTests
{
    [Theory]
    [InlineData("500")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    [InlineData("408")]
    [InlineData("disconnect")]
    [InlineData("timeout")]
    public async Task ReadOnlyProductLookupRetriesAndPreservesPayload(string failure)
    {
        using var dependencies = new DependencyStub();
        var id = dependencies.AddProduct();
        var calls = 0;
        dependencies.ProductResponse = async (request, token) =>
        {
            var body = (await request.Content!.ReadFromJsonAsync<ProductLookupRequest>(token))!;
            Assert.Equal(id, Assert.Single(body.ProductIds!));
            if (++calls <= 2) return Fail(failure);
            return DependencyStub.Json(HttpStatusCode.OK, new ProductLookupResponse([dependencies.Products[id]], []));
        };
        await using var factory = Factory(dependencies);
        using var scope = factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IProductClient>();
        Assert.Equal(id, Assert.Single((await client.LookupAsync([id], CancellationToken.None))!.Products).Id);
        Assert.Equal(3, dependencies.ProductCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ProductRetriesAreBoundedAndCanBeDisabled(int retries)
    {
        using var dependencies = new DependencyStub();
        dependencies.ProductResponse = (_, _) => Task.FromResult(Fail("503"));
        await using var factory = Factory(dependencies);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["HttpClients:Product:MaxRetries"] = retries.ToString() })));
        using var scope = configured.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([Guid.NewGuid()], CancellationToken.None));
        Assert.Equal(retries + 1, dependencies.ProductCalls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task ProductApplicationErrorsAreNotRetried(int status)
    {
        using var dependencies = new DependencyStub();
        dependencies.ProductResponse = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        await using var factory = Factory(dependencies);
        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([Guid.NewGuid()], CancellationToken.None));
        Assert.Equal(1, dependencies.ProductCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndInactiveProductsAreNotRetried(bool inactive)
    {
        using var dependencies = new DependencyStub();
        var id = inactive ? dependencies.AddProduct(active: false) : Guid.NewGuid();
        await using var factory = Factory(dependencies);
        using var scope = factory.Services.CreateScope();
        var result = (await scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([id], CancellationToken.None))!;
        if (inactive) Assert.False(Assert.Single(result.Products).IsActive);
        else Assert.Equal(id, Assert.Single(result.MissingProductIds));
        Assert.Equal(1, dependencies.ProductCalls);
    }

    [Fact]
    public async Task InvalidProductJsonIsNotRetried()
    {
        using var dependencies = new DependencyStub();
        dependencies.ProductResponse = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json")
        });
        await using var factory = Factory(dependencies);
        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([Guid.NewGuid()], CancellationToken.None));
        Assert.Equal(1, dependencies.ProductCalls);
    }

    [Fact]
    public async Task TotalRequestTimeoutCancelsTheDependencyWithoutStartingMoreAttempts()
    {
        using var dependencies = new DependencyStub();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dependencies.ProductResponse = async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        await using var factory = Factory(dependencies);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HttpClients:Product:ConnectTimeoutSeconds"] = "1",
                ["HttpClients:Product:RequestTimeoutSeconds"] = "1"
            })));
        using var scope = configured.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([Guid.NewGuid()], CancellationToken.None));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(1, dependencies.ProductCalls);
    }

    [Fact]
    public async Task CallerCancellationStopsRetryBackoff()
    {
        using var dependencies = new DependencyStub();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dependencies.ProductResponse = (_, _) =>
        {
            attempted.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        };
        await using var factory = Factory(dependencies);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["HttpClients:Product:RetryDelayMilliseconds"] = "1000" })));
        using var scope = configured.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var lookup = scope.ServiceProvider.GetRequiredService<IProductClient>().LookupAsync([Guid.NewGuid()], cancellation.Token);
        await attempted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(1, dependencies.ProductCalls);
    }

    [Theory]
    [InlineData("503", InventoryOutcome.Uncertain)]
    [InlineData("disconnect", InventoryOutcome.Uncertain)]
    [InlineData("timeout", InventoryOutcome.Uncertain)]
    [InlineData("shortage", InventoryOutcome.Rejected)]
    [InlineData("invalid", InventoryOutcome.InvalidRequest)]
    [InlineData("conflict", InventoryOutcome.PayloadConflict)]
    public async Task InventoryPostIsNeverAutomaticallyRetried(string failure, InventoryOutcome expected)
    {
        using var dependencies = new DependencyStub();
        var product = Guid.NewGuid();
        var id = Guid.NewGuid();
        dependencies.InventoryResponse = (_, _) => Task.FromResult(failure switch
        {
            "shortage" => DependencyStub.Json(HttpStatusCode.Conflict, new { errorCode = "INSUFFICIENT_STOCK", reservationId = id, shortfalls = new[] { new StockShortfall(product, 2, 1) } }),
            "invalid" => DependencyStub.Json(HttpStatusCode.BadRequest, new { errorCode = "INVALID_RESERVATION" }),
            "conflict" => DependencyStub.Json(HttpStatusCode.Conflict, new { errorCode = "RESERVATION_PAYLOAD_CONFLICT" }),
            _ => Fail(failure)
        });
        await using var factory = Factory(dependencies);
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IInventoryClient>().ReserveAsync(new CreateReservationRequest
        {
            ReservationId = id, Items = [new ReservationItemRequest { ProductId = product, Quantity = 2 }]
        }, CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, dependencies.InventoryCalls);
        Assert.Equal(id, Assert.Single(dependencies.Reservations).ReservationId);
        Assert.Equal(0, dependencies.InventoryQueryCalls);
    }

    [Theory]
    [InlineData("RequestTimeoutSeconds", "0")]
    [InlineData("RequestTimeoutSeconds", "31")]
    [InlineData("ConnectTimeoutSeconds", "6")]
    [InlineData("MaxRetries", "4")]
    [InlineData("RetryDelayMilliseconds", "0")]
    public async Task InvalidConfigurationFailsAtStartup(string setting, string value)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"HttpClients:Product:{setting}"] = value
        });
        builder.Services.AddHttpClient("Product").ConfigureDependency("Product", retryProductLookups: true);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal("Product", exception.OptionsName);
        Assert.Equal(typeof(DependencyHttpSettings), exception.OptionsType);
    }

    [Fact]
    public async Task DefaultHttpConfigurationStartsSuccessfully()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddHttpClient("Product").ConfigureDependency("Product", retryProductLookups: true);
        builder.Services.AddHttpClient("Inventory").ConfigureDependency("Inventory");
        using var host = builder.Build();

        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptionsMonitor<DependencyHttpSettings>>();
        Assert.Equal(5, options.Get("Product").RequestTimeoutSeconds);
        Assert.Equal(2, options.Get("Inventory").ConnectTimeoutSeconds);
        await host.StopAsync();
    }

    private static OrderApiFactory Factory(DependencyStub dependencies) => new(
        "Server=localhost;Database=Unused;Integrated Security=True", dependencies);

    private static HttpResponseMessage Fail(string failure) => failure switch
    {
        "disconnect" => throw new HttpRequestException("Simulated disconnect"),
        "timeout" => throw new TaskCanceledException("Simulated dependency timeout"),
        _ => new HttpResponseMessage((HttpStatusCode)int.Parse(failure))
    };
}
