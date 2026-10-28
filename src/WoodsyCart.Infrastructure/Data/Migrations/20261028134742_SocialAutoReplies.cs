using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SocialAutoReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiKeyProtected",
                table: "SocialConfigs",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "SocialConfigs",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "claude-opus-5-5");

            migrationBuilder.AddColumn<int>(
                name: "AutoRepliesPerHour",
                table: "SocialConfigs",
                type: "int",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<bool>(
                name: "AutoReplyAi",
                table: "SocialConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoReplyMode",
                table: "SocialConfigs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "Auto",
                table: "SocialComments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReplyState",
                table: "SocialComments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SuggestedComplete",
                table: "SocialComments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedReply",
                table: "SocialComments",
                type: "nvarchar(2200)",
                maxLength: 2200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedSource",
                table: "SocialComments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuggestedTopic",
                table: "SocialComments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiKeyProtected",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "AutoRepliesPerHour",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "AutoReplyAi",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "AutoReplyMode",
                table: "SocialConfigs");

            migrationBuilder.DropColumn(
                name: "Auto",
                table: "SocialComments");

            migrationBuilder.DropColumn(
                name: "ReplyState",
                table: "SocialComments");

            migrationBuilder.DropColumn(
                name: "SuggestedComplete",
                table: "SocialComments");

            migrationBuilder.DropColumn(
                name: "SuggestedReply",
                table: "SocialComments");

            migrationBuilder.DropColumn(
                name: "SuggestedSource",
                table: "SocialComments");

            migrationBuilder.DropColumn(
                name: "SuggestedTopic",
                table: "SocialComments");
        }
    }
}
