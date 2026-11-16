using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Library",
                table: "Library");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "Library",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "Library",
                type: "integer",
                nullable: true);

            // Existing rows all got the empty Guid above; give each its own before it becomes the key.
            migrationBuilder.Sql("UPDATE \"Library\" SET \"Id\" = gen_random_uuid();");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Library",
                table: "Library",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_Library_UserId_Type_TmdbId_SeasonNumber_Format",
                table: "Library",
                columns: new[] { "UserId", "Type", "TmdbId", "SeasonNumber", "Format" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Library",
                table: "Library");

            migrationBuilder.DropIndex(
                name: "IX_Library_UserId_Type_TmdbId_SeasonNumber_Format",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "Library");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Library",
                table: "Library",
                columns: new[] { "UserId", "Type", "TmdbId" });
        }
    }
}
