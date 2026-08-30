using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabQueue.Core.Migrations;

/// <summary>
/// Finding B. The availability query filters on resource_id and on whether the reservation
/// overlaps a requested window. EF Core's foreign-key index covers the first half only, so
/// the plan finds every reservation for the resource and then discards almost all of them
/// in a heap filter — 2,454 rows read to return 23.
///
/// A GiST index over (resource_id, during) indexes both halves. resource_id is a uuid and
/// needs btree_gist to participate, which the initial migration already enables. Partial on
/// status = 'confirmed' because cancelled rows never take part in an availability answer.
/// </summary>
public partial class AddReservationOverlapIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX ix_reservations_resource_during
              ON reservations USING gist (resource_id, during)
              WHERE status = 'confirmed';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_reservations_resource_during;");
    }
}
