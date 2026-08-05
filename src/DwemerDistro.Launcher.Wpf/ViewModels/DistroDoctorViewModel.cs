using System.IO;
using System.Text;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

internal sealed class DistroDoctorViewModel : ObservableObject
{
    private static readonly MediaBrush IdleBrush = new SolidColorBrush(MediaColor.FromRgb(64, 64, 64));
    private static readonly MediaBrush RunningBrush = new SolidColorBrush(MediaColor.FromRgb(58, 50, 36));
    private static readonly MediaBrush SuccessBrush = new SolidColorBrush(MediaColor.FromRgb(38, 56, 39));
    private static readonly MediaBrush WarningBrush = new SolidColorBrush(MediaColor.FromRgb(123, 75, 20));
    private static readonly MediaBrush FailureBrush = new SolidColorBrush(MediaColor.FromRgb(94, 5, 5));

    private readonly DistroDoctorService _doctorService;
    private readonly StringBuilder _displayLog = new();
    private string _logText = string.Empty;
    private string _statusText = "Ready";
    private MediaBrush _statusBrush = IdleBrush;
    private string _downloadButtonText = "Download Log";
    private bool _isRepairMode;
    private bool _isRunning;
    private string? _report;

    public DistroDoctorViewModel(WslService wsl)
    {
        _doctorService = new DistroDoctorService(wsl);
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        DownloadLogCommand = new RelayCommand(DownloadLog, () => CanDownloadLog);
    }

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand DownloadLogCommand { get; }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public MediaBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public string DownloadButtonText
    {
        get => _downloadButtonText;
        private set => SetProperty(ref _downloadButtonText, value);
    }

    public bool IsRepairMode
    {
        get => _isRepairMode;
        set
        {
            if (SetProperty(ref _isRepairMode, value))
            {
                OnPropertyChanged(nameof(IsCheckOnlyMode));
            }
        }
    }

    public bool IsCheckOnlyMode
    {
        get => !IsRepairMode;
        set
        {
            if (value)
            {
                IsRepairMode = false;
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanClose));
                OnPropertyChanged(nameof(CanDownloadLog));
                RunCommand.RaiseCanExecuteChanged();
                DownloadLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanClose => !IsRunning;
    public bool CanDownloadLog => !IsRunning && !string.IsNullOrWhiteSpace(_report);

    public void NotifyCloseBlocked()
    {
        StatusText = "Doctor is still running";
        StatusBrush = RunningBrush;
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        _report = null;
        DownloadButtonText = "Download Log";
        DownloadLogCommand.RaiseCanExecuteChanged();
        _displayLog.Clear();
        LogText = string.Empty;
        StatusText = "Running…";
        StatusBrush = RunningBrush;

        var modeLabel = IsRepairMode ? "check and repair" : "check only";
        AppendOutput($"Starting Distro Doctor ({modeLabel})...{Environment.NewLine}");

        try
        {
            if (!await _doctorService.DistroExistsAsync().ConfigureAwait(true))
            {
                var missingMessage = $"{LauncherConstants.DistroName} is not currently installed.";
                AppendOutput($"[FAIL] {missingMessage}{Environment.NewLine}");
                var missingResult = new CommandResult(1, _displayLog.ToString(), string.Empty);
                _report = DistroDoctorService.BuildReport(IsRepairMode, missingResult);
                StatusText = "Distro not installed";
                StatusBrush = FailureBrush;
                return;
            }

            var result = await _doctorService.RunAsync(IsRepairMode, AppendOutput).ConfigureAwait(true);
            _report = DistroDoctorService.BuildReport(IsRepairMode, result);
            ApplyResultStatus(result);
        }
        catch (Exception ex)
        {
            AppendOutput($"[FAIL] {ex.Message}{Environment.NewLine}");
            var failureResult = new CommandResult(1, _displayLog.ToString(), ex.ToString());
            _report = DistroDoctorService.BuildReport(IsRepairMode, failureResult);
            StatusText = "Doctor failed to run";
            StatusBrush = FailureBrush;
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(CanDownloadLog));
            DownloadLogCommand.RaiseCanExecuteChanged();
        }
    }

    private void ApplyResultStatus(CommandResult result)
    {
        var summary = DistroDoctorService.ParseFinalSummary(result.StandardOutput);
        if (summary is null)
        {
            StatusText = result.Succeeded ? "Incomplete result" : $"Failed (exit {result.ExitCode})";
            StatusBrush = FailureBrush;
            return;
        }

        if (!result.Succeeded || summary.Failed > 0)
        {
            StatusText = $"Needs attention · {summary.Failed} failed";
            StatusBrush = FailureBrush;
        }
        else if (summary.Warnings > 0)
        {
            StatusText = $"Completed · {summary.Warnings} warnings";
            StatusBrush = WarningBrush;
        }
        else
        {
            StatusText = "Completed successfully";
            StatusBrush = SuccessBrush;
        }
    }

    private void AppendOutput(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => AppendOutput(text));
            return;
        }

        _displayLog.Append(text);
        LogText = _displayLog.ToString();
    }

    private void DownloadLog()
    {
        if (string.IsNullOrWhiteSpace(_report))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DiagnosticReportPaths.OutputDirectory);
            var outputPath = DiagnosticReportPaths.CreateTimestampedPath("distro-doctor");
            File.WriteAllText(outputPath, _report);
            DownloadButtonText = "Downloaded";
        }
        catch (Exception ex)
        {
            AppendOutput($"{Environment.NewLine}[FAIL] Could not download log: {ex.Message}{Environment.NewLine}");
            StatusText = "Log download failed";
            StatusBrush = FailureBrush;
        }
    }
}
