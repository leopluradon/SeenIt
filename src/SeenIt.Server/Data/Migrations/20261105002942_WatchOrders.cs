using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class WatchOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpisodeOrders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShowTmdbId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    PartsJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShowWatchOrders",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShowTmdbId = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowWatchOrders", x => new { x.UserId, x.ShowTmdbId });
                    table.ForeignKey(
                        name: "FK_ShowWatchOrders_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpisodeOrders_ShowTmdbId",
                table: "EpisodeOrders",
                column: "ShowTmdbId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpisodeOrders");

            migrationBuilder.DropTable(
                name: "ShowWatchOrders");
        }
    }
}
