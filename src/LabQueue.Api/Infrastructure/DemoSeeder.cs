using System.Security.Cryptography;
using System.Text;
using LabQueue.Api.Auth;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace LabQueue.Api.Infrastructure;

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Off unless explicitly turned on. Set <c>Demo__Seed=true</c> to enable.</summary>
    public bool Seed { get; set; }

    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional, and never published. Nothing in the seed needs an admin identity — rows go
    /// in through the DbContext — so this exists only so the deployment has an operator
    /// account, not so visitors have one.
    /// </summary>
    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}

/// <summary>
/// Seeds the small demo dataset the hosted deployment shows a visitor: six resources, a
/// maintenance window, and a few upcoming bookings.
///
/// This is not the benchmark seed. <c>db/seed/*.sql</c> builds 500k reservations for the
/// local measurement rig and is never pointed at the hosted database.
///
/// It runs on every boot and is idempotent, which is the point: a free-tier database that
/// gets reset, or a redeploy months from now, heals itself rather than waiting for someone
/// to remember a manual step.
/// </summary>
public static class DemoSeeder
{
    private const string GatedCertificationCode = "BSL2";

    private static readonly (string Code, string Name, string Description)[] Certifications =
    [
        ("BSL2",     "Biosafety Level 2",        "Handling of BSL-2 biological agents."),
        ("LASER-3B", "Class 3B Laser Operation", "Operation of Class 3B laser systems."),
        ("NMR-OP",   "NMR Operator",             "Independent operation of NMR spectrometers."),
        ("CRYO",     "Cryogenics Handling",      "Safe handling of liquid nitrogen and helium.")
    ];

    // Four plainly bookable, one certification-gated, one retired — enough for a visitor to
    // meet every booking rule without wading through two hundred instruments.
    private static readonly (string Code, string Name, string Location, bool Gated, ResourceStatus Status)[] Resources =
    [
        ("NMR-600",   "600 MHz NMR Spectrometer",   "Chemistry, Room 114",  false, ResourceStatus.Active),
        ("CENT-U20",  "Ultracentrifuge U20",        "Biology, Room 002",    false, ResourceStatus.Active),
        ("AUTO-CLV",  "Autoclave (large chamber)",  "Shared Services, B1",  false, ResourceStatus.Active),
        ("SPEC-UV1",  "UV-Vis Spectrophotometer",   "Chemistry, Room 118",  false, ResourceStatus.Active),
        ("BSC-2A",    "Class II Biosafety Cabinet", "Biology, Room 210",    true,  ResourceStatus.Active),
        ("LASER-OLD", "Argon Laser Bench (retired)", "Physics, Room 31",    false, ResourceStatus.Retired)
    ];

    public static async Task SeedDemoDataAsync(this WebApplication app)
    {
        var options = app.Configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();

        if (!options.Seed)
        {
            return;
        }

        // The test fixture sets its environment variables process-wide and never clears
        // them, so a stray Demo__Seed on a developer machine would quietly add rows to every
        // test class's database and surface as booking-rule tests failing intermittently.
        // Refuse rather than let that be discovered as flakiness.
        if (app.Environment.EnvironmentName == "Testing")
        {
            throw new InvalidOperationException(
                "Demo:Seed is true while ASPNETCORE_ENVIRONMENT is 'Testing'. The demo seed must "
                + "never run against a test database - it would inject resources and reservations "
                + "into fixtures that assert on exact counts. Unset Demo__Seed.");
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Email)) missing.Add("Demo__Email");
        if (string.IsNullOrWhiteSpace(options.Password)) missing.Add("Demo__Password");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Demo:Seed is true but {string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} "
                + "not set. The demo account cannot be created without them. See .env.example.");
        }

        if (!string.IsNullOrWhiteSpace(options.AdminEmail) ^ !string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            throw new InvalidOperationException(
                "Demo:AdminEmail and Demo:AdminPassword must be set together or not at all.");
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordHashing>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DemoSeeder));

        await EnsureCertificationsAsync(db);
        await EnsureResourcesAsync(db);

        var member = await EnsureUserAsync(db, passwords, options.Email!, options.DisplayName ?? "Demo Researcher",
            options.Password!, UserRole.Member);

        if (!string.IsNullOrWhiteSpace(options.AdminEmail))
        {
            await EnsureUserAsync(db, passwords, options.AdminEmail!, "Demo Operations",
                options.AdminPassword!, UserRole.Admin);
        }

        await EnsureGatedCertificationHeldAsync(db, member);
        await EnsureUpcomingMaintenanceAsync(db);
        var created = await EnsureUpcomingReservationsAsync(db, member);

        await db.SaveChangesAsync();

        logger.LogWarning(
            "Demo seed ran: environment {Environment}, demo account {Email}, {Created} reservation(s) created.",
            app.Environment.EnvironmentName, options.Email, created);
    }

    // ---------------------------------------------------------------- pieces

    private static async Task EnsureCertificationsAsync(LabQueueDbContext db)
    {
        var existing = await db.Certifications.Select(c => c.Code).ToListAsync();

        foreach (var (code, name, description) in Certifications.Where(c => !existing.Contains(c.Code)))
        {
            db.Certifications.Add(new Certification
            {
                Id = StableId($"labqueue:demo:certification:{code}"),
                Code = code,
                Name = name,
                Description = description
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task EnsureResourcesAsync(LabQueueDbContext db)
    {
        var existing = await db.Resources.Select(r => r.Code).ToListAsync();
        var gatedCertificationId = await db.Certifications
            .Where(c => c.Code == GatedCertificationCode)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync();

        foreach (var (code, name, location, gated, status) in Resources.Where(r => !existing.Contains(r.Code)))
        {
            db.Resources.Add(new Resource
            {
                Id = StableId($"labqueue:demo:resource:{code}"),
                Code = code,
                Name = name,
                Location = location,
                RequiredCertificationId = gated ? gatedCertificationId : null,
                Status = status,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<User> EnsureUserAsync(
        LabQueueDbContext db,
        PasswordHashing passwords,
        string email,
        string displayName,
        string password,
        UserRole role)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user is not null)
        {
            return user;
        }

        user = new User
        {
            Id = StableId($"labqueue:demo:user:{email}"),
            Email = email,
            DisplayName = displayName,
            Role = role,
            CreatedAt = DateTime.UtcNow
        };

        // Hashed here, at boot, from an environment variable. Nothing derived from the demo
        // password is ever committed.
        user.PasswordHash = passwords.Hash(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// The demo account holds the gating certification, so it can book every active resource.
    /// A visitor who registers their own account does not, and meets the 403 instead — both
    /// halves of the rule are reachable from the live URL depending on which login you use.
    /// </summary>
    private static async Task EnsureGatedCertificationHeldAsync(LabQueueDbContext db, User member)
    {
        var certificationId = await db.Certifications
            .Where(c => c.Code == GatedCertificationCode)
            .Select(c => c.Id)
            .FirstOrDefaultAsync();

        if (certificationId == Guid.Empty)
        {
            return;
        }

        var held = await db.UserCertifications
            .AnyAsync(uc => uc.UserId == member.Id && uc.CertificationId == certificationId);

        if (held)
        {
            return;
        }

        db.UserCertifications.Add(new UserCertification
        {
            UserId = member.Id,
            CertificationId = certificationId,
            GrantedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    private static async Task EnsureUpcomingMaintenanceAsync(LabQueueDbContext db)
    {
        var resourceId = await db.Resources
            .Where(r => r.Code == "AUTO-CLV")
            .Select(r => r.Id)
            .FirstOrDefaultAsync();

        if (resourceId == Guid.Empty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var alreadyScheduled = await db.MaintenanceWindows
            .AnyAsync(m => m.ResourceId == resourceId && m.During.UpperBound > now);

        if (alreadyScheduled)
        {
            return;
        }

        var day = NextWeekdayUtc(1);

        db.MaintenanceWindows.Add(new MaintenanceWindow
        {
            Id = Guid.CreateVersion7(),
            ResourceId = resourceId,
            During = TimeWindow.ClosedOpen(day.AddHours(8), day.AddHours(12)),
            Reason = "Quarterly chamber service",
            CreatedAt = now
        });
    }

    /// <summary>
    /// Keeps a few bookings in the future rather than seeding fixed dates once. Dates pinned
    /// at first boot would be in the past by the time anyone clicks the link, and an empty
    /// demo reads as a broken one.
    /// </summary>
    private static async Task<int> EnsureUpcomingReservationsAsync(LabQueueDbContext db, User member)
    {
        var now = DateTime.UtcNow;

        var hasUpcoming = await db.Reservations.AnyAsync(r =>
            r.UserId == member.Id
            && r.Status == ReservationStatus.Confirmed
            && r.During.UpperBound > now);

        if (hasUpcoming)
        {
            return 0;
        }

        var day = NextWeekdayUtc(1);

        // NMR-600 and CENT-U20 carry the bookings. SPEC-UV1 and BSC-2A are deliberately left
        // clear so a visitor's first booking succeeds; re-posting one of the windows below is
        // how you reach the 409 path, and the conflict counter, on purpose.
        (string Code, int StartHour, int EndHour)[] slots =
        [
            ("NMR-600",  9, 11),
            ("NMR-600", 13, 15),
            ("CENT-U20", 10, 12)
        ];

        var codes = slots.Select(s => s.Code).Distinct().ToArray();

        var resources = await db.Resources
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id);

        var created = 0;

        foreach (var (code, startHour, endHour) in slots)
        {
            if (!resources.TryGetValue(code, out var resourceId))
            {
                continue;
            }

            db.Reservations.Add(new Reservation
            {
                Id = Guid.CreateVersion7(),
                ResourceId = resourceId,
                UserId = member.Id,
                During = TimeWindow.ClosedOpen(day.AddHours(startHour), day.AddHours(endHour)),
                Status = ReservationStatus.Confirmed,
                CreatedAt = now
            });

            created++;
        }

        return created;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Midnight UTC, <paramref name="daysAhead"/> days from today, skipping to Monday over a weekend.</summary>
    private static DateTime NextWeekdayUtc(int daysAhead)
    {
        var day = DateTime.UtcNow.Date.AddDays(daysAhead);

        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }

        return DateTime.SpecifyKind(day, DateTimeKind.Utc);
    }

    /// <summary>
    /// Same derivation the SQL seed uses — <c>md5(key)::uuid</c> — so ids are stable across
    /// reseeds and a wiped database comes back with the same resource ids it had before.
    /// MD5 here is an identifier function, not a security primitive.
    /// </summary>
    private static Guid StableId(string key)
        => new(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key))));
}
