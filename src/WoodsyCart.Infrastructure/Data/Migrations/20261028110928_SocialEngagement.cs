using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SocialEngagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Comments",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommentsCheckedAt",
                table: "SocialPosts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Likes",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Reach",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Saves",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Shares",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StatsAt",
                table: "SocialPosts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Views",
                table: "SocialPosts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommentsEmailedAt",
                table: "SocialConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngagementIssues",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SocialPostId = table.Column<int>(type: "int", nullable: false),
                    Platform = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Author = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2200)", maxLength: 2200, nullable: false),
                    PostedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FromUs = table.Column<bool>(type: "bit", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialComments_SocialComments_ParentId",
                        column: x => x.ParentId,
                        principalTable: "SocialComments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SocialComments_SocialPosts_SocialPostId",
                        column: x => x.SocialPostId,
                        principalTable: "SocialPosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialComments_ParentId_ReadAt",
                table: "SocialComments",
                columns: new[] { "ParentId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialComments_Platform_ExternalId",
                table: "SocialComments",
                columns: new[] { "Platform", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialComments_SocialPostId",
                table: "SocialComments",
                column: "SocialPostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialComments");

            migrationBuilder.DropColumn(
                name: "Comments",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "CommentsCheckedAt",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Likes",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Reach",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Saves",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Shares",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "StatsAt",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "Views",
                table: "SocialPosts");

            migrationBuilder.DropColumn(
                name: "CommentsEmailedAt",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "EngagementIssues",
                table: "SocialConfigs");
        }
    }
}
