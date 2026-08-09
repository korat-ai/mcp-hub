using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <summary>
    /// Р26: <c>Grants.ApprovedDefinitionDigest</c> — the digest of the MCP server definition as it
    /// stood when the owner approved. Admission refuses to apply a permission whose digest does not
    /// match the server's current definition, so a re-publish under an existing name can no longer
    /// inherit an approval given for a different launch command.
    ///
    /// <para><b>Existing rows get an empty digest, and that is intentional.</b> Empty is treated as
    /// a mismatch, so every permission approved before this migration must be approved once more.
    /// Backfilling the current definition instead would assert "whatever is configured now is what
    /// was approved" — precisely the assumption this decision exists to stop making. One re-approval
    /// per existing permission is the honest price.</para>
    ///
    /// <para><b>What this migration deliberately does NOT do.</b> The scaffolder also wanted to drop
    /// fifteen tables (agents, rooms, threads, channel_bindings, InferencePoints, Invite, Feedback,
    /// WaitlistSignups, BootstrapState, CryptoSessions, …) and the MagicLinkToken.InviteCode column,
    /// because the model no longer has those entities after the open-source triage removals. Those
    /// drops were removed by hand: the recorded decision is that removed tables stay in migration
    /// history and in the live databases. Deleting them here would destroy data on korat and
    /// korat-dev as a side effect of adding a column — a data-loss migration disguised as a schema
    /// tweak. The model snapshot legitimately no longer describes them, so the tables simply remain
    /// as orphans until someone decides to drop them deliberately, in a migration that says so.</para>
    /// </summary>
    public partial class AddGrantApprovedDefinitionDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovedDefinitionDigest",
                table: "Grants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedDefinitionDigest",
                table: "Grants");
        }
    }
}
