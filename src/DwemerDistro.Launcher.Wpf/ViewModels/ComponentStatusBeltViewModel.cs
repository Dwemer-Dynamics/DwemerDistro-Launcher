using System.Collections.ObjectModel;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>One cell of the header belt: the service's mark, its actual port, and what the last probe proved.</summary>
public sealed class ComponentStatusItemViewModel : ObservableObject
{
    private ComponentServiceState _state = ComponentServiceState.Checking;
    private string _statusText = "Checking...";
    private int _port;

    public ComponentStatusItemViewModel(ComponentServiceDefinition definition)
    {
        Definition = definition;
        _port = definition.Port;
        ArtworkSource = ComponentServiceStatus.ArtworkRoot + definition.Artwork;
    }

    public ComponentServiceDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string ArtworkSource { get; }

    public ComponentServiceState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(ShortStatusText));
        }
    }

    /// <summary>One or two words for the hover tooltip; StatusText keeps the probe's full explanation.</summary>
    public string ShortStatusText => State switch
    {
        ComponentServiceState.Running => "Running",
        ComponentServiceState.Starting => "Starting",
        ComponentServiceState.Stopped or ComponentServiceState.DistroStopped => "Stopped",
        ComponentServiceState.NotResponding => "Not responding",
        ComponentServiceState.Disabled => "Disabled",
        ComponentServiceState.Checking => "Checking",
        ComponentServiceState.Paused => "Paused",
        _ => "Unknown",
    };

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(Summary));
        }
    }

    public int Port
    {
        get => _port;
        private set
        {
            if (SetProperty(ref _port, value))
            {
                OnPropertyChanged(nameof(PortText));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string PortText => $"Port {Port}";

    /// <summary>One line for screen readers and the overflow list: name, port, status.</summary>
    public string Summary => $"{Name}, port {Port}. {StatusText}";

    public void Set(ComponentServiceState state, string statusText, int? port = null)
    {
        State = state;
        StatusText = statusText;
        if (port is { } value) Port = value;
    }
}

/// <summary>
/// The window-lifetime, read-only service status shown in the header on every tab. Items appear once a probe has
/// proved them installed, in the fixed catalog order; the view decides how many fit and the rest overflow.
/// </summary>
public sealed class ComponentStatusBeltViewModel : ObservableObject
{
    private readonly Dictionary<string, ComponentStatusItemViewModel> _byId;
    private double _availableWidth = double.PositiveInfinity;
    private bool _hasProbed;
    private string _placeholderText = "Service status appears once Dwemer Distro is running.";

    public ComponentStatusBeltViewModel()
    {
        _byId = ComponentServiceStatus.Services.ToDictionary(service => service.Id, service => new ComponentStatusItemViewModel(service), StringComparer.Ordinal);
    }

    /// <summary>Installed services, in the fixed order.</summary>
    public ObservableCollection<ComponentStatusItemViewModel> Items { get; } = [];

    public ObservableCollection<ComponentStatusItemViewModel> VisibleItems { get; } = [];

    public ObservableCollection<ComponentStatusItemViewModel> OverflowItems { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasOverflow => OverflowItems.Count > 0;

    /// <summary>"+N" beside visible cells, or a bare count when none fit and the dropdown is the whole belt.</summary>
    public string OverflowText => VisibleItems.Count == 0 ? $"{OverflowItems.Count}" : $"+{OverflowItems.Count}";

    public string OverflowAccessibleName => VisibleItems.Count == 0
        ? $"Component services, {OverflowItems.Count}"
        : $"{OverflowItems.Count} more component services";

    /// <summary>The worst state among overflowed services, so a hidden red still shows on the +N button.</summary>
    public ComponentServiceState OverflowState => Worst(OverflowItems);

    public bool ShowsPlaceholder => !HasItems;

    public string PlaceholderText
    {
        get => _placeholderText;
        private set => SetProperty(ref _placeholderText, value);
    }

    public bool HasProbed => _hasProbed;

    /// <summary>A 28px cell plus its 4px gap.</summary>
    public const double CellPitch = 32;

    /// <summary>Room kept for the +N toggle (up to "+11" and its light) whenever anything overflows.</summary>
    public const double OverflowReserve = 50;

    /// <summary>Fits as many cells as the header's spare width allows; the rest go behind +N.</summary>
    public void SetAvailableWidth(double width)
    {
        _availableWidth = Math.Max(0, width);
        Split();
    }

    /// <summary>Applies one successful probe. Services the catalog proves absent leave the belt.</summary>
    public void Apply(IReadOnlyList<ComponentServiceProbe> probes, bool starting)
    {
        var byId = probes.ToDictionary(probe => probe.Id, StringComparer.Ordinal);
        var installed = new List<ComponentStatusItemViewModel>();
        foreach (var service in ComponentServiceStatus.Services)
        {
            if (!byId.TryGetValue(service.Id, out var probe) || probe.Installed == "absent") continue;
            var item = _byId[service.Id];
            var (state, text) = ComponentServiceStatus.Describe(service, probe, starting);
            item.Set(state, text, probe.Port);
            installed.Add(item);
        }

        _hasProbed = true;
        PlaceholderText = "No component services are installed.";
        Replace(installed);
    }

    /// <summary>Every known cell takes the same state, e.g. while the distro is stopped or a check failed.</summary>
    public void SetAll(ComponentServiceState state, string statusText, string placeholderText)
    {
        foreach (var item in Items) item.Set(state, statusText);
        if (!_hasProbed) PlaceholderText = placeholderText;
        OnPropertyChanged(nameof(OverflowState));
    }

    private void Replace(List<ComponentStatusItemViewModel> installed)
    {
        if (!installed.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var item in installed) Items.Add(item);
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ShowsPlaceholder));
            Split();
        }

        OnPropertyChanged(nameof(OverflowState));
    }

    private void Split()
    {
        var visible = Items.Count * CellPitch <= _availableWidth
            ? Items.Count
            : (int)Math.Max(0, Math.Floor((_availableWidth - OverflowReserve) / CellPitch));
        Sync(VisibleItems, Items.Take(visible));
        Sync(OverflowItems, Items.Skip(visible));
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(OverflowText));
        OnPropertyChanged(nameof(OverflowAccessibleName));
        OnPropertyChanged(nameof(OverflowState));
    }

    private static void Sync(ObservableCollection<ComponentStatusItemViewModel> target, IEnumerable<ComponentStatusItemViewModel> source)
    {
        var wanted = source.ToList();
        if (wanted.SequenceEqual(target)) return;
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private static ComponentServiceState Worst(IEnumerable<ComponentStatusItemViewModel> items)
    {
        var states = items.Select(item => item.State).ToList();
        if (states.Count == 0) return ComponentServiceState.Unknown;
        if (states.Any(state => state is ComponentServiceState.Stopped or ComponentServiceState.NotResponding)) return ComponentServiceState.Stopped;
        if (states.Contains(ComponentServiceState.Starting)) return ComponentServiceState.Starting;
        if (states.All(state => state == ComponentServiceState.Running)) return ComponentServiceState.Running;
        return ComponentServiceState.Unknown;
    }
}
