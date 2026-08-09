using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConsumerAgentClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsumerAgentClientId",
                table: "agents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_ConsumerAgentClientId",
                table: "agents",
                column: "ConsumerAgentClientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_ConsumerAgentClientId",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "ConsumerAgentClientId",
                table: "agents");
        }
    }
}
