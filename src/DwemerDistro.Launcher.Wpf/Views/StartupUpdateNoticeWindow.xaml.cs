using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DwemerDistro.Launcher.Wpf.ViewModels;
using Point = System.Windows.Point;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>What the startup update notice was closed with. Every choice only navigates; none of them updates.</summary>
internal enum StartupUpdateNoticeChoice
{
    Close,
    OpenSettings,
    ShowConsole
}

/// <summary>
/// Launcher-themed notice for an automatic startup distro update that failed or could not be recorded. It is
/// shown modeless, so the window, the assistant's startup connection and shutdown cleanup never wait on it.
/// </summary>
public partial class StartupUpdateNoticeWindow : Window
{
    private const double WorkAreaMargin = 12;

    internal StartupUpdateNoticeWindow(StartupDistroUpdateNotice notice)
    {
        InitializeComponent();

        Title = notice.Title;
        TitleTextBlock.Text = notice.Title;
        HeadingTextBlock.Text = notice.Heading;
        if (notice.IsWarning)
        {
            HeadingTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "GameCenter.CopperStrong");
        }

        SummaryTextBlock.Text = notice.Summary;
        DetailsTextBox.Text = notice.Details ?? string.Empty;
        DetailsBorder.Visibility = string.IsNullOrWhiteSpace(notice.Details) ? Visibility.Collapsed : Visibility.Visible;
        NextStepTextBlock.Text = notice.NextStep;

        AutomationProperties.SetName(this, notice.Heading);
        AutomationProperties.SetHelpText(this, notice.ToPlainText());
        Loaded += (_, _) =>
        {
            OpenSettingsButton.Focus();
            KeepInsideWorkArea();
        };
    }

    internal StartupUpdateNoticeChoice Choice { get; private set; } = StartupUpdateNoticeChoice.Close;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FitToWorkArea();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitToWorkArea();
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => CloseWith(StartupUpdateNoticeChoice.OpenSettings);

    private void ShowConsoleButton_Click(object sender, RoutedEventArgs e) => CloseWith(StartupUpdateNoticeChoice.ShowConsole);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseWith(StartupUpdateNoticeChoice.Close);

    private void CloseWith(StartupUpdateNoticeChoice choice)
    {
        Choice = choice;
        Close();
    }

    /// <summary>Caps the size to the owner's monitor so the actions stay on screen on small laptops.</summary>
    private void FitToWorkArea()
    {
        var area = GetWorkArea();
        MaxWidth = Math.Max(MinWidth, area.Width - WorkAreaMargin * 2);
        MaxHeight = Math.Max(160, area.Height - WorkAreaMargin * 2);
    }

    private void KeepInsideWorkArea()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var area = GetWorkArea();
        Left = Math.Max(area.Left + WorkAreaMargin, Math.Min(Left, area.Right - WorkAreaMargin - ActualWidth));
        Top = Math.Max(area.Top + WorkAreaMargin, Math.Min(Top, area.Bottom - WorkAreaMargin - ActualHeight));
    }

    /// <summary>The work area of the owner's monitor (or this window's), in this window's units.</summary>
    private Rect GetWorkArea()
    {
        var handle = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        if (handle == IntPtr.Zero)
        {
            handle = new WindowInteropHelper(this).Handle;
        }

        var screen = handle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen
            : System.Windows.Forms.Screen.FromHandle(handle);
        if (screen is null)
        {
            return SystemParameters.WorkArea;
        }

        var pixels = screen.WorkingArea;
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(
            fromDevice.Transform(new Point(pixels.Left, pixels.Top)),
            fromDevice.Transform(new Point(pixels.Right, pixels.Bottom)));
    }
}
