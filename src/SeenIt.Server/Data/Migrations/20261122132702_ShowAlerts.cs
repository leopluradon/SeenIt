using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShowAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShowAlerts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTmdbId = table.Column<int>(type: "integer", nullable: false),
                    On = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowAlerts", x => new { x.UserId, x.ShowTmdbId });
                    table.ForeignKey(
                        name: "FK_ShowAlerts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShowAlerts");
        }
    }
}
