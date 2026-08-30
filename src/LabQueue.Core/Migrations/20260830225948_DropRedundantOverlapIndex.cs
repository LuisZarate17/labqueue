using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabQueue.Core.Migrations;

/// <summary>
/// The exclusion constraint added in the previous migration builds its own GiST index over
/// (resource_id, during) partial on status = 'confirmed' — the same predicate as
/// ix_reservations_resource_during. The two were 65 MB each, so half of that 130 MB was
/// paying for nothing, on every insert as well as on disk.
///
/// Verified before dropping: with ix_reservations_resource_during gone, the availability
/// query still plans as a Bitmap Index Scan, on reservations_no_overlap, with the same
/// Index Cond covering both halves of the predicate. See docs/findings/finding-b-after.txt.
///
/// IX_reservations_resource_id is deliberately left alone. It is EF Core's foreign-key
/// index, it is not partial on status, and it serves lookups the constraint's index does
/// not.
/// </summary>
public partial class DropRedundantOverlapIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_reservations_resource_during;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX IF NOT EXISTS ix_reservations_resource_during
              ON reservations USING gist (resource_id, during)
              WHERE status = 'confirmed';
            """);
    }
}
