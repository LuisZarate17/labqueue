using System.Text;
using FluentValidation;
using LabQueue.Api.Auth;
using LabQueue.Api.Endpoints;
using LabQueue.Api.Infrastructure;
using LabQueue.Api.Observability;
using LabQueue.Core.Data;
using LabQueue.Core.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Retry is configured on the context rather than around the one call site that needs it,
// for the reason advisory locks were rejected in DECISIONS.md: correctness that every write
// path has to remember is correctness that will eventually be forgotten. Unlike SERIALIZABLE
// — rejected for pricing the whole application to fix one path — a retry policy costs
// nothing on the requests that do not fail.
//
// 40P01 is what makes this necessary. Fifty callers contending for one slot mostly resolve
// as exclusion violations, but Postgres sometimes resolves that contention as a deadlock
// instead, and the victim's transaction is gone. Retrying gives it a second attempt against
// a settled table, where it either wins the slot or loses it cleanly to 23P01.
//
// 40P01 is already in Npgsql's transient set; naming it here is documentation, not a fix.
builder.Services.AddDbContext<LabQueueDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("LabQueue"),
        npgsql => npgsql.EnableRetryOnFailure(
            maxRetryCount: 5,
            // The default backoff reaches 30s. A booking request that has already lost a
            // deadlock should re-contend in milliseconds or give up, not park a connection.
            maxRetryDelay: TimeSpan.FromMilliseconds(250),
            errorCodesToAdd: [PostgresErrorCodes.DeadlockDetected])));

builder.AddLabQueueObservability();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<PasswordHashing>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || Encoding.UTF8.GetByteCount(jwt.Key) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must be configured with at least 32 bytes of key material. " +
        "Set it as the Jwt__Key environment variable or in appsettings.Development.json. " +
        "See .env.example.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = LabQueueClaims.Subject,
            RoleClaimType = LabQueueClaims.Role
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin", policy => policy.RequireRole("admin"));

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["requestId"] = context.HttpContext.RequestId();
    };
});

var app = builder.Build();

app.UseRequestId();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// The landing route exists so a stranger who clicks the live URL lands on something that
// tells them how to use it, rather than a 404.
app.MapGet("/", (IConfiguration configuration) =>
{
    var demo = configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();

    // Only the member account is ever published. Creating maintenance windows is an admin
    // capability and a maintenance window blocks bookings on a resource, so a public admin
    // login would let any visitor take the demo down for everyone after them.
    object? demoCredentials = demo.Seed
        ? new
        {
            email = demo.Email,
            password = demo.Password,
            role = "member",
            howTo = "POST /auth/login with these credentials, then GET /resources and "
                    + "POST /reservations with { resourceId, from, to }. Or register your own "
                    + "account at POST /auth/register."
        }
        : null;

    return Results.Ok(new
    {
        name = "labqueue",
        description = "Lab equipment reservation API — book instruments for a window of time, "
                      + "with certification gating and maintenance windows.",
        source = "https://github.com/LuisZarate17/labqueue",
        health = "/health",
        demo = demoCredentials,
        note = "Hosted on free tiers. The first request after a period of inactivity has to wake "
               + "both the web service and the database, so it is slow; everything after it is not."
    });
})
   .AllowAnonymous()
   .WithName("Landing");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous()
   .WithName("Health");

app.MapAuthEndpoints();
app.MapResourceEndpoints();
app.MapReservationEndpoints();
app.MapAdminEndpoints();

// Migrations are deliberately not applied here — they run out of band via
// scripts/db-migrate.ps1. Demo data self-heals on boot; schema changes stay deliberate.
await app.SeedDemoDataAsync();

app.Run();

public partial class Program;
