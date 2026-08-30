using System.Diagnostics;
using System.Net;
using LabQueue.Tests.Infrastructure;

namespace LabQueue.Tests;

/// <summary>
/// Finding A. <see cref="LabQueue.Core.Services.ReservationService.BookAsync"/> checks for an
/// overlapping reservation with a SELECT and then INSERTs, with no transaction and no lock.
/// Under READ COMMITTED, requests that run the SELECT before any of them has committed its
/// INSERT all see an empty result and all insert. One slot, many reservations.
///
/// This test is committed skipped and fails when un-skipped. That ordering is deliberate: a
/// concurrency test written after the fix proves nothing, because nothing establishes that it
/// ever exercised the race. Run it with
///
///     ./scripts/dev-test.ps1 -Unskip -Filter Fifty_concurrent -Repeat 5
///
/// The recorded failure is in docs/findings/finding-a-repro.txt.
/// </summary>
public class ConcurrentBookingTests(LabQueueApiFixture fixture) : IClassFixture<LabQueueApiFixture>
{
    private const int Attempts = 50;

    private static readonly DateTimeOffset Start = new(2027, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(2);

    [Fact]
    public async Task Fifty_concurrent_bookings_for_one_slot_yield_exactly_one_reservation()
    {
        var resourceId = await fixture.CreateResourceAsync();

        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        try
        {
            // The fifty requests block dedicated threads while their pipelines run on the
            // thread pool. Without this the pool injects threads roughly twice a second and
            // the requests trickle through instead of racing.
            ThreadPool.SetMinThreads(Math.Max(workerThreads, 128), Math.Max(completionPortThreads, 128));

            await WarmUpAsync();
            await RaceAsync(resourceId);
        }
        finally
        {
            ThreadPool.SetMinThreads(workerThreads, completionPortThreads);
        }
    }

    /// <summary>
    /// Pays the one-off costs before the barrier rather than inside it. Both would otherwise
    /// land on whichever requests happen to go first and stagger them well beyond the width
    /// of the race window: JIT and EF Core query compilation for the booking path (tens of
    /// milliseconds on first execution), and opening physical Npgsql connections.
    /// </summary>
    private async Task WarmUpAsync()
    {
        var throwaway = await fixture.CreateResourceAsync();
        var booked = await LabQueueApiFixture.BookAsync(
            fixture.Member, throwaway, Start.AddDays(-30), End.AddDays(-30));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        // Concurrent, so the pool has to open Attempts physical connections rather than
        // handing the same one back Attempts times.
        var warm = Enumerable.Range(0, Attempts)
            .Select(_ => fixture.Member.GetAsync("/reservations?take=1"))
            .ToArray();
        foreach (var response in await Task.WhenAll(warm))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
    }

    private async Task RaceAsync(Guid resourceId)
    {
        var barrier = new Barrier(Attempts);
        var statuses = new HttpStatusCode[Attempts];
        var releasedAt = new long[Attempts];

        var runners = new Task[Attempts];
        for (var i = 0; i < Attempts; i++)
        {
            var slot = i;
            runners[slot] = Task.Factory.StartNew(
                () =>
                {
                    // LongRunning gives every runner its own thread, so the barrier parks
                    // fifty real threads and wakes them together. A barrier over pooled
                    // threads would block the very pool it needs to release them.
                    barrier.SignalAndWait();
                    releasedAt[slot] = Stopwatch.GetTimestamp();

                    using var response = LabQueueApiFixture
                        .BookAsync(fixture.Member, resourceId, Start, End)
                        .GetAwaiter().GetResult();

                    statuses[slot] = response.StatusCode;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(runners);

        var created = statuses.Count(s => s == HttpStatusCode.Created);
        var conflicted = statuses.Count(s => s == HttpStatusCode.Conflict);
        var confirmed = await fixture.CountConfirmedAsync(resourceId);
        var spreadMs = (releasedAt.Max() - releasedAt.Min()) * 1000.0 / Stopwatch.Frequency;

        var histogram = string.Join(
            ", ",
            statuses.GroupBy(s => s)
                    .OrderBy(g => (int)g.Key)
                    .Select(g => $"{(int)g.Key} {g.Key} x{g.Count()}"));

        // The release spread is reported so a passing run can be told apart from a run that
        // never raced. If this ever passes with a spread in the milliseconds, the requests
        // were serialised and the result says nothing about the booking path.
        var detail =
            $"""

             {Attempts} simultaneous POST /reservations, one resource, one window.

               expected          : 201 x1, 409 x{Attempts - 1}, 1 confirmed row
               actual            : {histogram}
               confirmed rows    : {confirmed}
               release spread    : {spreadMs:0.000} ms across {Attempts} threads

             """;

        Assert.True(created == 1 && conflicted == Attempts - 1 && confirmed == 1, detail);
    }
}
