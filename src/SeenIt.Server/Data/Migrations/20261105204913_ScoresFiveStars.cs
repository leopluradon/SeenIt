using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScoresFiveStars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FiveStarRatings",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExternalScores",
                columns: table => new
                {
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TmdbId = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalScores", x => new { x.Type, x.TmdbId });
                });

            migrationBuilder.CreateTable(
                name: "ImdbRatings",
                columns: table => new
                {
                    ImdbId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rating = table.Column<double>(type: "double precision", nullable: false),
                    Votes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImdbRatings", x => x.ImdbId);
                });

            migrationBuilder.CreateTable(
                name: "ServerState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerState", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalScores");

            migrationBuilder.DropTable(
                name: "ImdbRatings");

            migrationBuilder.DropTable(
                name: "ServerState");

            migrationBuilder.DropColumn(
                name: "FiveStarRatings",
                table: "AspNetUsers");
        }
    }
}
