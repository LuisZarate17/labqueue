using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabQueue.Core.Enums;
using LabQueue.Tests.Infrastructure;

namespace LabQueue.Tests;

/// <summary>
/// One test per booking rule. Expectations are cross-checked against
/// <c>scripts/gate02.sh</c>, which exercises the same five rules over HTTP.
///
/// Rule order inside BookAsync matters and is asserted implicitly: the resource checks run
/// before the window checks, so an unknown resource with a malformed window is a 404 rather
/// than a 400. Each test seeds its own resource, so none of them interact.
/// </summary>
public class BookingRuleTests(LabQueueApiFixture fixture) : IClassFixture<LabQueueApiFixture>
{
    private static readonly DateTimeOffset Start = new(2027, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(2);

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task Booking_a_free_window_returns_201()
    {
        var resourceId = await fixture.CreateResourceAsync();

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await fixture.CountConfirmedAsync(resourceId));
    }

    [Fact]
    public async Task A_booking_shows_up_in_your_own_reservations()
    {
        var resourceId = await fixture.CreateResourceAsync();
        var booked = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);
        var reservationId = await LabQueueApiFixture.IdOfAsync(booked);

        var response = await fixture.Member.GetAsync("/reservations?take=200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(
            document.RootElement.EnumerateArray(),
            element => element.GetProperty("id").GetGuid() == reservationId);
    }

    // ------------------------------------------------- rule 1: resource exists and is active

    [Fact]
    public async Task An_unknown_resource_returns_404()
    {
        var response = await LabQueueApiFixture.BookAsync(fixture.Member, Guid.NewGuid(), Start, End);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_retired_resource_returns_409()
    {
        var resourceId = await fixture.CreateResourceAsync(status: ResourceStatus.Retired);

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------------------------------ rule 2: the window is well formed

    [Fact]
    public async Task An_end_before_the_start_returns_400()
    {
        var resourceId = await fixture.CreateResourceAsync();

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, End, Start);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_window_under_the_minimum_returns_400()
    {
        var resourceId = await fixture.CreateResourceAsync();

        var response = await LabQueueApiFixture.BookAsync(
            fixture.Member, resourceId, Start, Start.AddMinutes(5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_window_over_the_maximum_returns_400()
    {
        var resourceId = await fixture.CreateResourceAsync();

        var response = await LabQueueApiFixture.BookAsync(
            fixture.Member, resourceId, Start, Start.AddHours(9));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------- rule 3: certification

    [Fact]
    public async Task A_gated_resource_without_the_certification_returns_403()
    {
        var certificationId = await fixture.CreateCertificationAsync();
        var resourceId = await fixture.CreateResourceAsync(requiredCertificationId: certificationId);

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_gated_resource_with_the_certification_returns_201()
    {
        var certificationId = await fixture.CreateCertificationAsync();
        var resourceId = await fixture.CreateResourceAsync(requiredCertificationId: certificationId);

        var grant = await fixture.Admin.PostAsJsonAsync(
            $"/users/{fixture.MemberId}/certifications", new { certificationId });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------------------------------------------------------- rule 4: maintenance window

    [Fact]
    public async Task A_window_overlapping_maintenance_returns_409()
    {
        var resourceId = await fixture.CreateResourceAsync();
        await fixture.CreateMaintenanceWindowAsync(resourceId, Start.AddHours(-2), End.AddHours(2));

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------------------------ rule 5: overlapping confirmed booking

    [Fact]
    public async Task An_identical_window_returns_409()
    {
        var resourceId = await fixture.CreateResourceAsync();
        Assert.Equal(
            HttpStatusCode.Created,
            (await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End)).StatusCode);

        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_partially_overlapping_window_returns_409()
    {
        var resourceId = await fixture.CreateResourceAsync();
        Assert.Equal(
            HttpStatusCode.Created,
            (await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End)).StatusCode);

        var response = await LabQueueApiFixture.BookAsync(
            fixture.Member, resourceId, Start.AddHours(1), End.AddHours(1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Windows are [from, to), so one starting exactly where another ends does not overlap.
    /// This is the test that would break if a later phase built the exclusion constraint on
    /// a closed-closed range.
    /// </summary>
    [Fact]
    public async Task An_abutting_window_returns_201()
    {
        var resourceId = await fixture.CreateResourceAsync();
        Assert.Equal(
            HttpStatusCode.Created,
            (await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End)).StatusCode);

        var response = await LabQueueApiFixture.BookAsync(
            fixture.Member, resourceId, End, End.AddHours(2));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, await fixture.CountConfirmedAsync(resourceId));
    }
}
