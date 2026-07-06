using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Application.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Deliberately empty: the generated ConfigureEntities now declares the domain entities' Guid primary
    /// keys client-assigned (ValueGeneratedNever) — a model-only change with no schema effect. The migration
    /// exists so the snapshot matches the model again (EF's pending-model-changes check would otherwise
    /// refuse to migrate).
    /// </remarks>
    public partial class ClientAssignedGuidKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
