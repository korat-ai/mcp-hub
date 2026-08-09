using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMagicLinkTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "MagicLinkToken",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkToken_TokenHash",
                table: "MagicLinkToken",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MagicLinkToken_TokenHash",
                table: "MagicLinkToken");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "MagicLinkToken");
        }
    }
}
