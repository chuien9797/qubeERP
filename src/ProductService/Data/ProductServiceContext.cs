using Microsoft.EntityFrameworkCore;

namespace ProductService.Data;

public sealed class ProductServiceContext(DbContextOptions<ProductServiceContext> options) : DbContext(options);
