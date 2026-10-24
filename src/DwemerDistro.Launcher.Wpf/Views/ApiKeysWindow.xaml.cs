using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class ApiKeysWindow : Window
{
    private readonly ApiKeysService _service;
    private ApiKeysResult? _snapshot;
    private bool _busy;
    private string? _confirmation;
    private string? _confirmedProvider;
    private string? _confirmedTarget;

    public ApiKeysWindow(ApiKeysService service)
    {
        _service = service;
        InitializeComponent();
        UpdateState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) { NewKey?.Clear(); CancelConfirmation(); UpdateState(); }
    private void Key_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private async Task ReloadAsync()
    {
        SetBusy(true);
        try
        {
            var provider = (Provider.SelectedItem as ApiKeyProvider)?.Id;
            var target = (Target.SelectedItem as ApiKeyTarget)?.Id;
            var result = await _service.RunAsync("status");
            if (!result.Ok) { Status.Text = result.Error; _snapshot = null; return; }
            _snapshot = result;
            Provider.ItemsSource = result.Providers ?? [];
            var targets = new List<ApiKeyTarget> { new("", "Shared default", true, "", []) };
            targets.AddRange(result.Targets ?? []);
            Target.ItemsSource = targets;
            Provider.SelectedItem = result.Providers?.FirstOrDefault(p => p.Id == provider)
                ?? result.Providers?.FirstOrDefault(p => p.Id == "openrouter") ?? result.Providers?.FirstOrDefault();
            Target.SelectedItem = targets.FirstOrDefault(t => t.Id == target) ?? targets[0];
            Status.Text = "Ready. Shared defaults apply to empty keys and mods already using a shared key.";
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Editor.IsEnabled = !busy && _confirmation is null;
        Refresh.IsEnabled = !busy;
        if (busy) Status.Text = "Working…";
        UpdateState();
    }

    private void UpdateState()
    {
        if (SaveKey is null || Target is null || Provider is null) return;
        var provider = Provider.SelectedItem as ApiKeyProvider;
        var target = Target.SelectedItem as ApiKeyTarget;
        var ready = !_busy && _snapshot is not null && provider is not null && target?.Available == true && _confirmation is null;
        SaveKey.IsEnabled = ready && NewKey.Password.Length > 0;
        UseShared.IsEnabled = ready && target!.Id.Length > 0;
        RemoveShared.IsEnabled = ready && target!.Id.Length == 0 && provider!.Configured;
        if (target is null || provider is null) { CurrentStatus.Text = "Select a provider and destination."; return; }
        if (!target.Available) { CurrentStatus.Text = target.Reason; return; }
        var value = target.Id.Length == 0 ? provider : target.Providers.FirstOrDefault(p => p.Id == provider.Id);
        CurrentStatus.Text = value?.Configured == true
            ? target.Id.Length == 0 ? "Shared key configured" : value.Source == "shared" ? "Using shared key" : "Using a mod-specific key"
            : "No key configured";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !SaveKey.IsEnabled) return;
        var provider = ((ApiKeyProvider)Provider.SelectedItem).Id;
        var target = ((ApiKeyTarget)Target.SelectedItem).Id;
        await MutateAsync(target.Length == 0 ? "set-shared" : "set-override", provider, target.Length == 0 ? null : target, NewKey.Password);
    }

    private void UseShared_Click(object sender, RoutedEventArgs e) => Confirm("use-shared",
        "Replace this mod's key with the shared default? If no shared key is set, this mod's key will be cleared.");
    private void Remove_Click(object sender, RoutedEventArgs e) => Confirm("remove-shared",
        "Remove this shared key? Compatible mods using it will have that key cleared. Mod-specific keys are preserved.");

    private void Confirm(string operation, string text)
    {
        if (_busy || _confirmation is not null) return;
        _confirmedProvider = (Provider.SelectedItem as ApiKeyProvider)?.Id;
        _confirmedTarget = (Target.SelectedItem as ApiKeyTarget)?.Id;
        if (_confirmedProvider is null || _confirmedTarget is null) return;
        _confirmation = operation;
        NewKey.Clear();
        Editor.IsEnabled = false;
        ConfirmationText.Text = text;
        Confirmation.Visibility = Visibility.Visible;
        Refresh.IsEnabled = false;
        UpdateState();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { CancelConfirmation(); UpdateState(); }
    private void CancelConfirmation()
    {
        _confirmation = _confirmedProvider = _confirmedTarget = null;
        if (Confirmation is null) return;
        Confirmation.Visibility = Visibility.Collapsed;
        Editor.IsEnabled = !_busy;
        Refresh.IsEnabled = !_busy;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _confirmation is null || _confirmedProvider is null) return;
        var operation = _confirmation; var provider = _confirmedProvider; var target = _confirmedTarget;
        CancelConfirmation();
        await MutateAsync(operation, provider, operation == "use-shared" ? target : null, null);
    }

    private async Task MutateAsync(string operation, string provider, string? target, string? key)
    {
        SetBusy(true);
        try
        {
            var result = await _service.RunAsync(operation, provider, target, key);
            if (!result.Ok) { Status.Text = result.Error; return; }
            NewKey.Clear();
            await ReloadAsync();
            Status.Text = result.Warnings?.Count > 0 ? "Saved, with some mod updates pending. " + string.Join(" ", result.Warnings) : "Saved.";
        }
        finally { SetBusy(false); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; Status.Text = "Wait for the current API key operation to finish."; }
        else NewKey.Clear();
    }
}
