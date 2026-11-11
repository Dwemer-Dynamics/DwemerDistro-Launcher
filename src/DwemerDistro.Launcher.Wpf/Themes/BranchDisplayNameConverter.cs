using System.Globalization;
using System.Windows.Data;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Themes;

/// <summary>Shows a release selector's branch choice by its user-facing label (Dev reads as Beta).</summary>
public sealed class BranchDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ServerManagementService.ToBranchDisplayName(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
