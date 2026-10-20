using Microsoft.EntityFrameworkCore;

namespace OrderService.Data;

public sealed class OrderServiceContext(DbContextOptions<OrderServiceContext> options) : DbContext(options);
