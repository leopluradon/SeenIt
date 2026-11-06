using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AirTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AirNetwork",
                table: "Shows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TvmazeCheckedAt",
                table: "Shows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TvmazeId",
                table: "Shows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AirStamp",
                table: "Episodes",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AirNetwork",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "TvmazeCheckedAt",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "TvmazeId",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "AirStamp",
                table: "Episodes");
        }
    }
}
