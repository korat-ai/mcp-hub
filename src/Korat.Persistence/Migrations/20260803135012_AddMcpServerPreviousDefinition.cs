using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpServerPreviousDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DefinitionChangedAt",
                table: "McpServers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousLaunchArguments",
                table: "McpServers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousLaunchCommand",
                table: "McpServers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefinitionChangedAt",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "PreviousLaunchArguments",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "PreviousLaunchCommand",
                table: "McpServers");
        }
    }
}
