using LabQueue.Api.Auth;
using LabQueue.Api.Contracts;
using LabQueue.Api.Infrastructure;
using LabQueue.Api.Validation;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace LabQueue.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/register", RegisterAsync)
             .WithValidation<RegisterRequest>()
             .WithName("Register")
             .WithSummary("Create an account and receive a token")
             .WithDescription(
                 "Always creates a member. There is no way to register an admin, deliberately: "
                 + "admin routes schedule maintenance windows, and a maintenance window blocks "
                 + "bookings on a resource for everyone. Passwords are at least 12 characters.")
             .Produces<AuthResponse>(StatusCodes.Status201Created)
             .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/login", LoginAsync)
             .WithValidation<LoginRequest>()
             .WithName("Login")
             .WithSummary("Exchange credentials for a token")
             .WithDescription(LoginDescription(app.ServiceProvider.GetRequiredService<IConfiguration>()))
             .Produces<AuthResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// Publishes the demo credentials into the docs page, but only when the demo account is
    /// actually seeded — the same condition the landing route uses, and for the same reason.
    /// Demo:AdminEmail and Demo:AdminPassword exist on the deployment and are never printed
    /// anywhere: a public admin login would let any visitor block every resource.
    /// </summary>
    private static string LoginDescription(IConfiguration configuration)
    {
        const string Base = "Returns a token valid for two hours. Paste it into Authorize, "
                            + "at the top of this page, to call everything below.";

        var demo = configuration.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new DemoOptions();

        return demo is { Seed: true, Email: { } email, Password: { } password }
            ? $"{Base}\n\nThis deployment seeds a demo member you can use: **{email}** / **{password}**."
            : Base;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        LabQueueDbContext db,
        PasswordHashing passwords,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        var email = request.Email.Trim();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.Problem(
                title: "Email already registered",
                detail: "An account already exists for that email address.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Role = UserRole.Member,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwords.Hash(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = tokens.Issue(user);
        return Results.Created($"/users/{user.Id}", new AuthResponse(token, expiresAt, ToResponse(user)));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        LabQueueDbContext db,
        PasswordHashing passwords,
        JwtTokenService tokens,
        CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null || !passwords.Verify(user, request.Password))
        {
            return Results.Problem(
                title: "Invalid credentials",
                detail: "Email or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var (token, expiresAt) = tokens.Issue(user);
        return Results.Ok(new AuthResponse(token, expiresAt, ToResponse(user)));
    }

    private static UserResponse ToResponse(User user)
        => new(user.Id, user.Email, user.DisplayName, user.Role.ToString().ToLowerInvariant());
}
