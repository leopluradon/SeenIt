using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "TmdbAirDate",
                table: "Episodes",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TvmazeAirDate",
                table: "Episodes",
                type: "date",
                nullable: true);
            // What's cached so far is TMDB's date; TVmaze's arrives on its next check.
            migrationBuilder.Sql("UPDATE \"Episodes\" SET \"TmdbAirDate\" = \"AirDate\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TmdbAirDate",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "TvmazeAirDate",
                table: "Episodes");
        }
    }
}
