using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportPartsTrackingSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Parts",
                table: "ImportJobs",
                type: "integer",
                nullable: false,
                defaultValue: 63); // ImportParts.All for jobs created before this

            migrationBuilder.AddColumn<DateOnly>(
                name: "TrackingSince",
                table: "AspNetUsers",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Parts",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "TrackingSince",
                table: "AspNetUsers");
        }
    }
}
