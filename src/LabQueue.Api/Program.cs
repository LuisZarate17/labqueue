using System.Text;
using FluentValidation;
using LabQueue.Api.Auth;
using LabQueue.Api.Endpoints;
using LabQueue.Api.Infrastructure;
using LabQueue.Api.Observability;
using LabQueue.Api.OpenApi;
using LabQueue.Core.Data;
using LabQueue.Core.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// Deliberately no EnableRetryOnFailure. It was tried for the 40P01 deadlock described in
// DECISIONS.md section 7 and made things measurably worse: EF Core's execution strategy
// retries SaveChangesAsync alone, so fifty deadlock victims all re-attempt the INSERT and
// re-contend on the same constraint every few hundred milliseconds. The lock queue stops
// draining, and commands that used to fail in 1s at deadlock_timeout instead sat until the
// 30s CommandTimeout. Retrying the booking operation, which re-reads before it re-inserts,
// is what ReservationService.BookAsync does instead.
builder.Services.AddDbContext<LabQueueDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("LabQueue")));

builder.AddLabQueueObservability();

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<PasswordHashing>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

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

// Served in every environment, Production included, and deliberately not behind
// app.Environment.IsDevelopment(). The hosted deployment is the only reason this API is
// public at all, and docs that work on a developer machine while the live URL 404s would be
// the same gap this closes, moved somewhere harder to notice.
app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options
    .WithTitle("labqueue API")
    // Scalar serves its bundle from assets embedded in the package; only the default web
    // fonts come from a CDN. Dropping those means the page renders with nothing leaving the
    // origin, which is what a free-tier deploy in front of an unknown network wants.
    .DisableDefaultFonts()
    // Keeps a pasted token across navigations. It is a bearer token in localStorage:
    // acceptable here because it lasts two hours, the published account is a member, and
    // the same credentials are printed at / anyway.
    .EnablePersistentAuthentication()
    .AddPreferredSecuritySchemes(JwtBearerDefaults.AuthenticationScheme));

// The landing route exists so a stranger who clicks the live URL lands on something that
// tells them how to use it, rather than a 404.
//
// A browser gets sent to /docs instead. The JSON below is the useful answer for a terminal
// and useless for a human, and the bare host is the URL people actually paste — so serving
// the same body to both put the reference one undiscoverable hop away. Negotiated on Accept
// rather than redirecting outright, because the JSON is still the machine-readable entry
// point and curl should keep getting it.
app.MapGet("/", (IConfiguration configuration, HttpRequest request) =>
{
    if (request.Headers.Accept.Any(h => h is not null && h.Contains("text/html", StringComparison.OrdinalIgnoreCase)))
    {
        return Results.Redirect("/docs");
    }

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
            howTo = "In a browser, open /docs: run POST /auth/login with these credentials, "
                    + "paste the token into Authorize, then GET /resources and POST /reservations "
                    + "with { resourceId, from, to }. Send that last one twice — the second is the "
                    + "409. From a terminal the same sequence works against these paths directly. "
                    + "Or register your own account at POST /auth/register."
        }
        : null;

    return Results.Ok(new
    {
        name = "labqueue",
        description = "Lab equipment reservation API — book instruments for a window of time, "
                      + "with certification gating and maintenance windows.",
        source = "https://github.com/LuisZarate17/labqueue",
        docs = "/docs",
        openapi = "/openapi/v1.json",
        health = "/health",
        demo = demoCredentials,
        note = "Hosted on free tiers. The first request after a period of inactivity has to wake "
               + "both the web service and the database, so it is slow; everything after it is not."
    });
})
   .AllowAnonymous()
   .WithName("Landing")
   .WithTags("Meta")
   .WithSummary("What this API is and how to start");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous()
   .WithName("Health")
   .WithTags("Meta")
   .WithSummary("Liveness check")
   .WithDescription("What Render's health check and the container HEALTHCHECK poll.");

app.MapAuthEndpoints();
app.MapResourceEndpoints();
app.MapReservationEndpoints();
app.MapAdminEndpoints();

// Migrations are deliberately not applied here — they run out of band via
// scripts/db-migrate.ps1. Demo data self-heals on boot; schema changes stay deliberate.
await app.SeedDemoDataAsync();

app.Run();

public partial class Program;
