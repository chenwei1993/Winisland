using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinIsland.Infrastructure;

/// <summary>
/// Applies a user-selectable font scale to every text element, including ones
/// created later by data templates and dynamically loaded panes. Only the font
/// size is scaled; paddings and container sizes stay as designed.
/// </summary>
public static class UiFontSize
{
    private sealed class Baseline
    {
        public double Value { get; init; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, Baseline> Baselines = new();
    private static DispatcherTimer? _timer;
    private static double _scale = 1.0;

    public static void Initialize()
    {
        if (_timer is not null)
        {
            return;
        }

        // Data templates and pane switches create text elements after the first
        // layout; a light sweep keeps them in sync without hooking every item.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += (_, _) => ApplyAll();
        _timer.Start();
    }

    public static void ApplyMode(int mode)
    {
        _scale = mode switch
        {
            0 => 0.9,
            2 => 1.12,
            _ => 1.0
        };

        ApplyAll();
    }

    public static void ApplyAll()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            Walk(window);
        }
    }

    private static void Apply(FrameworkElement element)
    {
        if (element is not (TextBlock or Control))
        {
            return;
        }

        // Let children inherit the scaled size from their parent instead of
        // scaling the same value again.
        var source = DependencyPropertyHelper.GetValueSource(element, TextElement.FontSizeProperty);
        if (source.BaseValueSource == BaseValueSource.Inherited)
        {
            return;
        }

        if (!Baselines.TryGetValue(element, out var baseline))
        {
            baseline = new Baseline { Value = (double)element.GetValue(TextElement.FontSizeProperty) };
            Baselines.Add(element, baseline);
        }

        var target = baseline.Value * _scale;
        var current = (double)element.GetValue(TextElement.FontSizeProperty);
        if (Math.Abs(current - target) > 0.001)
        {
            element.SetValue(TextElement.FontSizeProperty, target);
        }
    }

    private static void Walk(DependencyObject node)
    {
        if (node is FrameworkElement element)
        {
            Apply(element);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i));
        }
    }
}
