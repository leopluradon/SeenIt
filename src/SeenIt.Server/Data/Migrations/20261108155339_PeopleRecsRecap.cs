using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PeopleRecsRecap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecommendationId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PersonFollows",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProfilePath = table.Column<string>(type: "text", nullable: true),
                    Department = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonFollows", x => new { x.UserId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_PersonFollows_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonUpcoming",
                columns: table => new
                {
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TmdbId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PosterPath = table.Column<string>(type: "text", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: true),
                    Role = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonUpcoming", x => new { x.PersonId, x.Type, x.TmdbId });
                });

            migrationBuilder.CreateTable(
                name: "Recommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    TmdbId = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recommendations_AspNetUsers_FromId",
                        column: x => x.FromId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Recommendations_AspNetUsers_ToId",
                        column: x => x.ToId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecommendationId",
                table: "Notifications",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonFollows_PersonId",
                table: "PersonFollows",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonUpcoming_Date",
                table: "PersonUpcoming",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_FromId_CreatedAt",
                table: "Recommendations",
                columns: new[] { "FromId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Recommendations_ToId_CreatedAt",
                table: "Recommendations",
                columns: new[] { "ToId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Recommendations_RecommendationId",
                table: "Notifications",
                column: "RecommendationId",
                principalTable: "Recommendations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_Recommendations_RecommendationId",
                table: "Notifications");

            migrationBuilder.DropTable(
                name: "PersonFollows");

            migrationBuilder.DropTable(
                name: "PersonUpcoming");

            migrationBuilder.DropTable(
                name: "Recommendations");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_RecommendationId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "RecommendationId",
                table: "Notifications");
        }
    }
}
