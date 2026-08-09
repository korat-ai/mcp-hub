using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 032 (#57 Leg 3 C1): tamper-evident audit trail.
            // Additive CreateTable only — rolling-deploy safe (old silos never touch these tables).
            migrationBuilder.CreateTable(
                name: "AuditChainHead",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false),
                    LastHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChainHead", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DetailsJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PrevHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    RowHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Seq);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "Action", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "ActorId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SpaceId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "SpaceId", "OccurredAtUtc" });

            // Seed the genesis chain head: LastSeq = 0, LastHash = SHA256("korat-audit-genesis-v1").
            // Must match Korat.Cloud.Security.Audit.AuditHasher.GenesisHash exactly — the chain
            // verifier seeds from this value when no prune checkpoint exists.
            migrationBuilder.InsertData(
                table: "AuditChainHead",
                columns: new[] { "Id", "LastSeq", "LastHash" },
                values: new object[]
                {
                    1,
                    0L,
                    new byte[]
                    {
                        0x05, 0xfe, 0x77, 0x95, 0x13, 0x5e, 0xf7, 0xb0, 0x8b, 0x5c, 0xd4, 0x45, 0x4b, 0x04, 0x5b, 0x2b,
                        0x67, 0x3b, 0xe9, 0xa3, 0x48, 0xd9, 0x8d, 0x31, 0x78, 0xba, 0x30, 0x59, 0xa8, 0x8e, 0x8b, 0x48
                    }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditChainHead");

            migrationBuilder.DropTable(
                name: "AuditEvents");
        }
    }
}
