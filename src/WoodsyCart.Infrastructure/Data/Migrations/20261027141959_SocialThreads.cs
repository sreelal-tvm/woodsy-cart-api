using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SocialThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ThreadsAppId",
                table: "SocialConfigs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ThreadsConnectedAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ThreadsEnabled",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadsTemplate",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ThreadsTokenExpiresAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadsTokenProtected",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ThreadsTokenRefreshedAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadsTopic",
                table: "SocialConfigs",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadsUserId",
                table: "SocialConfigs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThreadsUsername",
                table: "SocialConfigs",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("UPDATE SocialConfigs SET ThreadsTopic = 'Furniture'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThreadsAppId",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsConnectedAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsEnabled",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsTemplate",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsTokenExpiresAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsTokenProtected",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsTokenRefreshedAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsTopic",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsUserId",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "ThreadsUsername",
                table: "SocialConfigs");
        }
    }
}
