using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppPushOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppPushMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppPushMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppPushMessages_PushSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "PushSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppPushMessages_SubscriptionId_CreatedAt",
                table: "AppPushMessages",
                columns: new[] { "SubscriptionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppPushMessages");
        }
    }
}
