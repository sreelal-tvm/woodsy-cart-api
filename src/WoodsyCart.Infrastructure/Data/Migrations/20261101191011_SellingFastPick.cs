using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SellingFastPick : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPick",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSellingFast",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecentOrders",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PickLabel",
                table: "BusinessSettings",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Woodsy pick");

            migrationBuilder.AddColumn<int>(
                name: "SellingFastDays",
                table: "BusinessSettings",
                type: "int",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "SellingFastOrders",
                table: "BusinessSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPick",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsSellingFast",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RecentOrders",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PickLabel",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "SellingFastDays",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "SellingFastOrders",
                table: "BusinessSettings");
        }
    }
}
