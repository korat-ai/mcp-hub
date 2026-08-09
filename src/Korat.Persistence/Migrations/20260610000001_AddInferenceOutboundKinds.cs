using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInferenceOutboundKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // T3/T4 additive nullable columns on InferencePoints.
            // All nullable → rolling-deploy safe (old silos write NULL for new columns; new silos read them).
            // EncryptedSecret is text (unbounded) — ASP.NET DataProtection ciphertext can exceed 4 KB.

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "InferencePoints",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "InferencePoints",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthHeaderName",
                table: "InferencePoints",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretHint",
                table: "InferencePoints",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedSecret",
                table: "InferencePoints",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "EncryptedSecret", table: "InferencePoints");
            migrationBuilder.DropColumn(name: "SecretHint",      table: "InferencePoints");
            migrationBuilder.DropColumn(name: "AuthHeaderName",  table: "InferencePoints");
            migrationBuilder.DropColumn(name: "BaseUrl",         table: "InferencePoints");
            migrationBuilder.DropColumn(name: "Provider",        table: "InferencePoints");
        }
    }
}
