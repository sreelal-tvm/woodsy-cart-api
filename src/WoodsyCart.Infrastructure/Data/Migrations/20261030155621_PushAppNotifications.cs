using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WoodsyCart.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PushAppNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SeenAvailable",
                table: "WishlistItems",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SeenPrice",
                table: "WishlistItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PushSubscriptions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CartCount",
                table: "PushSubscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CartProductId",
                table: "PushSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CartRemindedAt",
                table: "PushSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CartUpdatedAt",
                table: "PushSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "PushSubscriptions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "PushSubscriptions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastPromoAt",
                table: "PushSubscriptions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderId",
                table: "PushSubscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Promotions",
                table: "PushSubscriptions",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CustomersEnabled",
                table: "PushConfigs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "EventsJson",
                table: "PushConfigs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNewArrivalsAt",
                table: "PushConfigs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TeamEnabled",
                table: "PushConfigs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushAnnouncedAt",
                table: "Products",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentReminderAt",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewReminderAt",
                table: "Orders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PushSentAt",
                table: "Offers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushCampaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Devices = table.Column<int>(type: "int", nullable: false),
                    Sent = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushCampaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Email",
                table: "PushSubscriptions",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_OrderId",
                table: "PushSubscriptions",
                column: "OrderId");

            // every device so far was turned on in Admin
            migrationBuilder.Sql("UPDATE PushSubscriptions SET IsAdmin = 1");
            // what's already in the shop isn't "new"; offers already running aren't announced late
            migrationBuilder.Sql("UPDATE Products SET PushAnnouncedAt = SYSUTCDATETIME() WHERE IsActive = 1");
            migrationBuilder.Sql("UPDATE Offers SET PushSentAt = SYSUTCDATETIME() WHERE StartsAt <= SYSUTCDATETIME()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushCampaigns");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_Email",
                table: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_PushSubscriptions_OrderId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "SeenAvailable",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "SeenPrice",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "CartCount",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "CartProductId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "CartRemindedAt",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "CartUpdatedAt",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "LastPromoAt",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "Promotions",
                table: "PushSubscriptions");

            migrationBuilder.DropColumn(
                name: "CustomersEnabled",
                table: "PushConfigs");

            migrationBuilder.DropColumn(
                name: "EventsJson",
                table: "PushConfigs");

            migrationBuilder.DropColumn(
                name: "LastNewArrivalsAt",
                table: "PushConfigs");

            migrationBuilder.DropColumn(
                name: "TeamEnabled",
                table: "PushConfigs");

            migrationBuilder.DropColumn(
                name: "PushAnnouncedAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PaymentReminderAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ReviewReminderAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PushSentAt",
                table: "Offers");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PushSubscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
