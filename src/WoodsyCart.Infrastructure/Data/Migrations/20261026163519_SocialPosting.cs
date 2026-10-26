using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SocialPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SocialConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetaAppId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PageId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PageName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PageTokenProtected = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    InstagramUserId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InstagramUsername = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InstagramEnabled = table.Column<bool>(type: "bit", nullable: false),
                    FacebookEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Template = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IncludePrice = table.Column<bool>(type: "bit", nullable: false),
                    ShowMrp = table.Column<bool>(type: "bit", nullable: false),
                    WhatsApp = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    City = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Hashtags = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MaxHashtags = table.Column<int>(type: "int", nullable: false),
                    PostTimes = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AutoQueueNewProducts = table.Column<bool>(type: "bit", nullable: false),
                    AutoQueueSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Permalink = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CaptionOverride = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: true),
                    PostedCaption = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: true),
                    PostedSnapshot = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PhotoCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialPosts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_ProductId_Platform",
                table: "SocialPosts",
                columns: new[] { "ProductId", "Platform" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialPosts_Status_ScheduledAt",
                table: "SocialPosts",
                columns: new[] { "Status", "ScheduledAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialConfigs");

            migrationBuilder.DropTable(
                name: "SocialPosts");
        }
    }
}
