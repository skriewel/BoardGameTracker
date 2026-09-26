using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardGameTracker.Core.Datastore.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RemoveInvalidBggPlaySessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "Sessions"
                WHERE "BggPlayId" IS NOT NULL
                  AND (
                      "Start" = '-infinity'::timestamptz
                      OR "Start"::date = DATE '1900-01-01'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Destructive cleanup of invalid imported BGG sessions cannot be reversed.
        }
    }
}
