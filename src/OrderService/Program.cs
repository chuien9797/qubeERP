using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderService.Application;
using OrderService.Clients;
using OrderService.Data;
using OrderService.OpenApi;
using QubeERP.ApiDocumentation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContextFactory<OrderServiceContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Database")
        ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", timeout: TimeSpan.FromSeconds(5));
builder.Services.AddScoped<OrderApplicationService>();
builder.Services.AddHttpClient<IProductClient, ProductClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:Product"] ?? "http://localhost:5101");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:Inventory"] ?? "http://localhost:5102");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = 400,
            Title = "Request validation failed."
        };
        problem.Extensions["errorCode"] = "INVALID_ORDER";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        var response = new BadRequestObjectResult(problem);
        response.ContentTypes.Add("application/problem+json");
        return response;
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerRequestBuilder(builder.Configuration);
builder.Services.AddSwaggerGen(options => options.SchemaFilter<OrderExamples>());
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    context.ProblemDetails.Extensions.TryAdd("errorCode", context.ProblemDetails.Status switch
    {
        404 => "NOT_FOUND",
        413 => "REQUEST_TOO_LARGE",
        415 => "UNSUPPORTED_MEDIA_TYPE",
        >= 500 => "INTERNAL_ERROR",
        _ => "INVALID_ORDER"
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
    app.UseSwaggerUI(options => options.UseRequestBuilder("Orders"));
}
app.MapSwaggerRequestBuilder("Orders");
app.MapControllers();
app.MapHealthChecks("/health");

if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<OrderServiceContext>();
    await database.Database.MigrateAsync();
}

await app.RunAsync();

public partial class Program;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderServiceContext>();
        return await database.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The service database is unavailable.");
    }
}
