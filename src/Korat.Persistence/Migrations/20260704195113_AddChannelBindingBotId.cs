using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelBindingBotId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BotId",
                table: "channel_bindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Bot-id dedup, race-safe (review fix): a Telegram webhook is one-per-bot GLOBALLY, so
            // (Kind, BotId) is unique across ALL Spaces. PARTIAL (WHERE "BotId" IS NOT NULL) makes
            // it additive over legacy rows (BotId NULL) and rolling-deploy-safe.
            migrationBuilder.CreateIndex(
                name: "IX_channel_bindings_Kind_BotId",
                table: "channel_bindings",
                columns: new[] { "Kind", "BotId" },
                unique: true,
                filter: "\"BotId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channel_bindings_Kind_BotId",
                table: "channel_bindings");

            migrationBuilder.DropColumn(
                name: "BotId",
                table: "channel_bindings");
        }
    }
}
