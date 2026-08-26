using NpgsqlTypes;

namespace LabQueue.Core.Time;

/// <summary>
/// The single construction site for reservation and maintenance windows.
/// Every range in the system is built here so that bound inclusivity is uniform:
/// tstzrange is a continuous range type, so Postgres does not canonicalise bounds
/// the way it does for discrete types, and a mixed-inclusivity row changes what
/// the overlap operator means at its boundary.
/// </summary>
public static class TimeWindow
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(8);

    /// <summary>Builds a <c>[from, to)</c> range with both bounds in UTC.</summary>
    public static NpgsqlRange<DateTime> ClosedOpen(DateTimeOffset from, DateTimeOffset to)
        => new(from.UtcDateTime, lowerBoundIsInclusive: true,
               to.UtcDateTime, upperBoundIsInclusive: false);

    public static NpgsqlRange<DateTime> ClosedOpen(DateTime from, DateTime to)
        => new(DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc), lowerBoundIsInclusive: true,
               DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc), upperBoundIsInclusive: false);

    public static TimeSpan DurationOf(DateTimeOffset from, DateTimeOffset to) => to - from;

    public static bool HasValidDuration(DateTimeOffset from, DateTimeOffset to)
    {
        var duration = to - from;
        return duration >= MinimumDuration && duration <= MaximumDuration;
    }
}
