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
using DwemerDistro.Launcher.Wpf.Views;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace DwemerDistro.Launcher.Wpf;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int ComponentsTabIndex = 1;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly MainWindowViewModel _viewModel = new();
    private InstallComponentsWindowViewModel? _componentsViewModel;
    private Paragraph? _outputParagraph;
    private RichTextBox? _outputRichTextBox;
    private int _renderedOutputLength;
    private int _renderedOutputGeneration = -1;
    private bool _isConsoleExpanded;
    private int _landmarkIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += MainWindow_SourceInitialized;
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

        if (_isConsoleExpanded)
        {
            var output = _viewModel.OutputText;
            var generation = _viewModel.OutputGeneration;
            _ = Dispatcher.BeginInvoke(() => RenderOutputText(output, generation));
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Branch combo boxes and other nested selectors bubble their own SelectionChanged
        // through the TabControl, and the control raises this once during XAML load before
        // the host exists. Only a real tab switch may move the Components page.
        if (SetupComponentsHost is null || !ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        UpdateComponentsPageLifetime();
    }

    private void ComponentsViewModel_ActiveOperationsCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _componentsViewModel))
        {
            return;
        }

        // An install or configuration run held the page open. Retry the unload now that it
        // has finished, in case the user moved to another destination while it ran.
        _ = Dispatcher.BeginInvoke(UpdateComponentsPageLifetime);
    }

    private void UpdateComponentsPageLifetime()
    {
        if (SetupComponentsHost is null)
        {
            return;
        }

        var componentsSelected = MainTabs.SelectedIndex == ComponentsTabIndex;
        if (componentsSelected)
        {
            if (SetupComponentsHost.Content is null)
            {
                var viewModel = new InstallComponentsWindowViewModel(_viewModel);
                viewModel.ActiveOperationsCompleted += ComponentsViewModel_ActiveOperationsCompleted;
                _componentsViewModel = viewModel;
                SetupComponentsHost.Content = new InstallComponentsView
                {
                    DataContext = viewModel,
                    MinHeight = 0
                };
            }

            return;
        }

        var componentsViewModel = _componentsViewModel;
        if (SetupComponentsHost.Content is null
            || componentsViewModel is null
            || !InstallComponentsWindowViewModel.ShouldUnloadComponentsPage(
                componentsSelected,
                componentsViewModel.HasActiveOperation))
        {
            return;
        }

        // Alt+M and the Settings shortcut switch destinations without moving focus first.
        // Hand focus to the tab region before the page goes, so keyboard users land in the
        // destination they picked and no discarded control stays focused.
        if (SetupComponentsHost.IsKeyboardFocusWithin)
        {
            Keyboard.Focus(MainTabs);
        }

        var detachTask = componentsViewModel.DetachAsync();
        componentsViewModel.ActiveOperationsCompleted -= ComponentsViewModel_ActiveOperationsCompleted;
        if (SetupComponentsHost.Content is FrameworkElement componentsView)
        {
            componentsView.DataContext = null;
            if (componentsView is ContentControl componentsContent)
            {
                componentsContent.Content = null;
            }
        }

        SetupComponentsHost.Content = null;
        _componentsViewModel = null;

        _ = ReclaimComponentsPageAsync(detachTask);
    }

    // Wait for canceled probes and their Loaded continuation to release the discarded visual tree.
    private async Task ReclaimComponentsPageAsync(Task detachTask)
    {
        try
        {
            await detachTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Components cleanup failed after leaving the page.", ex);
        }

        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            () => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void DestinationNav_Checked(object sender, RoutedEventArgs e)
    {
        if (MainTabs is null || sender is not System.Windows.Controls.RadioButton { Tag: string tag } || !int.TryParse(tag, out var index))
        {
            return;
        }

        MainTabs.SelectedIndex = index;
    }

    private void OpenSettingsDestination_Click(object sender, RoutedEventArgs e)
    {
        SettingsNavButton.IsChecked = true;
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
            _outputRichTextBox?.ScrollToEnd();
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
        var toggleLabel = _isConsoleExpanded ? "Hide Console" : "Show Console";
        AutomationProperties.SetName(ToggleConsoleButton, toggleLabel);
        ToggleConsoleButton.Content = toggleLabel;

        if (!_isConsoleExpanded)
        {
            ReleaseOutputConsole();
            _renderedOutputLength = 0;
            _renderedOutputGeneration = -1;
            ConsoleRow.Height = new GridLength(38);
            Grid.SetRow(ConsoleDockHost, 3);
            Grid.SetRowSpan(ConsoleDockHost, 1);
            ConsoleDockHost.Height = double.NaN;
            ConsoleDockHost.VerticalAlignment = VerticalAlignment.Stretch;
            ConsoleScrim.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureOutputConsole();
        RenderOutputText(_viewModel.OutputText, _viewModel.OutputGeneration);

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

    // Build the document control only while the user is looking at console output.
    private RichTextBox EnsureOutputConsole()
    {
        if (_outputRichTextBox is not null)
        {
            return _outputRichTextBox;
        }

        var paragraph = new Paragraph();
        var output = new RichTextBox
        {
            Margin = new Thickness(14, 0, 14, 12),
            Padding = new Thickness(10),
            IsReadOnly = true,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 9, 9)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Document = new FlowDocument(paragraph)
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                Foreground = Brushes.White
            }
        };
        output.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "GameCenter.Border");
        ScrollViewer.SetVerticalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(output, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(output, "Dwemer Distro output console");

        _outputParagraph = paragraph;
        _outputRichTextBox = output;
        OutputConsoleHost.Content = output;
        return output;
    }

    private void ReleaseOutputConsole()
    {
        _outputParagraph?.Inlines.Clear();
        if (_outputRichTextBox is not null)
        {
            _outputRichTextBox.Document.Blocks.Clear();
        }

        OutputConsoleHost.Content = null;
        _outputParagraph = null;
        _outputRichTextBox = null;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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
            case Key.M:
                LibraryNavButton.IsChecked = true;
                return true;
            case Key.C:
                SetupNavButton.IsChecked = true;
                return true;
            case Key.S:
                SettingsNavButton.IsChecked = true;
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
            2 => SettingsLandmark,
            3 => LogsLandmark,
            _ => LibraryLandmark
        };
        var landmarks = new FrameworkElement[]
        {
            UtilityLandmark,
            NavigationLandmark,
            activePage,
            GameRail,
            _isConsoleExpanded && _outputRichTextBox is not null ? _outputRichTextBox : ToggleConsoleButton
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

    private void RenderOutputText(string value, int generation)
    {
        var outputParagraph = _outputParagraph;
        var output = _outputRichTextBox;
        if (!_isConsoleExpanded || outputParagraph is null || output is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
            _renderedOutputGeneration = generation;
            return;
        }

        if (generation != _renderedOutputGeneration || value.Length < _renderedOutputLength)
        {
            outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
            _renderedOutputGeneration = generation;
        }

        var appendedText = value[_renderedOutputLength..];
        if (appendedText.Length == 0)
        {
            return;
        }

        AppendFormattedText(outputParagraph, appendedText);
        _renderedOutputLength = value.Length;
        _renderedOutputGeneration = generation;
        output.ScrollToEnd();
    }

    private void AppendFormattedText(Paragraph outputParagraph, string text)
    {
        var lastIndex = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                outputParagraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
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
                outputParagraph.Inlines.Add(hyperlink);
            }
            else
            {
                outputParagraph.Inlines.Add(new Run(match.Value));
                lastIndex = match.Index + match.Length;
                continue;
            }

            if (!string.IsNullOrEmpty(trailing))
            {
                outputParagraph.Inlines.Add(new Run(trailing));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            outputParagraph.Inlines.Add(new Run(text[lastIndex..]));
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
