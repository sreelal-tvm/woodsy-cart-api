using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FeedEnabled",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FeedFetchedAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedFetchedBy",
                table: "SocialConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedEnabled",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "FeedFetchedAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "FeedFetchedBy",
                table: "SocialConfigs");
        }
    }
}
