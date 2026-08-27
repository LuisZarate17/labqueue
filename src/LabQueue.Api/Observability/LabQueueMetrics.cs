using System.Diagnostics.Metrics;

namespace LabQueue.Api.Observability;

/// <summary>
/// The application's own instruments, as opposed to the ones ASP.NET Core and Npgsql
/// publish for us.
///
/// Registered unconditionally, including when no OTLP endpoint is configured. Callers
/// depend on this type, not on whether telemetry happens to be exported anywhere — an
/// unsubscribed <see cref="Counter{T}"/> costs a branch and nothing else.
/// </summary>
public sealed class LabQueueMetrics
{
    public const string MeterName = "LabQueue";

    private readonly Counter<long> _reservationConflicts;

    public LabQueueMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        // Deliberately untagged. Resource id would be the obvious dimension and it is
        // unbounded, which is the wrong shape for a 10k-series budget. The question this
        // instrument answers - "how often does the overlap check reject a booking" - does
        // not need a breakdown to be legible.
        _reservationConflicts = meter.CreateCounter<long>(
            "reservations.conflicts.total",
            unit: "{conflict}",
            description: "Bookings rejected because the requested window overlapped a confirmed reservation.");
    }

    public void ReservationConflict() => _reservationConflicts.Add(1);
}
