namespace VakifAtlas.SharedKernel;

/// <summary>
/// Provides date and time calculations based on Turkey Time (UTC+3, no daylight saving time).
/// Used for computing daily XP reset thresholds, memory dates (<c>memoryDate</c>), and reporting years.
/// </summary>
public static class TurkeyTime
{
    /// <summary>
    /// Gets the fixed UTC offset for Turkey (UTC+3).
    /// </summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    /// <summary>
    /// Gets today's calendar date in Turkey time from the specified <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="clock">The time provider providing the current UTC instant.</param>
    /// <returns>The current calendar date in Turkey time.</returns>
    public static DateOnly Today(TimeProvider clock) => DateOf(clock.GetUtcNow());

    /// <summary>
    /// Converts a UTC or offset-based instant to its calendar date representation in Turkey time.
    /// </summary>
    /// <param name="instant">The timestamp instant to convert.</param>
    /// <returns>The corresponding calendar date in Turkey time.</returns>
    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>
    /// Extracts the calendar year of an instant based on Turkey time.
    /// </summary>
    /// <param name="instant">The timestamp instant to evaluate.</param>
    /// <returns>The calendar year in Turkey time.</returns>
    public static int YearOf(DateTimeOffset instant) => instant.ToOffset(Offset).Year;
}
