using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HomeStatsSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New Home sections (stats widgets) start switched off, so nobody's Home changes until they turn them on.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "HiddenHomeSections" = "HiddenHomeSections" || ARRAY['w-month', 'w-heatmap', 'w-genres', 'w-records']
                WHERE NOT ("HiddenHomeSections" && ARRAY['w-month', 'w-heatmap', 'w-genres', 'w-records']);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
