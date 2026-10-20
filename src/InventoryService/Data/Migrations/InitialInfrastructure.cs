using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryService.Data.Migrations;

[DbContext(typeof(InventoryServiceContext))]
[Migration("20261021000000_InitialInfrastructure")]
public sealed class InitialInfrastructure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
