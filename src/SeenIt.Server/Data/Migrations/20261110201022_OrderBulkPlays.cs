using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderBulkPlays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Mark season watched" used to record episodes in whatever order the database returned them, and
            // History breaks ties by when a play was recorded, so a season could show e.g. E14 after E22.
            // Re-stamp each such batch (same user, show and time, recorded within the same second) in
            // season/episode order, a microsecond apart.
            migrationBuilder.Sql("""
                UPDATE "WatchEvents" w
                SET "CreatedAt" = b.base + b.rn * interval '1 microsecond'
                FROM (
                    SELECT "Id",
                           min("CreatedAt") OVER g AS base,
                           row_number() OVER (g ORDER BY "SeasonNumber", "EpisodeNumber") - 1 AS rn,
                           count(*) OVER g AS n
                    FROM "WatchEvents"
                    WHERE "Type" = 2
                    WINDOW g AS (PARTITION BY "UserId", "TmdbId", "WatchedAt", date_trunc('second', "CreatedAt"))
                ) b
                WHERE w."Id" = b."Id" AND b.n > 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
