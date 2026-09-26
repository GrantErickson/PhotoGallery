using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoGallery.Core.Data;

namespace PhotoGallery.App.Pages;

/// <summary>Photos from a calendar day across all years, one section per year; step or pick the day.</summary>
public sealed partial class OnThisDayPage : Page
{
    private DateTime _day = DateTime.Today;
    private bool _suppress;

    public OnThisDayPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // remember the chosen day
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Show(_day);

    private void Show(DateTime day)
    {
        _day = day.Date;
        _suppress = true;
        DatePicker.Date = new DateTimeOffset(_day);
        _suppress = false;
        SubtitleText.Text = _day == DateTime.Today ? "Today, in past years" : "";
        // Everything taken on this month/day in any year (up to today, so "future" years don't appear).
        Gallery.BaseFilter = new MediaFilter { MonthDay = (_day.Month, _day.Day), To = DateTime.Today.AddDays(1) };
    }

    private void OnPreviousDay(object sender, RoutedEventArgs e) => Show(_day.AddDays(-1));

    private void OnNextDay(object sender, RoutedEventArgs e) => Show(_day.AddDays(1));

    private void OnToday(object sender, RoutedEventArgs e) => Show(DateTime.Today);

    private void OnDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_suppress && args.NewDate is { } date) Show(date.Date);
    }
}
