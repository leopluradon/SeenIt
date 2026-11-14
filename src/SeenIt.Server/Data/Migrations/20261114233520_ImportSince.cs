using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "Since",
                table: "ImportJobs",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Since",
                table: "ImportJobs");
        }
    }
}
