using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.ViewModels;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly MainWindowViewModel _viewModel = new();
    private readonly InstallComponentsWindowViewModel _componentsViewModel;
    private readonly Paragraph _outputParagraph = new();
    private int _renderedOutputLength;
    private bool _isConsoleExpanded;
    private int _landmarkIndex = -1;

    public MainWindow()
    {
        _componentsViewModel = new InstallComponentsWindowViewModel(_viewModel);
        InitializeComponent();
        DataContext = _viewModel;
        SetupComponentsView.DataContext = _componentsViewModel;
        SourceInitialized += MainWindow_SourceInitialized;
        OutputRichTextBox.Document = new FlowDocument(_outputParagraph)
        {
            PagePadding = new Thickness(0)
        };
        OutputRichTextBox.Document.TextAlignment = TextAlignment.Left;
        OutputRichTextBox.Document.Foreground = Brushes.White;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LauncherLogService.Startup("Main window loaded.");
            UpdateSelectedGameDetails();
            ApplyConsoleLayout();
            ApplyHighContrastState();
            await _viewModel.InitializeAsync().ConfigureAwait(true);
            _ = _viewModel.RunFirstRunSetupStartupCheckAsync();
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Main window initialization failed.", ex);
            MessageBox.Show(
                $"Launcher startup hit an error but stayed open.\n\n{ex.Message}\n\nDetails were written to:\n{LauncherLogService.StartupLogPath}",
                "DwemerDistro Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        await _viewModel.ShutdownAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedGame))
        {
            _ = Dispatcher.BeginInvoke(UpdateSelectedGameDetails);
        }

        if (e.PropertyName != nameof(MainWindowViewModel.OutputText))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() => RenderOutputText(_viewModel.OutputText));
    }

    private void DestinationNav_Checked(object sender, RoutedEventArgs e)
    {
        if (MainTabs is null || sender is not System.Windows.Controls.RadioButton { Tag: string tag } || !int.TryParse(tag, out var index))
        {
            return;
        }

        MainTabs.SelectedIndex = index;
        _ = Dispatcher.BeginInvoke(UpdateSetupViewport);
    }

    private void GameRail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedGameDetails();
    }

    private void UpdateSelectedGameDetails()
    {
        if (ChimDetails is null)
        {
            return;
        }

        var key = _viewModel.SelectedGame.Key;
        ChimDetails.Visibility = key == "CHIM" ? Visibility.Visible : Visibility.Collapsed;
        StobeDetails.Visibility = key == "STOBE" ? Visibility.Visible : Visibility.Collapsed;
        DialecticDetails.Visibility = key == "DIALECTIC" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { IsEnabled: true } checkBox)
        {
            return;
        }

        e.Handled = true;
        checkBox.IsChecked = !(checkBox.IsChecked ?? false);
        if (checkBox.Command?.CanExecute(checkBox.CommandParameter) == true)
        {
            checkBox.Command.Execute(checkBox.CommandParameter);
        }
    }

    private void ToggleConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleConsole();
    }

    private void ToggleConsole()
    {
        _isConsoleExpanded = !_isConsoleExpanded;
        ApplyConsoleLayout();
        if (_isConsoleExpanded)
        {
            OutputRichTextBox.ScrollToEnd();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyConsoleLayout();
    }

    private void ApplyConsoleLayout()
    {
        if (ConsoleDockHost is null)
        {
            return;
        }

        var compact = ActualWidth > 0 && ActualWidth < 1120;
        UpdateSetupViewport();
        AutomationProperties.SetName(
            ToggleConsoleButton,
            _isConsoleExpanded ? "Collapse output console" : "Expand output console");
        ToggleConsoleButton.Content = _isConsoleExpanded
            ? "HIDE OUTPUT CONSOLE  ·  Ctrl+`"
            : "OUTPUT CONSOLE  ·  Ctrl+`";

        if (!_isConsoleExpanded)
        {
            ConsoleRow.Height = new GridLength(38);
            Grid.SetRow(ConsoleDockHost, 3);
            Grid.SetRowSpan(ConsoleDockHost, 1);
            ConsoleDockHost.Height = double.NaN;
            ConsoleDockHost.VerticalAlignment = VerticalAlignment.Stretch;
            ConsoleScrim.Visibility = Visibility.Collapsed;
            return;
        }

        if (compact)
        {
            ConsoleRow.Height = new GridLength(38);
            Grid.SetRow(ConsoleDockHost, 2);
            Grid.SetRowSpan(ConsoleDockHost, 2);
            ConsoleDockHost.Height = Math.Min(300, Math.Max(220, ActualHeight * 0.42));
            ConsoleDockHost.VerticalAlignment = VerticalAlignment.Bottom;
            ConsoleScrim.Visibility = Visibility.Visible;
            return;
        }

        ConsoleRow.Height = new GridLength(220);
        Grid.SetRow(ConsoleDockHost, 3);
        Grid.SetRowSpan(ConsoleDockHost, 1);
        ConsoleDockHost.Height = double.NaN;
        ConsoleDockHost.VerticalAlignment = VerticalAlignment.Stretch;
        ConsoleScrim.Visibility = Visibility.Collapsed;
    }

    private void UpdateSetupViewport()
    {
        if (SetupComponentsView is null || MainTabs is null || MainTabs.ActualHeight <= 0)
        {
            return;
        }

        SetupComponentsView.Height = Math.Max(240, MainTabs.ActualHeight - 86);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Oem3)
        {
            ToggleConsole();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            var key = e.SystemKey == Key.None ? e.Key : e.SystemKey;
            if (TryHandleAltShortcut(key))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.F6)
        {
            FocusNextLandmark((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
    }

    private bool TryHandleAltShortcut(Key key)
    {
        switch (key)
        {
            case Key.D1:
            case Key.NumPad1:
                GameRail.SelectedIndex = 0;
                return true;
            case Key.D2:
            case Key.NumPad2:
                GameRail.SelectedIndex = 1;
                return true;
            case Key.D3:
            case Key.NumPad3:
                GameRail.SelectedIndex = 2;
                return true;
            case Key.L:
                LibraryNavButton.IsChecked = true;
                return true;
            case Key.S:
                SetupNavButton.IsChecked = true;
                return true;
            case Key.M:
                MaintenanceNavButton.IsChecked = true;
                return true;
            case Key.G:
                LogsNavButton.IsChecked = true;
                return true;
            case Key.Space:
                SystemCommands.ShowSystemMenu(this, PointToScreen(new System.Windows.Point(8, 36)));
                return true;
            default:
                return false;
        }
    }

    private void FocusNextLandmark(bool reverse)
    {
        FrameworkElement activePage = MainTabs.SelectedIndex switch
        {
            1 => (FrameworkElement)SetupLandmark,
            2 => MaintenanceLandmark,
            3 => LogsLandmark,
            _ => LibraryLandmark
        };
        var landmarks = new FrameworkElement[]
        {
            UtilityLandmark,
            NavigationLandmark,
            activePage,
            GameRail,
            _isConsoleExpanded ? OutputRichTextBox : ToggleConsoleButton
        };

        _landmarkIndex = reverse
            ? (_landmarkIndex - 1 + landmarks.Length) % landmarks.Length
            : (_landmarkIndex + 1) % landmarks.Length;
        var landmark = landmarks[_landmarkIndex];
        if (!landmark.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            landmark.Focus();
        }
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            _ = Dispatcher.BeginInvoke(ApplyHighContrastState);
        }
    }

    private void ApplyHighContrastState()
    {
        HeroArt.Visibility = SystemParameters.HighContrast ? Visibility.Collapsed : Visibility.Visible;
        HeroHighContrastFallback.Visibility = SystemParameters.HighContrast ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Chrome_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this);
        source.AddHook(ConstrainMaximizedWindowToWorkArea);
    }

    // Supply the active monitor work area so custom chrome never covers its taskbar.
    private static IntPtr ConstrainMaximizedWindowToWorkArea(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private void RenderOutputText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
            return;
        }

        if (value.Length < _renderedOutputLength)
        {
            _outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
        }

        var appendedText = value[_renderedOutputLength..];
        if (appendedText.Length == 0)
        {
            return;
        }

        AppendFormattedText(appendedText);
        _renderedOutputLength = value.Length;
        OutputRichTextBox.ScrollToEnd();
    }

    private void AppendFormattedText(string text)
    {
        var lastIndex = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                _outputParagraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            var trailing = match.Value[url.Length..];

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var hyperlink = new Hyperlink(new Run(url))
                {
                    NavigateUri = uri,
                    Foreground = Brushes.LightSkyBlue
                };
                hyperlink.Click += Hyperlink_Click;
                _outputParagraph.Inlines.Add(hyperlink);
            }
            else
            {
                _outputParagraph.Inlines.Add(new Run(match.Value));
                lastIndex = match.Index + match.Length;
                continue;
            }

            if (!string.IsNullOrEmpty(trailing))
            {
                _outputParagraph.Inlines.Add(new Run(trailing));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            _outputParagraph.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private static void Hyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { NavigateUri: not null } hyperlink)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(hyperlink.NavigateUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}
