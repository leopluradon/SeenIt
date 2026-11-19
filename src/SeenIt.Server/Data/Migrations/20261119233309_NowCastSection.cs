using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class NowCastSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Cast of what you're watching" is a Home section you add: start it switched off for existing accounts too.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"HiddenHomeSections\" = array_append(\"HiddenHomeSections\", 'now-cast') WHERE NOT ('now-cast' = ANY(\"HiddenHomeSections\"));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
