using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "room_messages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SenderAgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContentCipher = table.Column<string>(type: "text", nullable: false),
                    Mentions = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "room_participants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoomId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_participants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rooms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpaceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerPrincipalUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rooms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_room_messages_RoomId_Sequence",
                table: "room_messages",
                columns: new[] { "RoomId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_room_participants_RoomId_AgentId",
                table: "room_participants",
                columns: new[] { "RoomId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rooms_SpaceId_OwnerPrincipalUserId",
                table: "rooms",
                columns: new[] { "SpaceId", "OwnerPrincipalUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "room_messages");

            migrationBuilder.DropTable(
                name: "room_participants");

            migrationBuilder.DropTable(
                name: "rooms");
        }
    }
}
