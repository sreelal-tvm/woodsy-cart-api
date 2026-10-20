using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImageCleaning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CleanReport",
                table: "ProductImages",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CleanStatus",
                table: "ProductImages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CleanedAt",
                table: "ProductImages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPath",
                table: "ProductImages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalThumbnailPath",
                table: "ProductImages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ImageCleanAuto",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ImageCleanEnhance",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ImageCleanRecentre",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ImageCleanRemoveMarks",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImages_CleanStatus",
                table: "ProductImages",
                column: "CleanStatus");
        
            // Existing settings row: photo clean-up starts switched on.
            migrationBuilder.Sql("UPDATE BusinessSettings SET ImageCleanAuto = 1, ImageCleanRemoveMarks = 1, ImageCleanRecentre = 1, ImageCleanEnhance = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductImages_CleanStatus",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "CleanReport",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "CleanStatus",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "CleanedAt",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "OriginalPath",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "OriginalThumbnailPath",
                table: "ProductImages");

            migrationBuilder.DropColumn(
                name: "ImageCleanAuto",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "ImageCleanEnhance",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "ImageCleanRecentre",
                table: "BusinessSettings");

            migrationBuilder.DropColumn(
                name: "ImageCleanRemoveMarks",
                table: "BusinessSettings");
        }
    }
}
