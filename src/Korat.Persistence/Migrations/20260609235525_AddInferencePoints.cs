using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInferencePoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "Spaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InferenceEndpointKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InferencePointId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceEndpointKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InferencePoints",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublisherNodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelsJson = table.Column<string>(type: "text", nullable: false),
                    DefaultModel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferencePoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_Slug",
                table: "Spaces",
                column: "Slug",
                unique: true,
                filter: "\"Slug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InferenceEndpointKeys_KeyHash",
                table: "InferenceEndpointKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InferenceEndpointKeys_SpaceId_InferencePointId",
                table: "InferenceEndpointKeys",
                columns: new[] { "SpaceId", "InferencePointId" });

            migrationBuilder.CreateIndex(
                name: "IX_InferencePoints_PublisherNodeId",
                table: "InferencePoints",
                column: "PublisherNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_InferencePoints_SpaceId_AgentName",
                table: "InferencePoints",
                columns: new[] { "SpaceId", "AgentName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InferenceEndpointKeys");

            migrationBuilder.DropTable(
                name: "InferencePoints");

            migrationBuilder.DropIndex(
                name: "IX_Spaces_Slug",
                table: "Spaces");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "Spaces");
        }
    }
}
