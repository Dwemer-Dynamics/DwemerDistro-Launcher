using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace DwemerDistro.Launcher.Wpf.Views;

/// <summary>
/// What one blocked or failed mod update dialog says. A non-null <see cref="YesChoice"/> makes it the
/// Yes/No discard confirmation; otherwise it is an OK-only warning or error.
/// </summary>
internal sealed record ServerUpdateDialogContent(
    string Title,
    string Heading,
    string? Intro,
    string DetailsLabel,
    string Details,
    string? YesChoice,
    string? NoChoice,
    string Footnote,
    bool IsWarning)
{
    public bool IsConfirmation => YesChoice is not null;

    /// <summary>The whole message as one string, for screen readers and the regression tests.</summary>
    public string ToPlainText()
    {
        var parts = new List<string> { Heading };
        if (!string.IsNullOrWhiteSpace(Intro))
        {
            parts.Add(Intro);
        }

        parts.Add($"{DetailsLabel}:\n{Details}");
        if (IsConfirmation)
        {
            parts.Add($"Yes: {YesChoice}");
            parts.Add($"No: {NoChoice}");
        }

        parts.Add(Footnote);
        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// Launcher-themed replacement for the native message boxes of a blocked or failed mod update.
/// Only the Yes button returns true; No, OK, Escape, the title bar close button and Alt+F4 all
/// return false, so consent is never assumed.
/// </summary>
public partial class ServerUpdateDialog : Window
{
    private const double WorkAreaMargin = 12;

    internal ServerUpdateDialog(ServerUpdateDialogContent content)
    {
        InitializeComponent();

        Title = content.Title;
        TitleTextBlock.Text = content.Title;
        HeadingTextBlock.Text = content.Heading;
        if (content.IsWarning)
        {
            HeadingTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "GameCenter.Gold");
        }

        IntroTextBlock.Text = content.Intro ?? string.Empty;
        IntroTextBlock.Visibility = string.IsNullOrWhiteSpace(content.Intro) ? Visibility.Collapsed : Visibility.Visible;
        DetailsLabelTextBlock.Text = content.DetailsLabel;
        DetailsTextBox.Text = content.Details;
        FootnoteTextBlock.Text = content.Footnote;

        if (content.IsConfirmation)
        {
            YesChoiceRun.Text = content.YesChoice;
            NoChoiceRun.Text = content.NoChoice;
            ChoicesPanel.Visibility = Visibility.Visible;
            YesButton.Visibility = Visibility.Visible;
            DismissButton.Content = "No";
            DismissButton.SetResourceReference(StyleProperty, "GameCenter.Button");
            AutomationProperties.SetName(YesButton, "Yes, discard local edits and retry");
            AutomationProperties.SetHelpText(YesButton, "Yes: " + content.YesChoice);
            AutomationProperties.SetName(DismissButton, "No, keep local edits");
            AutomationProperties.SetHelpText(DismissButton, "No: " + content.NoChoice);
        }

        AutomationProperties.SetName(this, content.Title);
        AutomationProperties.SetHelpText(this, content.ToPlainText());
        var isError = !content.IsConfirmation && !content.IsWarning;
        Loaded += (_, _) =>
        {
            // Error output explains itself at the end, so a long tail opens on its last line.
            if (isError)
            {
                DetailsTextBox.ScrollToEnd();
            }

            DismissButton.Focus();
            KeepInsideWorkArea();
        };
    }

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

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
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
