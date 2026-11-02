using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlashDeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Notify",
                table: "Offers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Repeat",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RepeatDays",
                table: "Offers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepeatWeeks",
                table: "Offers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rounding",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WindowMinutes",
                table: "Offers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "WindowStart",
                table: "Offers",
                type: "time",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OfferExcludedProducts",
                columns: table => new
                {
                    OfferId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfferExcludedProducts", x => new { x.OfferId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_OfferExcludedProducts_Offers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OfferExcludedProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfferExcludedProducts_ProductId",
                table: "OfferExcludedProducts",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfferExcludedProducts");

            migrationBuilder.DropColumn(
                name: "Notify",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "Repeat",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "RepeatDays",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "RepeatWeeks",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "Rounding",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "WindowMinutes",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "WindowStart",
                table: "Offers");
        }
    }
}
