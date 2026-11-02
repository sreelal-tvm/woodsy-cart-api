using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenamedTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRenamed",
                table: "Tags",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VendorName",
                table: "Tags",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // The vendor's "Bestseller" and "Trending" tags claim popularity we can't show from our own sales
            // (dark-pattern rules on false popularity), so they get neutral names that vendor sync keeps.
            migrationBuilder.Sql("UPDATE Tags SET VendorName = Name WHERE VendorTagId IS NOT NULL");
            migrationBuilder.Sql("UPDATE Tags SET Name = N'⭐ Top pick', IsRenamed = 1 WHERE VendorTagId = 13601");
            migrationBuilder.Sql("UPDATE Tags SET Name = N'✨ Style pick', IsRenamed = 1 WHERE VendorTagId = 28962");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Tags SET Name = VendorName WHERE IsRenamed = 1 AND VendorName IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "IsRenamed",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "VendorName",
                table: "Tags");
        }
    }
}
