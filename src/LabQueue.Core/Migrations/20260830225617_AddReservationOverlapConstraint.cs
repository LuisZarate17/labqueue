using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabQueue.Core.Migrations;

/// <summary>
/// Finding A. The booking path checks for an overlapping reservation and then inserts, with
/// nothing holding the gap between the two statements. Under READ COMMITTED — the Postgres
/// default, and what EF Core gives you — concurrent callers all see an empty result and all
/// insert. Measured at 50 concurrent requests for one slot: 50 confirmed reservations.
///
/// An exclusion constraint moves the guarantee into the database, where no write path can
/// bypass it, including a psql session. Partial on status = 'confirmed' so that cancelling
/// a reservation frees the slot.
///
/// Note this constraint builds its own GiST index over the same predicate as
/// ix_reservations_resource_during, which the next migration therefore drops.
/// </summary>
public partial class AddReservationOverlapConstraint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE reservations
              ADD CONSTRAINT reservations_no_overlap
              EXCLUDE USING gist (resource_id WITH =, during WITH &&)
              WHERE (status = 'confirmed');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE reservations DROP CONSTRAINT IF EXISTS reservations_no_overlap;");
    }
}
