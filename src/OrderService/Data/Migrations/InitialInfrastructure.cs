using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OrderService.Data.Migrations;

[DbContext(typeof(OrderServiceContext))]
[Migration("20261021000000_InitialInfrastructure")]
public sealed class InitialInfrastructure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
