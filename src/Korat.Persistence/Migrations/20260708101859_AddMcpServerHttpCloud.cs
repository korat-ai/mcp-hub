using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpServerHttpCloud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthHeaderName",
                table: "McpServers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthMode",
                table: "McpServers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedSecret",
                table: "McpServers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteUrl",
                table: "McpServers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretHint",
                table: "McpServers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthHeaderName",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "AuthMode",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "EncryptedSecret",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "RemoteUrl",
                table: "McpServers");

            migrationBuilder.DropColumn(
                name: "SecretHint",
                table: "McpServers");
        }
    }
}
