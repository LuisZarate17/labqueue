using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabQueue.Tests.Infrastructure;

namespace LabQueue.Tests;

public class AuthTests(LabQueueApiFixture fixture) : IClassFixture<LabQueueApiFixture>
{
    [Fact]
    public async Task Registering_returns_201_and_a_usable_token()
    {
        var email = $"new-{Guid.NewGuid():N}@labqueue.test";

        var response = await fixture.Anonymous.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "New Member"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task Registering_an_email_twice_returns_409()
    {
        var response = await fixture.Anonymous.PostAsJsonAsync("/auth/register", new
        {
            email = fixture.MemberEmail,
            password = "correct-horse-battery",
            displayName = "Duplicate"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Logging_in_with_the_right_password_returns_200()
    {
        var response = await fixture.Anonymous.PostAsJsonAsync("/auth/login", new
        {
            email = fixture.MemberEmail,
            password = LabQueueApiFixture.SeedPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Logging_in_with_the_wrong_password_returns_401()
    {
        var response = await fixture.Anonymous.PostAsJsonAsync("/auth/login", new
        {
            email = fixture.MemberEmail,
            password = "not-the-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/reservations")]
    [InlineData("/resources")]
    public async Task A_protected_route_without_a_token_returns_401(string route)
    {
        var response = await fixture.Anonymous.GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_route_as_a_member_returns_403()
    {
        var response = await fixture.Member.PostAsJsonAsync("/resources", new
        {
            code = "NOPE",
            name = "Nope"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await fixture.Anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
