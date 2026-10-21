using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using Shared.Contracts.Orders;
using Testcontainers.MsSql;
using Xunit;

namespace OrderService.Tests;

public sealed class OrderApiFixture : IAsyncLifetime
{
    private readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private OrderApiFactory? factory;
    private string connectionString = "";
    public DependencyStub Dependencies { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await sql.StartAsync();
        connectionString = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = "OrderDb" }.ConnectionString;
        await using var database = CreateContext();
        await database.Database.MigrateAsync();
        factory = new OrderApiFactory(connectionString, Dependencies);
        Client = factory.CreateClient();
    }

    public OrderServiceContext CreateContext() => new(new DbContextOptionsBuilder<OrderServiceContext>().UseSqlServer(connectionString).Options);

    public Task<HttpResponseMessage> PostAsync(string key, params OrderItemRequest[] items)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest { Items = items })
        };
        request.Headers.Add("Idempotency-Key", key);
        return SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request) return await Client.SendAsync(request);
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await sql.DisposeAsync();
        Dependencies.Dispose();
    }
}
