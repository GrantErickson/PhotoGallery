using System.Globalization;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App;

/// <summary>A month of the timeline: the group header and its items, for the grouped grid.</summary>
public sealed class MonthGroup(int year, int month) : List<MediaSummary>
{
    public string Title { get; } = new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>Consecutive items (newest first) split wherever the month changes.</summary>
    public static List<MonthGroup> Split(IReadOnlyList<MediaSummary> items)
    {
        var groups = new List<MonthGroup>();
        MonthGroup? current = null;
        int year = -1, month = -1;
        foreach (var item in items)
        {
            var taken = item.TakenLocal;
            if (current is null || taken.Year != year || taken.Month != month)
            {
                (year, month) = (taken.Year, taken.Month);
                current = new MonthGroup(year, month);
                groups.Add(current);
            }
            current.Add(item);
        }
        return groups;
    }
}
