using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using ProductService.Application;
using Shared.Contracts.Products;

namespace ProductService.Controllers;

[ApiController]
[Route("api/products")]
[RequestSizeLimit(65536)]
public sealed class ProductsController(CatalogueService catalogue, ILogger<ProductsController> logger) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Create a product.")]
    [EndpointDescription("Enter a unique SKU, name and nonnegative unit price with at most two decimal places. The product ID is generated automatically. SKUs are trimmed and saved in uppercase. Creating a product does not initialise stock; set its stock count in InventoryService next.")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await catalogue.CreateAsync(request, cancellationToken);
        if (product is null)
            return ProductProblem(StatusCodes.Status409Conflict, "A product with this SKU already exists.", "DUPLICATE_SKU");
        logger.LogInformation("Created product {ProductId}", product.Id);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpGet]
    [EndpointSummary("List products, including inactive products, in creation order.")]
    [EndpointDescription("Use offset to skip products and limit to choose the page size, up to 200. Products are returned in creation order, including inactive products.")]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> List(
        CancellationToken cancellationToken, [FromQuery, Range(0, int.MaxValue)] int offset = 0,
        [FromQuery, Range(1, 200)] int limit = 100) =>
        Ok(await catalogue.ListAsync(offset, limit, cancellationToken));

    [HttpGet("{id:guid}")]
    [EndpointSummary("View a product.")]
    [EndpointDescription("Choose a product from the dropdown in Swagger. The response includes its generated ID, SKU, name, unit price and active status.")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var product = await catalogue.GetAsync(id, cancellationToken);
        return product is not null ? Ok(product)
            : ProductProblem(StatusCodes.Status404NotFound, "Product not found.", "PRODUCT_NOT_FOUND");
    }

    [HttpPost("lookup")]
    [EndpointDescription("Select up to 50 different products in Swagger. The response contains product details and any IDs that were not found. OrderService uses this endpoint to load catalogue prices.")]
    [EndpointSummary("Look up selected products.")]
    [ProducesResponseType<ProductLookupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProductLookupResponse>> Lookup(ProductLookupRequest request, CancellationToken cancellationToken) =>
        Ok(await catalogue.LookupAsync(request.ProductIds!, cancellationToken));

    private ObjectResult ProductProblem(int status, string title, string errorCode)
    {
        var response = Problem(statusCode: status, title: title);
        var problem = (ProblemDetails)response.Value!;
        problem.Extensions["errorCode"] = errorCode;
        return response;
    }
}
