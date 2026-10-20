using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VendorGst : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "UnitVendorPrice",
                table: "OrderItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultGstRate",
                table: "BusinessSettings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "VendorPricesIncludeGst",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        
            // Existing settings row: GB Furniture adds 18% GST on top of its prices.
            migrationBuilder.Sql("UPDATE BusinessSettings SET DefaultGstRate = 18 WHERE DefaultGstRate = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitVendorPrice",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DefaultGstRate",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "VendorPricesIncludeGst",
                table: "BusinessSettings");
        }
    }
}
