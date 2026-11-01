using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewsAndLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cast",
                table: "Shows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Countries",
                table: "Shows",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "Creators",
                table: "Shows",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetaVersion",
                table: "Shows",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<string>>(
                name: "Networks",
                table: "Shows",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "Shows",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cast",
                table: "Movies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Countries",
                table: "Movies",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "Directors",
                table: "Movies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetaVersion",
                table: "Movies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OriginalLanguage",
                table: "Movies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Studios",
                table: "Movies",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "HiddenHomeSections",
                table: "AspNetUsers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "Tabs",
                table: "AspNetUsers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cast",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Countries",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Creators",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "MetaVersion",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Networks",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "Cast",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "Countries",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "Directors",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "MetaVersion",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "OriginalLanguage",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "Studios",
                table: "Movies");

            migrationBuilder.DropColumn(
                name: "HiddenHomeSections",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Tabs",
                table: "AspNetUsers");
        }
    }
}
