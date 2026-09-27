using System.Globalization;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App;

public enum GroupMode
{
    Month,
    /// <summary>One header per day with the full date (On this day: one day per year).</summary>
    Day,
    /// <summary>No headers or jump list: the query's own order (e.g. blurriest first).</summary>
    None,
    /// <summary>One header per year.</summary>
    Year,
}

/// <summary>A section of the date-ordered grid: its header and items.</summary>
public sealed class MonthGroup(string title) : List<MediaSummary>
{
    public string Title { get; } = title;

    /// <summary>Consecutive items (in date order, either way) split wherever the month (or day, or year) changes.</summary>
    public static List<MonthGroup> Split(IReadOnlyList<MediaSummary> items, GroupMode mode = GroupMode.Month)
    {
        var groups = new List<MonthGroup>();
        MonthGroup? current = null;
        var key = DateTime.MinValue;
        foreach (var item in items)
        {
            var taken = item.TakenLocal;
            var itemKey = mode switch
            {
                GroupMode.Day => taken.Date,
                GroupMode.Year => new DateTime(taken.Year, 1, 1),
                _ => new DateTime(taken.Year, taken.Month, 1),
            };
            if (current is null || itemKey != key)
            {
                key = itemKey;
                var format = mode switch { GroupMode.Day => "dddd, MMMM d, yyyy", GroupMode.Year => "yyyy", _ => "MMMM yyyy" };
                current = new MonthGroup(itemKey.ToString(format, CultureInfo.CurrentCulture));
                groups.Add(current);
            }
            current.Add(item);
        }
        return groups;
    }
}
