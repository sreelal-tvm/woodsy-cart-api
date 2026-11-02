using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeVideoSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every product starts with its YouTube videos hidden (they are on the vendor's channel); the master switch stays on
            migrationBuilder.AddColumn<bool>(
                name: "ShowYouTubeVideo",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowYouTubeVideos",
                table: "BusinessSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowYouTubeVideo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShowYouTubeVideos",
                table: "BusinessSettings");
        }
    }
}
