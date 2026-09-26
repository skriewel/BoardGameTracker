using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BoardGameTracker.Core.Datastore.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBggPlayImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BggPlayId",
                table: "Sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BggPlayIndex",
                table: "Sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_BggPlayId_BggPlayIndex",
                table: "Sessions",
                columns: new[] { "BggPlayId", "BggPlayIndex" },
                unique: true,
                filter: "\"BggPlayId\" IS NOT NULL AND \"BggPlayIndex\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_BggPlayId_BggPlayIndex",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BggPlayId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BggPlayIndex",
                table: "Sessions");
        }
    }
}
