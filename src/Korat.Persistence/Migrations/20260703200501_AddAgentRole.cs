using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "agents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "agents");
        }
    }
}
