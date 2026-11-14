using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing alerts get their kind (AlertKind: Episode 0, Movie 1, Person 2, Recommendation 3, RecWatched 4).
            // Work from people you follow is the release alert whose detail names them ("Out today · Christopher Nolan").
            migrationBuilder.Sql("""
                UPDATE "Notifications" SET "Kind" = CASE
                    WHEN "RecommendationId" IS NOT NULL THEN 3
                    WHEN "WatchedRecommendationId" IS NOT NULL THEN 4
                    WHEN "Type" IN (0, 1) AND "Detail" LIKE '% · %' THEN 2
                    WHEN "Type" = 0 THEN 1
                    ELSE 0 END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Notifications");
        }
    }
}
