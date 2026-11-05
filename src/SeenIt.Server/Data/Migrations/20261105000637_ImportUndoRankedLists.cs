using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportUndoRankedLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "Watchlist",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "WatchEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "Ratings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "Lists",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Ranked",
                table: "Lists",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "ListItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "ListItems",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingJson",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UndoJson",
                table: "ImportJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UndoneAt",
                table: "ImportJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportJobId",
                table: "Favorites",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchEvents_ImportJobId",
                table: "WatchEvents",
                column: "ImportJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WatchEvents_ImportJobId",
                table: "WatchEvents");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "Watchlist");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "WatchEvents");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "Ratings");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "Lists");

            migrationBuilder.DropColumn(
                name: "Ranked",
                table: "Lists");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "ListItems");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "ListItems");

            migrationBuilder.DropColumn(
                name: "PendingJson",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "UndoJson",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "UndoneAt",
                table: "ImportJobs");

            migrationBuilder.DropColumn(
                name: "ImportJobId",
                table: "Favorites");
        }
    }
}
