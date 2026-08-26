using LabQueue.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace LabQueue.Api.Auth;

/// <summary>
/// Wraps the framework password hasher (PBKDF2-HMAC-SHA512) so callers do not
/// depend on the Identity types directly.
/// </summary>
public sealed class PasswordHashing
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public bool Verify(User user, string password)
        => _hasher.VerifyHashedPassword(user, user.PasswordHash, password)
            is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}
