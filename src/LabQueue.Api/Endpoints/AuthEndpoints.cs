using LabQueue.Api.Auth;
using LabQueue.Api.Contracts;
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

        group.MapPost("/register", RegisterAsync).WithValidation<RegisterRequest>();
        group.MapPost("/login", LoginAsync).WithValidation<LoginRequest>();

        return app;
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
