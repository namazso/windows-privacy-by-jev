using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.State;

/// <summary>What one policy is set to in one section, and whether the registry agrees.</summary>
public sealed class ScopeState
{
    public required PolicyDef Policy { get; init; }
    public required Scope Scope { get; init; }

    /// <summary>The entries of the policy object that belong to the policy; empty when not configured.</summary>
    public required IReadOnlyList<PolEntry> Entries { get; init; }

    /// <summary>null when the policy object does not configure the policy.</summary>
    public required Decoded? Configured { get; init; }

    public required LiveResult Live { get; init; }

    public PolicyState State => Configured?.State ?? PolicyState.NotConfigured;

    public bool IsConfigured => Configured is not null;

    /// <summary>The state with its option values, as one line.</summary>
    public string Summary => Configured?.Summary ?? PolicyState.NotConfigured.Display();

    /// <summary>What the Current state column shows for this section.</summary>
    public string Text => Live.Status switch
    {
        LiveStatus.NotApplied => Summary + " (not applied)",
        LiveStatus.RegistryOnly when Live.Decoded is { } d => d.Summary + " (registry only)",
        _ => Summary,
    };

    public string Hash => SectionState.Hash(Entries);
}

/// <summary>Reads the policy object and the registry and works out the state of every policy.</summary>
public sealed class PolicyStateService
{
    private readonly CatalogStore _catalog;
    private readonly IRegistryReader _registry;
    private readonly PolicyDecoder _decoder;
    private readonly LiveComparer _comparer;
    private Dictionary<(string Uid, Scope Scope), ScopeState> _states = [];
    private Dictionary<Scope, List<PolEntry>> _unmapped = [];

    public PolicyStateService(CatalogStore catalog, IGpoStore store, IRegistryReader registry)
    {
        _catalog = catalog;
        _registry = registry;
        Store = store;
        _decoder = new PolicyDecoder(catalog.Index);
        _comparer = new LiveComparer(_decoder, registry);
        Snapshot = store.Read();
        Refresh();
    }

    public IGpoStore Store { get; }

    /// <summary>False when there is no registry to compare the policy with, as for a test folder.</summary>
    public bool ComparesRegistry => _registry.IsAvailable;
    public PolicyDecoder Decoder => _decoder;
    public GpoSnapshot Snapshot { get; private set; }

    public event EventHandler? Refreshed;

    public ScopeState this[string uid, Scope scope] => _states[(uid, scope)];

    public IReadOnlyList<ScopeState> For(PolicyDef pol) => pol.Scopes.Select(s => _states[(pol.Uid, s)]).ToList();

    /// <summary>Entries of the policy object that no policy in the catalogue accounts for.</summary>
    public IReadOnlyList<PolEntry> Unmapped(Scope scope) => _unmapped.GetValueOrDefault(scope) ?? [];

    public void Refresh()
    {
        (_registry as WindowsRegistryReader)?.Reset();
        Snapshot = Store.Read();

        var owned = new Dictionary<(string, Scope), List<PolEntry>>();
        var unmapped = new Dictionary<Scope, List<PolEntry>>();
        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            unmapped[scope] = [];
            foreach (var entry in Snapshot.Entries(scope))
            {
                var uids = _catalog.Index.Resolve(scope, entry).Select(h => h.Uid).Distinct().ToList();
                if (uids.Count == 0)
                    unmapped[scope].Add(entry);
                foreach (var uid in uids)
                {
                    if (!owned.TryGetValue((uid, scope), out var list))
                        owned[(uid, scope)] = list = [];
                    list.Add(entry);
                }
            }
        }

        var states = new Dictionary<(string, Scope), ScopeState>();
        foreach (var pol in _catalog.Policies)
        {
            foreach (var scope in pol.Scopes)
            {
                IReadOnlyList<PolEntry> entries = owned.GetValueOrDefault((pol.Uid, scope)) ?? [];
                states[(pol.Uid, scope)] = new ScopeState
                {
                    Policy = pol,
                    Scope = scope,
                    Entries = entries,
                    Configured = entries.Count > 0 ? _decoder.Decode(pol, scope, entries) : null,
                    Live = _comparer.Evaluate(pol, scope, entries),
                };
            }
        }
        _states = states;
        _unmapped = unmapped;
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
}
