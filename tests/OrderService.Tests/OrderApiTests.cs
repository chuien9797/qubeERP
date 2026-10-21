using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain;
using Shared.Contracts.Inventory;
using Shared.Contracts.Orders;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "Integration")]
public sealed class OrderApiTests(OrderApiFixture fixture) : IClassFixture<OrderApiFixture>
{
    private DependencyStub Dependencies => fixture.Dependencies;
    private static string NewKey() => Guid.NewGuid().ToString("N");
    private static OrderItemRequest Item(Guid id, int quantity = 1) => new() { ProductId = id, Quantity = quantity };

    [Fact]
    public async Task T02_MultiItemOrderPersistsPricesBeforeCallingInventory()
    {
        var first = Dependencies.AddProduct(12.50m);
        var second = Dependencies.AddProduct(7.25m);
        var key = NewKey();
        Dependencies.InventoryResponse = async (request, token) =>
        {
            await using var database = fixture.CreateContext();
            await database.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1000", token);
            var saved = await database.Orders.Include(order => order.Lines).SingleAsync(order => order.Id == request.ReservationId, token);
            Assert.Equal("Pending", saved.Status);
            Assert.Equal(2, saved.Lines.Count);
            Assert.NotNull(saved.LastAttemptAtUtc);
            return Reserved(request);
        };
        try
        {
            using var response = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
            Assert.Equal("Confirmed", order.Status);
            Assert.Equal(52m, order.TotalAmount);
            Assert.Equal(2, order.Lines.Count);
            Assert.Equal(37.50m, order.Lines.Single(line => line.ProductId == first).LineTotal);
            Assert.Equal(Dependencies.Products[first].Sku, order.Lines.Single(line => line.ProductId == first).Sku);
            Assert.Equal("Original product", order.Lines[0].Name);
            Assert.Equal("/api/orders/" + order.OrderId, response.Headers.Location!.AbsolutePath);
            using var read = await fixture.Client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(order.OrderId, (await read.Content.ReadFromJsonAsync<OrderResponse>())!.OrderId);
        }
        finally { Dependencies.InventoryResponse = null; }
    }

    [Fact]
    public async Task T07_SimultaneousSameKeyCreatesOneOrderAndUsesOnlyWinningId()
    {
        var product = Dependencies.AddProduct();
        var key = NewKey();
        var arrivals = 0;
        var bothLookingUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dependencies.ProductResponse = async (request, token) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothLookingUp.TrySetResult();
            await bothLookingUp.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            return await Dependencies.LookupAsync(request, token);
        };
        var callsBefore = Dependencies.InventoryCalls;
        try
        {
            var responses = await Task.WhenAll(fixture.PostAsync(key, Item(product, 3)), fixture.PostAsync(key, Item(product, 3)));
            try
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.All(responses, response => Assert.Contains(response.StatusCode,
                    new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Accepted }));
                var ids = new List<Guid>();
                foreach (var response in responses)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    ids.Add(json.RootElement.GetProperty("orderId").GetGuid());
                }
                Assert.Single(ids.Distinct());
                await using var database = fixture.CreateContext();
                var saved = Assert.Single(await database.Orders.Include(order => order.Lines).Where(order => order.IdempotencyKey == key).ToListAsync());
                Assert.Equal(ids[0], saved.Id);
                Assert.Equal("Confirmed", saved.Status);
                Assert.Single(saved.Lines);
                Assert.Equal(callsBefore + 1, Dependencies.InventoryCalls);
                Assert.Single(Dependencies.Reservations, request => request.ReservationId == saved.Id);
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally { Dependencies.ProductResponse = null; }
    }

    [Fact]
    public async Task ConfirmedReplayKeepsOriginalPricesAndDoesNotCallDependencies()
    {
        var first = Dependencies.AddProduct(3.33m);
        var second = Dependencies.AddProduct(4m);
        var key = NewKey();
        using var created = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<OrderResponse>())!;
        Dependencies.Products[first] = Dependencies.Products[first] with { UnitPrice = 999m, Name = "Changed", IsActive = false };
        Dependencies.ProductResponse = (_, _) => throw new HttpRequestException("Product unavailable");
        var productCalls = Dependencies.ProductCalls;
        var inventoryCalls = Dependencies.InventoryCalls;
        try
        {
            using var replay = await fixture.PostAsync(key, Item(second, 2), Item(first, 3));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(await created.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            Assert.Equal(17.99m, original.TotalAmount);
            Assert.Equal(productCalls, Dependencies.ProductCalls);
            Assert.Equal(inventoryCalls, Dependencies.InventoryCalls);
        }
        finally { Dependencies.ProductResponse = null; }
    }

    [Fact]
    public async Task RejectedOrderReplaysOriginalShortfallsWithoutAnotherCall()
    {
        var first = Dependencies.AddProduct();
        var second = Dependencies.AddProduct();
        var key = NewKey();
        Dependencies.InventoryResponse = (request, _) => Task.FromResult(DependencyStub.Json(HttpStatusCode.Conflict,
            new { errorCode = "INSUFFICIENT_STOCK", reservationId = request.ReservationId,
                shortfalls = new[] { new StockShortfall(second, 2, 1) } }));
        Guid id;
        try
        {
            using var response = await fixture.PostAsync(key, Item(first, 3), Item(second, 2));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            id = json.RootElement.GetProperty("orderId").GetGuid();
            Assert.NotNull(response.Headers.Location);
        }
        finally { Dependencies.InventoryResponse = null; }
        var calls = Dependencies.InventoryCalls;
        using var replay = await fixture.PostAsync(key, Item(second, 2), Item(first, 3));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var saved = (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;
        Assert.Equal("Rejected", saved.Status);
        Assert.Equal(1, Assert.Single(saved.Shortfalls).Available);
        Assert.Equal(calls, Dependencies.InventoryCalls);
    }

    [Fact]
    public async Task DifferentPayloadConflictsBeforeProductLookup()
    {
        var product = Dependencies.AddProduct();
        var key = NewKey();
        using var created = await fixture.PostAsync(key, Item(product));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var calls = Dependencies.ProductCalls;
        var inventoryCalls = Dependencies.InventoryCalls;
        using var conflict = await fixture.PostAsync(key, Item(product, 2));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var json = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(calls, Dependencies.ProductCalls);
        Assert.Equal(inventoryCalls, Dependencies.InventoryCalls);
    }

    [Fact]
    public async Task MissingAndInactiveProductsDoNotPersistOrders()
    {
        var inactive = Dependencies.AddProduct(active: false);
        var missing = Guid.NewGuid();
        var key = NewKey();
        var calls = Dependencies.InventoryCalls;
        using var response = await fixture.PostAsync(key, Item(inactive), Item(missing));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_PRODUCT", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("invalidProductIds").GetArrayLength());
        await AssertAbsentAsync(key);
        Assert.Equal(calls, Dependencies.InventoryCalls);
    }

    [Fact]
    public async Task ProductFailureDoesNotPersistAnOrder()
    {
        var key = NewKey();
        Dependencies.ProductResponse = (_, _) => throw new HttpRequestException("Connection lost");
        try
        {
            using var response = await fixture.PostAsync(key, Item(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await AssertAbsentAsync(key);
        }
        finally { Dependencies.ProductResponse = null; }
    }

    [Fact]
    public async Task DecimalOverflowIsRejectedBeforeOrderOrReservationCreation()
    {
        var key = NewKey();
        var product = Dependencies.AddProduct(9999999999999999.99m);
        var calls = Dependencies.InventoryCalls;
        using var response = await fixture.PostAsync(key, Item(product, 2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertAbsentAsync(key);
        Assert.Equal(calls, Dependencies.InventoryCalls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("disconnect")]
    [InlineData("server-error")]
    public async Task UncertainInventoryLeavesPendingAndReplayDoesNotYetRecover(string failure)
    {
        var key = NewKey();
        var product = Dependencies.AddProduct();
        Dependencies.InventoryResponse = (_, _) => failure switch
        {
            "timeout" => throw new TaskCanceledException("Dependency timeout"),
            "disconnect" => throw new HttpRequestException("Response lost"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };
        Guid id;
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var pending = (await response.Content.ReadFromJsonAsync<PendingOrderResponse>())!;
            id = pending.OrderId;
            Assert.Equal("Pending", pending.Status);
            Assert.Equal("/api/orders/" + id, pending.StatusUrl);
            Assert.Equal(pending.StatusUrl, response.Headers.Location!.OriginalString);
        }
        finally { Dependencies.InventoryResponse = null; }
        var productCalls = Dependencies.ProductCalls;
        var inventoryCalls = Dependencies.InventoryCalls;
        var saved = (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;
        using var replay = await fixture.PostAsync(key, Item(product));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var after = (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;
        Assert.Equal(saved.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(saved.LastAttemptAtUtc, after.LastAttemptAtUtc);
        Assert.Equal("Pending", after.Status);
        Assert.Equal(productCalls, Dependencies.ProductCalls);
        Assert.Equal(inventoryCalls, Dependencies.InventoryCalls);
    }

    [Theory]
    [InlineData(400, "INVALID_RESERVATION", "Rejected", "INVENTORY_INVALID_REQUEST")]
    [InlineData(409, "RESERVATION_PAYLOAD_CONFLICT", "RequiresReview", "RESERVATION_PAYLOAD_CONFLICT")]
    [InlineData(409, "SOMETHING_ELSE", "RequiresReview", "INVENTORY_UNEXPECTED_RESPONSE")]
    [InlineData(422, "INVALID_PRODUCT", "RequiresReview", "INVENTORY_UNEXPECTED_RESPONSE")]
    public async Task InventoryContractDefectsHaveExplicitTerminalMappings(int status, string code, string expectedStatus, string expectedCode)
    {
        var key = NewKey();
        var product = Dependencies.AddProduct();
        Dependencies.InventoryResponse = (request, _) => Task.FromResult(DependencyStub.Json((HttpStatusCode)status,
            new { errorCode = code, reservationId = request.ReservationId }));
        Guid id;
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            id = json.RootElement.GetProperty("orderId").GetGuid();
            Assert.Equal(expectedCode, json.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetString());
        }
        finally { Dependencies.InventoryResponse = null; }
        var saved = (await fixture.Client.GetFromJsonAsync<OrderResponse>($"/api/orders/{id}"))!;
        Assert.Equal(expectedStatus, saved.Status);
        Assert.Equal(expectedCode, saved.FailureCode);
        var calls = Dependencies.InventoryCalls;
        using var replay = await fixture.PostAsync(key, Item(product));
        Assert.Equal(HttpStatusCode.BadGateway, replay.StatusCode);
        Assert.Equal(calls, Dependencies.InventoryCalls);
    }

    [Fact]
    public async Task PendingTransitionCannotOverwriteAnExistingTerminalState()
    {
        var key = NewKey();
        var product = Dependencies.AddProduct();
        Dependencies.InventoryResponse = async (request, token) =>
        {
            await using var database = fixture.CreateContext();
            await database.Orders.Where(order => order.Id == request.ReservationId).ExecuteUpdateAsync(update => update
                .SetProperty(order => order.Status, "RequiresReview")
                .SetProperty(order => order.FailureCode, "OPERATOR_REVIEW"), token);
            return Reserved(request);
        };
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            await using var database = fixture.CreateContext();
            Assert.Equal("RequiresReview", (await database.Orders.SingleAsync(order => order.IdempotencyKey == key)).Status);
        }
        finally { Dependencies.InventoryResponse = null; }
    }

    [Fact]
    public async Task FiftyLinesAndMaximumQuantityAreAccepted()
    {
        var items = Enumerable.Range(0, 50).Select(_ => Item(Dependencies.AddProduct(0.01m), 10000)).ToArray();
        using var response = await fixture.PostAsync(NewKey(), items);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(50, order.Lines.Count);
        Assert.Equal(5000m, order.TotalAmount);
    }

    [Fact]
    public async Task KeysAreCaseSensitiveAndSurroundingWhitespaceIsTrimmed()
    {
        var key = "checkout-" + NewKey();
        var product = Dependencies.AddProduct();
        using var first = await fixture.PostAsync(" " + key + " ", Item(product));
        using var replay = await fixture.PostAsync(key, Item(product));
        using var distinct = await fixture.PostAsync(key.ToUpperInvariant(), Item(product));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Created, distinct.StatusCode);
    }

    [Fact]
    public async Task FailureSavingLinesRollsBackHeaderAndNeverCallsInventory()
    {
        var key = NewKey();
        var product = Dependencies.AddProduct();
        var calls = Dependencies.InventoryCalls;
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OrderLines WITH NOCHECK ADD CONSTRAINT CK_TestOrderFailure CHECK (UnitPrice < 0)");
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertAbsentAsync(key);
            Assert.Equal(calls, Dependencies.InventoryCalls);
        }
        finally { await database.Database.ExecuteSqlRawAsync("ALTER TABLE dbo.OrderLines DROP CONSTRAINT CK_TestOrderFailure"); }
    }

    [Fact]
    public async Task UnrelatedUniqueViolationIsNotAKeyReplay()
    {
        var key = NewKey();
        var product = Dependencies.AddProduct(87654.32m);
        using var created = await fixture.PostAsync(NewKey(), Item(product));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var database = fixture.CreateContext();
        await database.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IX_TestOrderTotal ON dbo.Orders(TotalAmount) WHERE TotalAmount = 87654.32");
        var calls = Dependencies.InventoryCalls;
        try
        {
            using var response = await fixture.PostAsync(key, Item(product));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await AssertAbsentAsync(key);
            Assert.Equal(calls, Dependencies.InventoryCalls);
        }
        finally { await database.Database.ExecuteSqlRawAsync("DROP INDEX IX_TestOrderTotal ON dbo.Orders"); }
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(10001, 1, 10001)]
    [InlineData(1, -1, -1)]
    [InlineData(2, 3, 5)]
    public async Task SqlRejectsInvalidQuantitiesAndAmounts(int quantity, decimal price, decimal lineTotal)
    {
        var key = NewKey();
        var order = new Order { IdempotencyKey = key, RequestFingerprint = new string('a', 64), TotalAmount = 0 };
        order.Lines.Add(new OrderLine { OrderId = order.Id, ProductId = Guid.NewGuid(), Sku = "CHECK", Name = "Check", Quantity = quantity,
            UnitPrice = price, LineTotal = lineTotal });
        await using var database = fixture.CreateContext();
        database.Orders.Add(order);
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        await AssertAbsentAsync(key);
    }

    [Fact]
    public async Task MissingOrderGetIsReadOnly()
    {
        var calls = Dependencies.InventoryCalls;
        var products = Dependencies.ProductCalls;
        using var response = await fixture.Client.GetAsync($"/api/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(calls, Dependencies.InventoryCalls);
        Assert.Equal(products, Dependencies.ProductCalls);
    }

    private async Task AssertAbsentAsync(string key)
    {
        await using var database = fixture.CreateContext();
        Assert.False(await database.Orders.AnyAsync(order => order.IdempotencyKey == key));
    }

    private static HttpResponseMessage Reserved(CreateReservationRequest request) => DependencyStub.Json(HttpStatusCode.Created,
        new ReservationResponse(request.ReservationId!.Value, "Reserved", null, []));
}
