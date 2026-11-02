using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LowStockWelcomeOffer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WelcomeOfferEndsAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WelcomeRemindedAt",
                table: "Users",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockQuantity",
                table: "ProductVariants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockQuantity",
                table: "Products",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LowStockBadgeAt",
                table: "BusinessSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "WelcomeCode",
                table: "BusinessSettings",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "WELCOME");

            migrationBuilder.AddColumn<int>(
                name: "WelcomeDiscountType",
                table: "BusinessSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "WelcomeDiscountValue",
                table: "BusinessSettings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 5m);

            migrationBuilder.AddColumn<decimal>(
                name: "WelcomeMaxDiscount",
                table: "BusinessSettings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // A sensible cap for the existing settings row (nullable columns don't take a default)
            migrationBuilder.Sql("UPDATE BusinessSettings SET WelcomeMaxDiscount = 1500");

            migrationBuilder.AddColumn<decimal>(
                name: "WelcomeMinOrder",
                table: "BusinessSettings",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "WelcomeOfferEnabled",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WelcomeValidHours",
                table: "BusinessSettings",
                type: "int",
                nullable: false,
                defaultValue: 48);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WelcomeOfferEndsAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WelcomeRemindedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "StockQuantity",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "StockQuantity",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "LowStockBadgeAt",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeCode",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeDiscountType",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeDiscountValue",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeMaxDiscount",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeMinOrder",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeOfferEnabled",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "WelcomeValidHours",
                table: "BusinessSettings");
        }
    }
}
