using System.Windows;
using System.Windows.Controls;
using WinIsland.Models;

namespace WinIsland.Panes;

public partial class CalendarView : UserControl
{
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public CalendarView()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        MonthText.Text = $"{_month:yyyy年M月}";
        TodayBadge.Visibility = _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month
            ? Visibility.Visible
            : Visibility.Collapsed;

        var days = new List<CalendarDay>(42);
        var offset = ((int)_month.DayOfWeek + 6) % 7;
        var start = _month.AddDays(-offset);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            days.Add(new CalendarDay
            {
                Date = date,
                Day = date.Day,
                IsCurrentMonth = date.Month == _month.Month && date.Year == _month.Year,
                IsToday = date.Date == DateTime.Today
            });
        }

        Days.ItemsSource = days;
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        Build();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        Build();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Build();
    }
}
