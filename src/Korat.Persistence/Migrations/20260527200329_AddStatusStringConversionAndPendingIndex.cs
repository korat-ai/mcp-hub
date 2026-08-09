using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusStringConversionAndPendingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Grants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "AccessRequests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            // G11 / C4: at most one Pending access request per (SpaceId, AgentClientId, McpServerId).
            // The EF scaffolder did not emit this CreateIndex because the InitialCreate
            // designer snapshot was generated before HasConversion<string>() landed and
            // EF Core silently dropped the filtered-index diff. Added manually so a
            // fresh Postgres deploy enforces the invariant at the database level.
            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_SpaceId_AgentClientId_McpServerId",
                table: "AccessRequests",
                columns: new[] { "SpaceId", "AgentClientId", "McpServerId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_SpaceId_AgentClientId_McpServerId",
                table: "AccessRequests");


            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Sessions",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "Grants",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                table: "AccessRequests",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
