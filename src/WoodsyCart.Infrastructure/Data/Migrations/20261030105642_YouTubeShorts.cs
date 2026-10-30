using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeShorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "YouTubeChannelId",
                table: "SocialConfigs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeChannelTitle",
                table: "SocialConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeClientId",
                table: "SocialConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeClientSecretProtected",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "YouTubeConnectedAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "YouTubeEnabled",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeHandle",
                table: "SocialConfigs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeRefreshTokenProtected",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeTimes",
                table: "SocialConfigs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "17:00");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "YouTubeChannelId",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeChannelTitle",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeClientId",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeClientSecretProtected",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeConnectedAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeEnabled",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeHandle",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeRefreshTokenProtected",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "YouTubeTimes",
                table: "SocialConfigs");
        }
    }
}
