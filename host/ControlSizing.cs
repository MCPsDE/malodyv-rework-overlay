using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MalodyV.ReworkOverlay;

internal static class ControlSizing
{
    public static void FitWindow(Window window, params FrameworkElement[][] rows)
    {
        FitLabels(window);
        var minimum = 460d;
        foreach (var row in rows)
        {
            var width = 66d + Math.Max(0, row.Length - 1) * 14;
            foreach (var element in row)
            {
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width += element.DesiredSize.Width;
            }
            minimum = Math.Max(minimum, Math.Ceiling(width));
        }
        window.MinWidth = minimum;
        window.Width = Math.Max(window.Width, minimum);
    }

    private static void FitLabels(DependencyObject parent)
    {
        if (parent is ContentControl control && control is Button or CheckBox && control.Content is string label)
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch),
                control.FontSize, Brushes.White, VisualTreeHelper.GetDpi(control).PixelsPerDip);
            var chrome = control is CheckBox ? 37 : control.Padding.Left + control.Padding.Right + 4;
            control.MinWidth = Math.Max(control.MinWidth, Math.Ceiling(text.WidthIncludingTrailingWhitespace + chrome));
        }
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            FitLabels(child);
    }
}
