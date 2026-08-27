using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LabQueue.Api.Auth;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace LabQueue.Tests.Infrastructure;

/// <summary>
/// A real Postgres 17 and a real request pipeline, one per test class.
///
/// Deliberately absent: any shared transaction, any connection-string pool ceiling, any
/// substitution of the database provider. All three would serialise concurrent requests,
/// and the concurrency reproducer exists precisely to observe what happens when fifty of
/// them run at once.
/// </summary>
public sealed class LabQueueApiFixture : IAsyncLifetime
{
    /// <summary>
    /// Program.cs refuses to start on a Jwt:Key under 32 bytes. It normally comes from
    /// appsettings.Development.json, which is gitignored and therefore absent on CI, so
    /// the fixture supplies its own rather than depending on a file that exists on only
    /// one machine.
    /// </summary>
    private const string JwtKey = "labqueue-test-signing-key-not-a-secret-0123456789";

    public const string SeedPassword = "correct-horse-battery";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        // Comfortably above the fifty concurrent connections the reproducer opens.
        .WithCommand("-c", "max_connections=200")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public HttpClient Anonymous { get; private set; } = null!;
    public HttpClient Member { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;

    public Guid MemberId { get; private set; }
    public Guid AdminId { get; private set; }
    public string MemberEmail { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Set through the environment rather than ConfigureAppConfiguration: Program.cs
        // reads Jwt:Key before builder.Build(), so configuration sources added via the web
        // host builder arrive too late to be seen. Safe because test-class parallelisation
        // is disabled assembly-wide - see AssemblyInfo.cs.
        Environment.SetEnvironmentVariable("ConnectionStrings__LabQueue", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("Serilog__MinimumLevel__Default", "Warning");

        _factory = new WebApplicationFactory<Program>();
        Anonymous = _factory.CreateClient();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        await db.Database.MigrateAsync();

        var passwords = scope.ServiceProvider.GetRequiredService<PasswordHashing>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();

        var member = NewUser("member@labqueue.test", "Test Member", UserRole.Member, passwords);
        var admin = NewUser("admin@labqueue.test", "Test Admin", UserRole.Admin, passwords);
        db.Users.AddRange(member, admin);
        await db.SaveChangesAsync();

        MemberId = member.Id;
        AdminId = admin.Id;
        MemberEmail = member.Email;

        // Tokens are issued once here and reused for every request in the class. The
        // reproducer must not authenticate inside its fifty tasks: PBKDF2 at 100k
        // iterations costs tens of milliseconds, which would stagger the simultaneous
        // requests by far more than the race window is wide.
        Member = ClientFor(tokens.Issue(member).Token);
        Admin = ClientFor(tokens.Issue(admin).Token);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    private HttpClient ClientFor(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static User NewUser(string email, string displayName, UserRole role, PasswordHashing passwords)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = displayName,
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwords.Hash(user, SeedPassword);
        return user;
    }

    // ------------------------------------------------------------------ test data

    /// <summary>
    /// Seeds a resource straight through the DbContext. The admin API can only create
    /// active resources, so a retired one for booking rule 1 has to be made this way.
    /// </summary>
    public async Task<Guid> CreateResourceAsync(
        ResourceStatus status = ResourceStatus.Active,
        Guid? requiredCertificationId = null)
    {
        var resource = new Resource
        {
            Id = Guid.CreateVersion7(),
            Code = "TEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Name = "Test Resource",
            Location = "Building T, Room 1",
            RequiredCertificationId = requiredCertificationId,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        await WithDbAsync(db =>
        {
            db.Resources.Add(resource);
            return Task.CompletedTask;
        });

        return resource.Id;
    }

    public async Task<Guid> CreateCertificationAsync()
    {
        var certification = new Certification
        {
            Id = Guid.CreateVersion7(),
            Code = "CERT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Name = "Test Certification"
        };

        await WithDbAsync(db =>
        {
            db.Certifications.Add(certification);
            return Task.CompletedTask;
        });

        return certification.Id;
    }

    public async Task<Guid> CreateUserAsync(string email)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordHashing>();

        var user = NewUser(email, "Other Member", UserRole.Member, passwords);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public HttpClient ClientForUser(Guid userId)
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var user = db.Users.Single(u => u.Id == userId);
        return ClientFor(tokens.Issue(user).Token);
    }

    /// <summary>
    /// Goes through the admin endpoint rather than the DbContext, so the maintenance rule
    /// is set up the same way gate02.sh sets it up.
    /// </summary>
    public async Task CreateMaintenanceWindowAsync(Guid resourceId, DateTimeOffset from, DateTimeOffset to)
    {
        var response = await Admin.PostAsJsonAsync(
            "/maintenance-windows",
            new { resourceId, from, to, reason = "Test maintenance" });

        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Could not create a maintenance window: {(int)response.StatusCode} "
                + await response.Content.ReadAsStringAsync());
        }
    }

    public async Task<int> CountConfirmedAsync(Guid resourceId)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        return await db.Reservations.CountAsync(
            r => r.ResourceId == resourceId && r.Status == ReservationStatus.Confirmed);
    }

    public async Task WithDbAsync(Func<LabQueueDbContext, Task> action)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LabQueueDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ requests

    public static HttpContent BookingBody(Guid resourceId, DateTimeOffset from, DateTimeOffset to)
        => JsonContent.Create(new { resourceId, from, to });

    public static Task<HttpResponseMessage> BookAsync(
        HttpClient client, Guid resourceId, DateTimeOffset from, DateTimeOffset to)
        => client.PostAsync("/reservations", BookingBody(resourceId, from, to));

    public static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }
}
