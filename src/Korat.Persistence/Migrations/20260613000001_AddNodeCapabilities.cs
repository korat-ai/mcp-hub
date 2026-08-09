using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Korat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // cloud-m9: persist node capabilities so NodeGrain.OnActivateAsync can repopulate
            // the volatile _capabilities set on cross-silo activations. Nullable — old rows get
            // null (treated as no capabilities, same as before). Rolling-deploy safe: old cloud
            // instances that do not yet write this column read null and behave exactly as before.
            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "Nodes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "Nodes");
        }
    }
}
