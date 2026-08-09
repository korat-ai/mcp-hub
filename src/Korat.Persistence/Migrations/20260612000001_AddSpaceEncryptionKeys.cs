using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpaceEncryptionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #55: per-space envelope DEK table.
            // Additive CreateTable only — rolling-deploy safe (old silos simply ignore the table).
            // PK = (SpaceId, DekVersion): composite, no auto-increment.
            // NO FK to Spaces — crypto-shred is explicit/audited, not cascade-delete triggered.

            migrationBuilder.CreateTable(
                name: "SpaceEncryptionKeys",
                columns: table => new
                {
                    SpaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DekVersion = table.Column<int>(type: "integer", nullable: false),
                    KekId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    // WrapNonce: 12-byte AES-GCM nonce. bytea[12].
                    WrapNonce = table.Column<byte[]>(type: "bytea", maxLength: 12, nullable: false),
                    // WrappedDek: 48-byte blob (32B ciphertext + 16B GCM tag). bytea[48].
                    WrappedDek = table.Column<byte[]>(type: "bytea", maxLength: 48, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceEncryptionKeys", x => new { x.SpaceId, x.DekVersion });
                });

            // Index for "list all DEKs for a space" (used by shred + rotation).
            migrationBuilder.CreateIndex(
                name: "IX_SpaceEncryptionKeys_SpaceId",
                table: "SpaceEncryptionKeys",
                column: "SpaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SpaceEncryptionKeys");
        }
    }
}
