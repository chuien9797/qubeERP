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
    [EndpointSummary("Create a product with a canonical SKU and a nonnegative price.")]
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
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> List(
        CancellationToken cancellationToken, [FromQuery, Range(0, int.MaxValue)] int offset = 0,
        [FromQuery, Range(1, 200)] int limit = 100) =>
        Ok(await catalogue.ListAsync(offset, limit, cancellationToken));

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get an existing product by ID, including its active status.")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var product = await catalogue.GetAsync(id, cancellationToken);
        return product is not null ? Ok(product)
            : ProductProblem(StatusCodes.Status404NotFound, "Product not found.", "PRODUCT_NOT_FOUND");
    }

    [HttpPost("lookup")]
    [EndpointSummary("Look up 1–50 distinct product IDs and return missing IDs separately.")]
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
