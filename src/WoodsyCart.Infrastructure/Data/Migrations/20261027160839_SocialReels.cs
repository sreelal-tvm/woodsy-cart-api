using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SocialReels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReelMusic",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ReelTimes",
                table: "SocialConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "18:00");

            migrationBuilder.AddColumn<bool>(
                name: "ReelsEnabled",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReelMusic",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ReelTimes",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ReelsEnabled",
                table: "SocialConfigs");
        }
    }
}
