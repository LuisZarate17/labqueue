using LabQueue.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace LabQueue.Core.Data;

public class LabQueueDbContext(DbContextOptions<LabQueueDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Certification> Certifications => Set<Certification>();
    public DbSet<UserCertification> UserCertifications => Set<UserCertification>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Indexes are declared explicitly in the entity configurations rather than
        // inferred from foreign keys, so the migration is the complete description
        // of what this schema indexes.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LabQueueDbContext).Assembly);
    }
}
