using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DecimalRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Value",
                table: "Ratings",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "RatingScale",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Keep anyone's 5-star choice (RatingScale.FiveStars = 1) before the old flag goes.
            migrationBuilder.Sql(@"UPDATE ""AspNetUsers"" SET ""RatingScale"" = 1 WHERE ""FiveStarRatings"";");

            migrationBuilder.DropColumn(
                name: "FiveStarRatings",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RatingScale",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<int>(
                name: "Value",
                table: "Ratings",
                type: "integer",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<bool>(
                name: "FiveStarRatings",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
