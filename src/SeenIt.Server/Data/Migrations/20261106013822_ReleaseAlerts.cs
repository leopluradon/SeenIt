using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarToken",
                table: "AspNetUsers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastReleaseEmail",
                table: "AspNetUsers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReleaseEmails",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TmdbId = table.Column<int>(type: "integer", nullable: false),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    PosterPath = table.Column<string>(type: "text", nullable: true),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Each existing account gets its own secret calendar link before the column becomes unique.
            migrationBuilder.Sql(@"UPDATE ""AspNetUsers"" SET ""CalendarToken"" = md5(random()::text || clock_timestamp()::text || ""Id""::text) || substr(md5(random()::text), 1, 16) WHERE ""CalendarToken"" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_CalendarToken",
                table: "AspNetUsers",
                column: "CalendarToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_Type_TmdbId_SeasonNumber_EpisodeNumber",
                table: "Notifications",
                columns: new[] { "UserId", "Type", "TmdbId", "SeasonNumber", "EpisodeNumber" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_CalendarToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CalendarToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LastReleaseEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReleaseEmails",
                table: "AspNetUsers");
        }
    }
}
