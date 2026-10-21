extern alias InventoryApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Data;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Testcontainers.MsSql;
using Xunit;
using InventoryContext = InventoryApi::InventoryService.Data.InventoryServiceContext;
using InventoryReservationService = InventoryApi::InventoryService.Application.ReservationService;
using ReservationOutcome = InventoryApi::InventoryService.Application.ReservationOutcome;
using StockItem = InventoryApi::InventoryService.Domain.StockItem;

namespace OrderService.Tests;

public sealed class RecoveryFixture : IAsyncLifetime
{
    private readonly MsSqlContainer sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private string orderConnection = "";
    private DbContextOptions<InventoryContext> inventoryOptions = null!;
    private InventoryReservationService reservations = null!;
    private OrderApiFactory? factory;
    public DependencyStub Dependencies { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await sql.StartAsync();
        orderConnection = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = "OrderDb" }.ConnectionString;
        var inventoryConnection = new SqlConnectionStringBuilder(sql.GetConnectionString()) { InitialCatalog = "InventoryDb" }.ConnectionString;
        inventoryOptions = new DbContextOptionsBuilder<InventoryContext>().UseSqlServer(inventoryConnection).Options;
        await using var orders = CreateOrderContext();
        await orders.Database.MigrateAsync();
        await using var inventory = CreateInventoryContext();
        await inventory.Database.MigrateAsync();
        reservations = new InventoryReservationService(inventoryOptions, NullLogger<InventoryReservationService>.Instance);
        ResetHandlers();
        factory = new OrderApiFactory(orderConnection, Dependencies);
        Client = factory.CreateClient();
    }

    public OrderServiceContext CreateOrderContext() => new(new DbContextOptionsBuilder<OrderServiceContext>().UseSqlServer(orderConnection).Options);
    public InventoryContext CreateInventoryContext() => new(inventoryOptions);

    public async Task<Guid> AddProductAsync(int onHand = 10, decimal price = 12.50m)
    {
        var id = Dependencies.AddProduct(price);
        await using var database = CreateInventoryContext();
        database.StockItems.Add(new StockItem { ProductId = id, OnHand = onHand, Reserved = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await database.SaveChangesAsync();
        return id;
    }

    public async Task<HttpResponseMessage> ReserveAsync(CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var result = await reservations.ReserveAsync(request, cancellationToken);
        return result.Outcome switch
        {
            ReservationOutcome.Reserved => DependencyStub.Json(result.IsReplay ? HttpStatusCode.OK : HttpStatusCode.Created, result.Reservation),
            ReservationOutcome.Rejected => DependencyStub.Json(HttpStatusCode.Conflict,
                new { errorCode = "INSUFFICIENT_STOCK", reservationId = request.ReservationId, shortfalls = result.Reservation!.Shortfalls }),
            ReservationOutcome.PayloadConflict => DependencyStub.Json(HttpStatusCode.Conflict,
                new { errorCode = "RESERVATION_PAYLOAD_CONFLICT", reservationId = request.ReservationId }),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };
    }

    public async Task<HttpResponseMessage> QueryAsync(Guid id, CancellationToken cancellationToken)
    {
        var saved = await reservations.GetAsync(id, cancellationToken);
        return saved is null
            ? DependencyStub.Json(HttpStatusCode.NotFound, new { errorCode = "RESERVATION_NOT_FOUND", reservationId = id })
            : DependencyStub.Json(HttpStatusCode.OK, saved);
    }

    public Task<HttpResponseMessage> PostAsync(string key, params OrderItemRequest[] items)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(new CreateOrderRequest { Items = items }) };
        request.Headers.Add("Idempotency-Key", key);
        return SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request) return await Client.SendAsync(request);
    }

    public void ResetHandlers()
    {
        Dependencies.ProductResponse = null;
        Dependencies.InventoryResponse = ReserveAsync;
        Dependencies.InventoryQueryResponse = QueryAsync;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        Dependencies.Dispose();
        await sql.DisposeAsync();
    }
}
