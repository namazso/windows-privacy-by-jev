using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Decode;

public enum HitRole
{
    Main,
    Element,
    EnabledList,
    DisabledList,
    EnumValueList,
    BoolList,
    List,
}

/// <summary>A policy that owns a registry value, and in what capacity.</summary>
public sealed record Hit(string Uid, HitRole Role, string? ElemId = null)
{
    public bool IsPrimary => Role is HitRole.Main or HitRole.Element;
}

/// <summary>Maps registry values back to the policies that own them. Mirrors pipeline/regindex.py.</summary>
public sealed class RegIndex
{
    private readonly Dictionary<(Scope, string Key, string Name), List<Hit>> _values = [];
    private readonly Dictionary<(Scope, string Key), List<Hit>> _lists = [];

    public RegIndex(IEnumerable<PolicyDef> policies)
    {
        foreach (var pol in policies)
        {
            foreach (var scope in pol.Scopes)
                AddPolicy(scope, pol);
        }
    }

    public IReadOnlyList<Hit> Resolve(Scope scope, PolEntry entry)
    {
        var key = Slot.NormKey(entry.Key);
        var hits = new List<Hit>();
        if (entry.Marker is PolMarker.None or PolMarker.Del
            && _values.TryGetValue((scope, key, entry.Name.ToLowerInvariant()), out var owners))
            hits.AddRange(owners);
        // A value owned directly by a policy or element outranks catch-all enabled/disabled lists.
        var primary = hits.Where(h => h.IsPrimary).ToList();
        if (primary.Count > 0)
            return primary;
        if (_lists.TryGetValue((scope, key), out var lists))
        {
            foreach (var h in lists)
            {
                if (!hits.Contains(h))
                    hits.Add(h);
            }
        }
        return hits;
    }

    /// <summary>The hits of <paramref name="entry"/> that belong to one policy.</summary>
    public IEnumerable<Hit> Resolve(Scope scope, PolEntry entry, string uid) =>
        Resolve(scope, entry).Where(h => h.Uid == uid);

    public bool Owns(Scope scope, PolEntry entry, string uid) => Resolve(scope, entry, uid).Any();

    /// <summary>True when a policy or element names this value outright, so a list in the same key leaves it alone.</summary>
    public bool HasPrimaryOwner(Scope scope, string key, string name) =>
        _values.TryGetValue((scope, Slot.NormKey(key), name.ToLowerInvariant()), out var owners)
        && owners.Any(h => h.IsPrimary);

    private void Add(Scope scope, string key, string? name, Hit hit)
    {
        if (string.IsNullOrEmpty(name))
            return;
        var id = (scope, Slot.NormKey(key), name.ToLowerInvariant());
        if (!_values.TryGetValue(id, out var bucket))
            _values[id] = bucket = [];
        if (!bucket.Contains(hit))
            bucket.Add(hit);
    }

    private void AddPolicy(Scope scope, PolicyDef pol)
    {
        Add(scope, pol.RegistryKey, pol.ValueName, new Hit(pol.Uid, HitRole.Main));
        foreach (var it in pol.EnabledList)
            Add(scope, it.Key, it.Name, new Hit(pol.Uid, HitRole.EnabledList));
        foreach (var it in pol.DisabledList)
            Add(scope, it.Key, it.Name, new Hit(pol.Uid, HitRole.DisabledList));
        foreach (var el in pol.Elements)
        {
            if (el.IsList)
            {
                var id = (scope, Slot.NormKey(el.Key));
                if (!_lists.TryGetValue(id, out var bucket))
                    _lists[id] = bucket = [];
                var hit = new Hit(pol.Uid, HitRole.List, el.Id);
                if (!bucket.Contains(hit))
                    bucket.Add(hit);
                continue;
            }
            Add(scope, el.Key, el.ValueName, new Hit(pol.Uid, HitRole.Element, el.Id));
            foreach (var item in el.Items)
            {
                foreach (var li in item.ValueList)
                    Add(scope, li.Key, li.Name, new Hit(pol.Uid, HitRole.EnumValueList, el.Id));
            }
            foreach (var li in el.TrueList.Concat(el.FalseList))
                Add(scope, li.Key, li.Name, new Hit(pol.Uid, HitRole.BoolList, el.Id));
        }
    }
}
