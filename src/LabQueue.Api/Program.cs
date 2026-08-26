using System.Text;
using FluentValidation;
using LabQueue.Api.Auth;
using LabQueue.Api.Endpoints;
using LabQueue.Api.Infrastructure;
using LabQueue.Core.Data;
using LabQueue.Core.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddDbContext<LabQueueDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("LabQueue")));

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous()
   .WithName("Health");

app.MapAuthEndpoints();
app.MapResourceEndpoints();
app.MapReservationEndpoints();
app.MapAdminEndpoints();

app.Run();

public partial class Program;
