using System.Net;
using LabQueue.Tests.Infrastructure;

namespace LabQueue.Tests;

public class CancellationTests(LabQueueApiFixture fixture) : IClassFixture<LabQueueApiFixture>
{
    private static readonly DateTimeOffset Start = new(2027, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(2);

    [Fact]
    public async Task Cancelling_your_own_reservation_returns_204()
    {
        var (_, reservationId) = await BookedSlotAsync();

        var response = await fixture.Member.DeleteAsync($"/reservations/{reservationId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_the_same_reservation_twice_returns_409()
    {
        var (_, reservationId) = await BookedSlotAsync();
        await fixture.Member.DeleteAsync($"/reservations/{reservationId}");

        var response = await fixture.Member.DeleteAsync($"/reservations/{reservationId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_unknown_reservation_returns_404()
    {
        var response = await fixture.Member.DeleteAsync($"/reservations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_someone_elses_reservation_returns_403()
    {
        var (_, reservationId) = await BookedSlotAsync();
        var otherId = await fixture.CreateUserAsync($"other-{Guid.NewGuid():N}@labqueue.test");
        var other = fixture.ClientForUser(otherId);

        var response = await other.DeleteAsync($"/reservations/{reservationId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_cancel_someone_elses_reservation()
    {
        var (_, reservationId) = await BookedSlotAsync();

        var response = await fixture.Admin.DeleteAsync($"/reservations/{reservationId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// The eventual exclusion constraint is partial on <c>status = 'confirmed'</c>, so a
    /// cancelled reservation must not keep holding its slot. This test is here to fail
    /// loudly if Phase 06 builds that constraint over every row instead.
    /// </summary>
    [Fact]
    public async Task A_cancelled_slot_can_be_rebooked()
    {
        var (resourceId, reservationId) = await BookedSlotAsync();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End)).StatusCode);

        var cancelled = await fixture.Member.DeleteAsync($"/reservations/{reservationId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        var rebooked = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);

        Assert.Equal(HttpStatusCode.Created, rebooked.StatusCode);
        Assert.Equal(1, await fixture.CountConfirmedAsync(resourceId));
    }

    private async Task<(Guid ResourceId, Guid ReservationId)> BookedSlotAsync()
    {
        var resourceId = await fixture.CreateResourceAsync();
        var response = await LabQueueApiFixture.BookAsync(fixture.Member, resourceId, Start, End);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (resourceId, await LabQueueApiFixture.IdOfAsync(response));
    }
}
