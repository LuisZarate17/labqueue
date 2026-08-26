namespace LabQueue.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "labqueue";
    public string Audience { get; set; } = "labqueue";
    public int TokenLifetimeHours { get; set; } = 2;
}
