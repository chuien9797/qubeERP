using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using InventoryService.Application;
using InventoryService.Clients;
using InventoryService.Data;
using InventoryService.Health;
using InventoryService.OpenApi;
using QubeERP.ApiDocumentation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextFactory<InventoryServiceContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(5));
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<StockReceiptService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddHttpClient<IProductClient, ProductClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:Product"] ?? "http://localhost:5101");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Request validation failed."
        };
        problem.Extensions["errorCode"] = context.HttpContext.Request.Path.StartsWithSegments("/api/inventory/reservations")
            ? "INVALID_RESERVATION"
            : "INVALID_REQUEST";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        var response = new BadRequestObjectResult(problem);
        response.ContentTypes.Add("application/problem+json");
        return response;
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerRequestBuilder(builder.Configuration);
builder.Services.AddSwaggerGen(options => options.SchemaFilter<InventoryExamples>());
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    context.ProblemDetails.Extensions.TryAdd("errorCode", context.ProblemDetails.Status switch
    {
        404 => "NOT_FOUND",
        413 => "REQUEST_TOO_LARGE",
        415 => "UNSUPPORTED_MEDIA_TYPE",
        >= 500 => "INTERNAL_ERROR",
        _ => "INVALID_REQUEST"
    });
});

var app = builder.Build();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.UseRequestBuilder("Inventory"));
}
app.MapSwaggerRequestBuilder("Inventory");
app.MapControllers();
app.MapHealthChecks("/health");

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<InventoryServiceContext>();
    await database.Database.MigrateAsync();
}

await app.RunAsync();

public partial class Program;
