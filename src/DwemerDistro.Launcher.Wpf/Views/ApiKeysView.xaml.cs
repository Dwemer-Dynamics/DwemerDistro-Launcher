// One global encrypted key table; editing a shared row preserves every consumer's key ID.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DwemerDistro.Launcher.Wpf.Services;
using Button = System.Windows.Controls.Button;
namespace DwemerDistro.Launcher.Wpf.Views;

public sealed class ProviderKeyRow : INotifyPropertyChanged {
    public SharedKey? Key { get; private set; }
    public IReadOnlyList<string> Providers { get; }
    private readonly IReadOnlyDictionary<string,KeyProvider> _catalog;
    private string _provider, _label, _origin, _revealed="";
    private bool _pending, _shown;
    public bool IsDraft => Key is null;
    private readonly bool _custom;
    public bool ProviderEditable => _custom && IsDraft;
    public bool Available => true;
    public string SecretName => $"API key for {Label}";
    public string SaveName => $"Save global key {Label}";
    public string Provider { get=>_provider; set { if(Key is not null) return; _provider=value; if(value!="custom_openai") Label=DisplayProvider(value); Changed(nameof(IsCustomName)); _origin=_catalog.GetValueOrDefault(value)?.Origin??""; Changed(nameof(Provider)); Changed(nameof(ProviderLabel)); Changed(nameof(Origin)); Changed(nameof(NeedsOrigin)); Changed(nameof(ShowOrigin)); Changed(nameof(CanSave)); } }
    public bool IsCustomName => Provider=="custom_openai";
    public string ProviderLabel => DisplayProvider(Provider);
    public string Label { get=>_label; set { _label=value; Changed(nameof(Label)); Changed(nameof(CanSave)); } }
    public string Origin { get=>_origin; set { _origin=value; Changed(nameof(Origin)); Changed(nameof(CanSave)); } }
    public bool NeedsOrigin => IsDraft && _catalog.GetValueOrDefault(Provider)?.Origin is null;
    public bool ShowOrigin => NeedsOrigin && HasPending;
    private string _testResult="";
    public string TestMessage { get; private set; }="";
    public bool CanTest => Configured && Key?.TestSupported==true && !HasPending;
    public string TestHint => HasPending ? "Save the replacement before testing." : Key is null ? "Save a key before testing." : Key.TestSupported ? "Quick read-only check of the saved key. No generation." : "Quick check unavailable for this provider and origin.";
    public string StateColor => _testResult switch { "rejected"=>"#EE9999", "unavailable" or "unsupported" or "rate_limited" or "reachable"=>"#E5C66A", _=>Configured ? "#8DD69A" : "#AAAAAA" };
    public void SetTestResult(string result,string message) { _testResult=result; TestMessage=message; Changed(nameof(SavedState)); Changed(nameof(StateColor)); Changed(nameof(TestMessage)); }
    public string SavedState => _testResult switch { "checking"=>"Checking...", "accepted"=>"Accepted", "rejected"=>"Rejected", "reachable"=>"Reachable", "rate_limited"=>"Rate limited", "unsupported"=>"No quick check", "unavailable"=>"Check failed", _=>StoredState };
    private string StoredState => Key is null ? "Not set" : Key.Configured ? "Configured" : "Unavailable";
    public bool Configured => Key?.Configured==true;
    public string Preview => Key?.Preview is {Length:>0} value ? value : Key is not null ? "********" : "Not set";
    public bool HasPending => _pending;
    public bool CanSave => Key is not null || _pending && Label.Trim().Length>0 && (!NeedsOrigin || Origin.Trim().Length>0);
    public bool CanShow => Configured;
    public bool IsRevealed => _shown;
    public string Revealed => _revealed;
    public string RevealLabel => _shown ? "Hide" : "Show";
    internal WeakReference<PasswordBox>? SecretInput;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ProviderKeyRow(SharedKey? key,string provider,IReadOnlyDictionary<string,KeyProvider> catalog,bool custom=false) {
        Key=key; _catalog=catalog; Providers=catalog.Keys.ToList(); _provider=key?.Provider??provider;
        _label=key?.Label??(custom?"":DisplayProvider(provider)); _origin=key?.Origin??catalog.GetValueOrDefault(provider)?.Origin??""; _custom=custom;
    }
    public static string DisplayProvider(string provider)=>provider switch {
        "openrouter"=>"OpenRouter", "openai"=>"OpenAI", "google"=>"Google", "azure"=>"Azure", "elevenlabs"=>"ElevenLabs",
        "nano_gpt"=>"Nano-GPT", "deepl"=>"DeepL", "custom_openai"=>"Custom", _=>char.ToUpperInvariant(provider[0])+provider[1..]
    };
    public void PendingChanged(bool pending) { if(pending) SetTestResult("",""); _pending=pending; Changed(nameof(CanTest)); Changed(nameof(TestHint)); Changed(nameof(HasPending)); Changed(nameof(ShowOrigin)); Changed(nameof(CanSave)); }
    public void Reveal(string secret) { _revealed=secret; _shown=true; Changed(nameof(Revealed)); Changed(nameof(IsRevealed)); Changed(nameof(RevealLabel)); }
    public void Hide() { _revealed=""; _shown=false; Changed(nameof(Revealed)); Changed(nameof(IsRevealed)); Changed(nameof(RevealLabel)); }
    public void Apply(SharedKey key) {
        SetTestResult("",""); Key=key; _label=key.Label; _origin=key.Origin;
        foreach(var name in new[]{nameof(Label),nameof(Origin),nameof(ProviderEditable),nameof(IsCustomName),nameof(IsDraft),nameof(NeedsOrigin),nameof(ShowOrigin),nameof(SavedState),nameof(Configured),nameof(Preview),nameof(CanSave),nameof(CanShow),nameof(CanTest),nameof(TestHint),nameof(StateColor)}) Changed(name);
    }
    private void Changed(string name)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
}

public partial class ApiKeysView : System.Windows.Controls.UserControl {
    private readonly ApiKeysService _service;
    private SharedKeysData? _snapshot;
    private readonly ObservableCollection<ProviderKeyRow> _rows=[];
    private bool _busy, _needsReload;
    private int _generation;
    public bool IsBusy=>_busy;
    public ApiKeysView(ApiKeysService service) { InitializeComponent(); _service=service; Rows.ItemsSource=_rows; UpdateState(); }
    private async void View_Loaded(object sender,RoutedEventArgs e) { if(!_busy) await ReloadAsync(); }
    private void View_Unloaded(object sender,RoutedEventArgs e)=>ClearInputs();
    public void ShowCloseBlocked()=>Status.Text="Wait for the API key operation to finish before closing the launcher.";
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await ReloadAsync();
    private void UpdateState() {
        Rows.IsEnabled=AddCustom.IsEnabled=!_busy&&!_needsReload&&_snapshot is not null;
        Refresh.IsEnabled=!_busy;
    }
    private void ClearInputs() {
        _generation++;
        foreach(var row in _rows) { row.Hide(); if(row.SecretInput?.TryGetTarget(out var input)==true) input.Clear(); row.PendingChanged(false); }
    }
    private async Task ReloadAsync() {
        _busy=true; ClearInputs(); UpdateState();
        try {
            var result=await _service.RunAsync("global-status");
            _snapshot=result.Ok ? result.Snapshot : null; _rows.Clear();
            if(_snapshot?.Providers is not {Count:>0} providers) { _needsReload=true; Status.Text="Global API keys are unavailable. Reload to retry."; return; }
            _needsReload=false;
            foreach(var key in _snapshot.Keys??[]) _rows.Add(new(key,key.Provider,providers));
            foreach(var provider in providers.Keys) if(provider!="custom_openai" && !(_snapshot.Keys??[]).Any(k=>k.Provider==provider)) _rows.Add(new(null,provider,providers));
            Status.Text="Global keys are shared by their assigned consumers. Saved keys stay masked until Show.";
        } finally { _busy=false; UpdateState(); }
    }
    private void AddCustom_Click(object sender,RoutedEventArgs e) {
        if(_busy||_needsReload||_snapshot?.Providers is not { } providers) return;
        var row=new ProviderKeyRow(null,"custom_openai",providers,true); _rows.Add(row); Rows.SelectedItem=row; Rows.ScrollIntoView(row);
        Status.Text="Name this custom key and paste its key. Enter its API origin when required.";
    }
    private void RowSecret_Changed(object sender,RoutedEventArgs e) {
        if(sender is PasswordBox {DataContext:ProviderKeyRow row} input) { row.SecretInput=new(input); row.PendingChanged(input.SecurePassword.Length>0); }
    }
    private void RowSecret_ContextChanged(object sender,DependencyPropertyChangedEventArgs e) {
        if(e.OldValue is ProviderKeyRow old) { old.SecretInput=null; old.PendingChanged(false); }
        if(sender is PasswordBox input) { input.Clear(); if(e.NewValue is ProviderKeyRow row) row.SecretInput=new(input); }
    }
    private void RowSecret_Unloaded(object sender,RoutedEventArgs e) { if(sender is PasswordBox input) input.Clear(); }
    private async void SaveRow_Click(object sender,RoutedEventArgs e) {
        if(_busy||_needsReload||sender is not Button {Tag:ProviderKeyRow row}||!row.CanSave) return;
        var secret=row.SecretInput?.TryGetTarget(out var input)==true ? input.Password : "";
        object payload=row.Key is { } key ? new {key_id=key.Id,revision=key.Revision,label=row.Label.Trim(),secret}
            : new {provider=row.Provider,label=row.Label.Trim(),origin=row.NeedsOrigin ? row.Origin.Trim() : null,secret};
        if(row.SecretInput?.TryGetTarget(out var editor)==true) editor.Clear();
        row.SetTestResult("",""); row.Hide(); _busy=true; UpdateState();
        try {
            var result=await _service.RunAsync("global-save",payload);
            if(result.Ok && result.Data?.Deserialize<SharedKey>(ApiKeysService.JsonOptions) is { } saved) {
                row.Apply(saved);
                if(_snapshot?.Keys is { } keys) { var index=keys.FindIndex(k=>k.Id==saved.Id); if(index<0) keys.Add(saved); else keys[index]=saved; }
                Status.Text=result.Outcome=="unchanged" ? "The current global key was kept." : $"Saved global key {saved.Label}. Existing assignments retain the same key ID.";
            } else {
                if(result.Outcome is null or "unavailable" or "conflict") _needsReload=true;
                Status.Text=result.Error??(result.Errors?.Count>0 ? string.Join("; ",result.Errors.Select(x=>$"{x.Key}: {x.Reason}")) : $"Global key was not saved ({result.Outcome}). Reload before retrying.");
            }
        } finally { _busy=false; UpdateState(); }
    }
    // Test only the saved revision, leaving pending input and the global key itself untouched.
    private async void TestRow_Click(object sender,RoutedEventArgs e) {
        if(_busy||_needsReload||sender is not Button {Tag:ProviderKeyRow row}||row.Key is not { } key||!row.CanTest) return;
        row.Hide(); row.SetTestResult("checking","Checking the saved key...");
        _busy=true; UpdateState(); var generation=_generation;
        try {
            var result=await _service.RunAsync("global-test",new {key_id=key.Id,revision=key.Revision});
            if(generation!=_generation) { row.SetTestResult("",""); return; }
            if(result.Ok && result.Data is { } data && data.TryGetProperty("result",out var status) && data.TryGetProperty("message",out var message)) {
                var state=status.GetString()??"unavailable";
                row.SetTestResult(state,message.GetString()??"The key could not be checked.");
                Status.Text=$"{row.ProviderLabel}: {row.TestMessage}";
            } else {
                row.SetTestResult("unavailable",result.Outcome=="conflict" ? "This saved key changed. Reload before testing." : "The saved key could not be checked. Reload and retry.");
                if(result.Outcome=="conflict") _needsReload=true;
                Status.Text=row.TestMessage;
            }
        } finally { _busy=false; UpdateState(); }
    }
    // Navigation invalidates pending reveal replies so hidden pages never retain a late plaintext response.
    private async void RevealRow_Click(object sender,RoutedEventArgs e) {
        if(_busy||_needsReload||sender is not Button {Tag:ProviderKeyRow row}||row.Key is not { } key||!row.CanShow) return;
        if(row.IsRevealed) { row.Hide(); return; }
        foreach(var other in _rows) other.Hide();
        var generation=_generation; _busy=true; UpdateState();
        try {
            var result=await _service.RunAsync("global-reveal",new {key_id=key.Id,revision=key.Revision});
            if(generation!=_generation) return;
            if(result.Ok && result.Data is { } data && data.TryGetProperty("secret",out var value) && value.ValueKind==JsonValueKind.String) {
                row.Reveal(value.GetString()!); Status.Text=$"Showing {row.Label}. Select Hide when finished.";
            } else Status.Text="The global key could not be shown. Reload to check its latest state.";
        } finally { _busy=false; UpdateState(); }
    }
}
