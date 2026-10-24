using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class ApiKeysWindow : Window
{
    private readonly ApiKeysService _service;
    private SharedKeysData? _snapshot;
    private bool _busy, _loading, _needsReload;
    private string? _confirmedOperation;
    private object? _confirmedPayload;

    public ApiKeysWindow(ApiKeysService service)
    {
        InitializeComponent();
        _service = service;
        Provider.ItemsSource = new[] { "openrouter", "openai", "inworld", "custom_openai" };
        Provider.SelectedIndex = 0;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Reload after every operation so revisions and connection assignments never come from an optimistic local update.
    private async Task ReloadAsync(string? message = null)
    {
        _busy = true;
        UpdateState();
        var selectedId = (SavedKey.SelectedItem as SharedKey)?.Id;
        var connectionId = (Connection.SelectedItem as KeyConnection)?.Id;
        try
        {
            var result = await _service.RunAsync("status");
            _snapshot = result.Ok ? result.Snapshot : null;
            _needsReload = _snapshot is null;
            _loading = true;
            SavedKey.ItemsSource = _snapshot?.Keys;
            SavedKey.SelectedItem = _snapshot?.Keys?.FirstOrDefault(k => k.Id == selectedId);
            Connection.ItemsSource = _snapshot?.Connections;
            Connection.SelectedItem = _snapshot?.Connections?.FirstOrDefault(c => c.Id == connectionId) ?? _snapshot?.Connections?.FirstOrDefault();
            _loading = false;
            LoadSelection();
            LegacyNotice.Text = string.Join(" ", _snapshot?.Warnings ?? []);
            Status.Text = _snapshot is null ? result.Error ?? "Shared keys unavailable. Reload before retrying." : message ?? "Ready. Existing assignments are unchanged.";
        }
        finally { _loading = false; _busy = false; UpdateState(); }
    }
    private void Saved_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) LoadSelection(); }
    private void New_Click(object sender, RoutedEventArgs e) { SavedKey.SelectedIndex = -1; LoadSelection(); KeyLabel.Focus(); }
    private void LoadSelection()
    {
        if (NewKey is null) return;
        NewKey.Clear();
        var key = SavedKey.SelectedItem as SharedKey;
        KeyLabel.Text = key?.Label ?? "";
        Provider.SelectedItem = key?.Provider ?? "openrouter";
        Origin.Text = key?.Origin ?? "";
        UpdateState();
    }
    private void Provider_Changed(object sender, SelectionChangedEventArgs e) { NewKey?.Clear(); UpdateState(); }
    private void Connection_Changed(object sender, SelectionChangedEventArgs e) => UpdateState();
    private void Input_Changed(object sender, TextChangedEventArgs e) => UpdateState();
    private void Secret_Changed(object sender, RoutedEventArgs e) => UpdateState();
    private void UpdateState()
    {
        if (SaveKey is null || NewKey is null || Connection is null) return;
        var key = SavedKey.SelectedItem as SharedKey;
        var connection = Connection.SelectedItem as KeyConnection;
        var ready = !_busy && !_needsReload && _snapshot is not null && _confirmedOperation is null;
        Editor.IsEnabled = ready;
        Refresh.IsEnabled = !_busy && _confirmedOperation is null;
        Provider.IsEnabled = key is null;
        Origin.IsEnabled = key is null && Provider.SelectedItem as string == "custom_openai";
        SaveKey.IsEnabled = ready && KeyLabel.Text.Trim().Length > 0 && (key is not null || NewKey.Password.Length > 0);
        TestKey.IsEnabled = DeleteKey.IsEnabled = ready && key is not null;
        BindKey.IsEnabled = ready && key is not null && connection is not null;
        DetachKey.IsEnabled = ready && connection is not null && connection.CredentialSource != "none";
        ImportKey.IsEnabled = ready && connection?.CredentialSource == "environment";
        Assignment.Text = connection is null ? "No CHIM 4 connections available." : connection.KeyId is null
            ? $"Currently using: {connection.CredentialSource}" : $"Currently using: {_snapshot?.Keys?.FirstOrDefault(k => k.Id == connection.KeyId)?.Display ?? connection.KeyId}";
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveKey.IsEnabled || _busy) return;
        var key = SavedKey.SelectedItem as SharedKey;
        if (key is null)
            await MutateAsync("create", new { provider = (string)Provider.SelectedItem, label = KeyLabel.Text.Trim(), origin = Origin.IsEnabled ? Origin.Text.Trim() : null, secret = NewKey.Password });
        else
            await MutateAsync("edit", new { key_id = key.Id, revision = key.Revision, label = KeyLabel.Text.Trim(), secret = NewKey.Password });
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (TestKey.IsEnabled && SavedKey.SelectedItem is SharedKey key) await MutateAsync("test", new { key_id = key.Id });
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!DeleteKey.IsEnabled || SavedKey.SelectedItem is not SharedKey key) return;
        var usage = key.Usage?.Count > 0 ? string.Join(", ", key.Usage.Select(u => u.Label)) : "No CHIM 4 connections reference this key.";
        Confirm("delete", new { key_id = key.Id, revision = key.Revision }, $"Delete ‘{key.Label}’? {usage} Referenced keys cannot be deleted. This cannot be undone.");
    }
    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (!BindKey.IsEnabled || Connection.SelectedItem is not KeyConnection connection || SavedKey.SelectedItem is not SharedKey key) return;
        Confirm("bind", new { connection_id = connection.Id, revision = connection.Revision, key_id = key.Id }, $"Change ‘{connection.Label}’ to ‘{key.Label}’? Other connections and profile assignments stay unchanged.");
    }
    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (!DetachKey.IsEnabled || Connection.SelectedItem is not KeyConnection connection) return;
        Confirm("bind", new { connection_id = connection.Id, revision = connection.Revision, key_id = (string?)null }, $"Remove the key assignment from ‘{connection.Label}’? Services requiring a key will stop working until one is selected.");
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!ImportKey.IsEnabled || Connection.SelectedItem is not KeyConnection connection) return;
        Confirm("import-environment", new { connection_id = connection.Id, provider = (string)Provider.SelectedItem }, $"Import ‘{connection.Label}’ as provider ‘{Provider.SelectedItem}’ and bind only this connection? Its model, endpoint and profile assignments are preserved. Let active requests finish first.");
    }
    // Freeze the full revision-bearing request while confirmation is visible.
    private void Confirm(string operation, object payload, string message)
    {
        _confirmedOperation = operation;
        _confirmedPayload = payload;
        NewKey.Clear();
        ConfirmationText.Text = message;
        Confirmation.Visibility = Visibility.Visible;
        UpdateState();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { ClearConfirmation(); UpdateState(); }
    private void ClearConfirmation() { _confirmedOperation = null; _confirmedPayload = null; Confirmation.Visibility = Visibility.Collapsed; }
    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _confirmedOperation is null || _confirmedPayload is null) return;
        var operation = _confirmedOperation; var payload = _confirmedPayload;
        ClearConfirmation();
        await MutateAsync(operation, payload);
    }
    private async Task MutateAsync(string operation, object payload)
    {
        _busy = true;
        UpdateState();
        try
        {
            var result = await _service.RunAsync(operation, payload);
            NewKey.Clear();
            // Unknown outcomes require an explicit reload, never an automatic write retry.
            if (result.Outcome is "unavailable" || result.Outcome is null)
            {
                _needsReload = true;
                Status.Text = result.Error ?? "Outcome unknown. Reload before retrying.";
                return;
            }
            var message = result.Error ?? $"Result: {result.Outcome}";
            if (result.Errors?.Count > 0) message += ". " + string.Join("; ", result.Errors.Select(error => $"{error.Key}: {error.Reason}"));
            if (operation == "test" && result.Data is { } data && data.TryGetProperty("result", out var tested)) message = $"Key test: {tested.GetString()}";
            await ReloadAsync(message);
        }
        finally { _busy = false; UpdateState(); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; Status.Text = "Wait for the current operation to finish."; }
        else { NewKey.Clear(); _confirmedPayload = null; }
    }
}
