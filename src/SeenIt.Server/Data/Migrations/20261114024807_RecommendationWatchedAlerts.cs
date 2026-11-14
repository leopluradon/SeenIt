using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecommendationWatchedAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_Type_TmdbId_SeasonNumber_EpisodeNumber",
                table: "Notifications");

            migrationBuilder.AddColumn<Guid>(
                name: "WatchedRecommendationId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_Type_TmdbId_SeasonNumber_EpisodeNumber~",
                table: "Notifications",
                columns: new[] { "UserId", "Type", "TmdbId", "SeasonNumber", "EpisodeNumber", "RecommendationId", "WatchedRecommendationId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_WatchedRecommendationId",
                table: "Notifications",
                column: "WatchedRecommendationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Notifications_Recommendations_WatchedRecommendationId",
                table: "Notifications",
                column: "WatchedRecommendationId",
                principalTable: "Recommendations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Notifications_Recommendations_WatchedRecommendationId",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_Type_TmdbId_SeasonNumber_EpisodeNumber~",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_WatchedRecommendationId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "WatchedRecommendationId",
                table: "Notifications");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_Type_TmdbId_SeasonNumber_EpisodeNumber",
                table: "Notifications",
                columns: new[] { "UserId", "Type", "TmdbId", "SeasonNumber", "EpisodeNumber" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
