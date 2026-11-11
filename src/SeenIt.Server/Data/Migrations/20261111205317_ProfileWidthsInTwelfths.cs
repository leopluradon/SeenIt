using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeenIt.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProfileWidthsInTwelfths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Profile widget widths were thirds of the row (1-3); they're twelfths now (3-12), so halves fit.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u
                SET "ProfileLayoutJson" = (
                    SELECT jsonb_agg(jsonb_set(e, '{w}', to_jsonb(least(greatest((e->>'w')::int, 1), 3) * 4)) ORDER BY i)::text
                    FROM jsonb_array_elements(u."ProfileLayoutJson"::jsonb) WITH ORDINALITY AS t(e, i))
                WHERE "ProfileLayoutJson" IS NOT NULL AND "ProfileLayoutJson" <> '[]';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
