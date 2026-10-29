using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SupportHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupportHours",
                table: "BusinessSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // The text the Contact us page showed until now, so it doesn't change until edited in Admin
            migrationBuilder.Sql("UPDATE BusinessSettings SET SupportHours = N'Mon–Sat, 10am–7pm'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportHours",
                table: "BusinessSettings");
        }
    }
}
