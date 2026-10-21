using System.Text.Json;
using Shared.Contracts.Products;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace QubeERP.ApiDocumentation;

public static class SwaggerRequestBuilder
{
    public static void AddSwaggerRequestBuilder(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient("SwaggerProducts", client =>
        {
            client.BaseAddress = new Uri(configuration["Services:Product"] ?? "http://localhost:5101");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
    }

    public static void UseRequestBuilder(this SwaggerUIOptions options, string service)
    {
        options.DocumentTitle = $"QubeERP {service}";
        options.InjectJavascript($"request-builder.js?service={service}");
        options.InjectStylesheet("request-builder.css");
        options.ConfigObject.AdditionalItems["plugins"] = new[] { "QubeSwaggerPlugin" };
    }

    public static void MapSwaggerRequestBuilder(this WebApplication app, string service)
    {
        if (!app.Environment.IsDevelopment())
            return;

        foreach (var (file, contentType) in new[]
        {
            ("request-builder.js", "text/javascript"),
            ("request-builder.css", "text/css")
        })
        {
            app.MapGet($"/swagger/{file}", (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var stream = typeof(SwaggerRequestBuilder).Assembly.GetManifestResourceStream($"QubeERP.Swagger.{file}")!;
                return Results.Stream(stream, contentType);
            }).ExcludeFromDescription();
        }

        if (service == "Products")
            return;

        app.MapGet("/swagger/products", async (int? offset, IHttpClientFactory clients, CancellationToken cancellationToken) =>
        {
            if (offset is < 0)
                return Results.Problem(statusCode: 400, title: "Product offset must not be negative.");

            try
            {
                using var response = await clients.CreateClient("SwaggerProducts")
                    .GetAsync($"/api/products?offset={offset ?? 0}&limit=200", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var products = await response.Content.ReadFromJsonAsync<ProductResponse[]>(cancellationToken);
                    if (products is not null)
                    {
                        return Results.Ok(products.Select(product => new
                        {
                            product.Id, product.Sku, product.Name, product.UnitPrice, product.IsActive
                        }));
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                app.Logger.LogWarning("Swagger product lookup failed: {Failure}", exception.GetType().Name);
            }

            return Results.Problem(statusCode: 503, title: "Product list is unavailable. Start ProductService and refresh the list.");
        }).ExcludeFromDescription();
    }
}
