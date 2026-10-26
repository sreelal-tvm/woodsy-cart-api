using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class WhiteBackground : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BackgroundAt",
                table: "ProductImages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundModel",
                table: "ProductImages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundNote",
                table: "ProductImages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundPath",
                table: "ProductImages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackgroundStatus",
                table: "ProductImages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundThumbnailPath",
                table: "ProductImages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImages_BackgroundStatus",
                table: "ProductImages",
                column: "BackgroundStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductImages_BackgroundStatus",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundAt",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundModel",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundNote",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundPath",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundStatus",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "BackgroundThumbnailPath",
                table: "ProductImages");
        }
    }
}
