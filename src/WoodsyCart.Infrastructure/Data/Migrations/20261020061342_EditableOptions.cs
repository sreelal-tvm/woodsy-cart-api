using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EditableOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceVariantId",
                table: "ProductVariants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorName",
                table: "ProductVariants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustomized",
                table: "ProductOptionValues",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "ProductOptionValues",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ProductOptionValues",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VendorColorCode",
                table: "ProductOptionValues",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorValue",
                table: "ProductOptionValues",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsColour",
                table: "ProductOptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsCustomized",
                table: "ProductOptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VendorName",
                table: "ProductOptions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VendorVariantName",
                table: "OrderItems",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // Keep the vendor's own names and colour codes alongside what customers see.
            migrationBuilder.Sql(@"UPDATE ProductOptionValues SET VendorValue = Value, VendorColorCode = ColorCode WHERE VendorValueId IS NOT NULL;");
            migrationBuilder.Sql(@"UPDATE o SET VendorName = o.Name FROM ProductOptions o
                WHERE EXISTS (SELECT 1 FROM ProductOptionValues v WHERE v.ProductOptionId = o.Id AND v.VendorValueId IS NOT NULL);");
            migrationBuilder.Sql(@"UPDATE ProductVariants SET VendorName = Name WHERE VendorVariantId IS NOT NULL;");
            // Values keep their current order.
            migrationBuilder.Sql(@"UPDATE v SET SortOrder = x.rn FROM ProductOptionValues v
                JOIN (SELECT Id, ROW_NUMBER() OVER (PARTITION BY ProductOptionId ORDER BY Id) - 1 AS rn FROM ProductOptionValues) x ON x.Id = v.Id;");
            // Colour merge: Colour, Color, Wood Colour, Name of Colour, Name Of Shade … are all colours;
            // a product's only colour option is shown simply as "Colour".
            migrationBuilder.Sql(@"UPDATE ProductOptions SET IsColour = 1 WHERE Name LIKE '%colour%' OR Name LIKE '%color%' OR Name LIKE '%shade%';");
            migrationBuilder.Sql(@"UPDATE o SET Name = 'Colour' FROM ProductOptions o
                WHERE o.IsColour = 1 AND o.Name <> 'Colour'
                  AND (SELECT COUNT(*) FROM ProductOptions x WHERE x.ProductId = o.ProductId AND x.IsColour = 1) = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceVariantId",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "VendorName",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "IsCustomized",
                table: "ProductOptionValues");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "ProductOptionValues");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ProductOptionValues");

            migrationBuilder.DropColumn(
                name: "VendorColorCode",
                table: "ProductOptionValues");

            migrationBuilder.DropColumn(
                name: "VendorValue",
                table: "ProductOptionValues");

            migrationBuilder.DropColumn(
                name: "IsColour",
                table: "ProductOptions");

            migrationBuilder.DropColumn(
                name: "IsCustomized",
                table: "ProductOptions");

            migrationBuilder.DropColumn(
                name: "VendorName",
                table: "ProductOptions");

            migrationBuilder.DropColumn(
                name: "VendorVariantName",
                table: "OrderItems");
        }
    }
}
