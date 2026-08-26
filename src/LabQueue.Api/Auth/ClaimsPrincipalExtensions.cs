using System.Security.Claims;

namespace LabQueue.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(LabQueueClaims.Subject)
                    ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(value, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated principal carries no usable subject claim.");
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.IsInRole("admin");
}
