using System.Security.Cryptography;
using System.Text;

namespace GpoCatalogue.Core.Pol;

/// <summary>A registry value a policy file acts on. A value and its <c>**del.</c> marker share a slot.</summary>
public readonly record struct Slot(string Key, string Name)
{
    public static string NormKey(string key) => key.Trim('\\').ToLowerInvariant();

    public static Slot Of(PolEntry e) => e.Marker switch
    {
        PolMarker.None or PolMarker.Del => new Slot(NormKey(e.Key), e.Name.ToLowerInvariant()),
        _ => new Slot(NormKey(e.Key), e.ValueName.ToLowerInvariant()),
    };
}

/// <summary>
/// The content of one section of a group policy object, reduced to what the registry extension ends up
/// applying: entries are processed in file order, a later write to a value replaces an earlier one, and
/// <c>**delvals.</c> removes everything written to its key before it.
/// </summary>
public static class SectionState
{
    public static List<PolEntry> Canonicalize(IEnumerable<PolEntry> entries)
    {
        var order = new List<Slot>();
        var map = new Dictionary<Slot, PolEntry>();
        foreach (var e in entries)
        {
            var slot = Slot.Of(e);
            if (e.Marker is PolMarker.DelVals or PolMarker.DeleteValues)
            {
                foreach (var earlier in order.Where(s => s.Key == slot.Key).ToList())
                {
                    map.Remove(earlier);
                    order.Remove(earlier);
                }
            }
            if (!map.ContainsKey(slot))
                order.Add(slot);
            map[slot] = e;
        }
        return order.Select(s => map[s]).ToList();
    }

    /// <summary>
    /// Adds <paramref name="added"/> to canonical <paramref name="existing"/> entries. Every value is meant to
    /// coexist, so a key-clearing marker is moved ahead of the other entries of its key instead of wiping them.
    /// </summary>
    public static List<PolEntry> Merge(IEnumerable<PolEntry> existing, IEnumerable<PolEntry> added)
    {
        var order = new List<Slot>();
        var map = new Dictionary<Slot, PolEntry>();
        foreach (var e in existing.Concat(added))
        {
            var slot = Slot.Of(e);
            if (!map.ContainsKey(slot))
                order.Add(slot);
            map[slot] = e;
        }
        var result = new List<PolEntry>(order.Count);
        var hoisted = new HashSet<string>();
        foreach (var slot in order)
        {
            if (hoisted.Add(slot.Key))
            {
                result.AddRange(order.Where(s => s.Key == slot.Key && ClearsKey(map[s])).Select(s => map[s]));
            }
            if (!ClearsKey(map[slot]))
                result.Add(map[slot]);
        }
        return result;
    }

    public static Dictionary<Slot, PolEntry> ToMap(IEnumerable<PolEntry> entries) =>
        Canonicalize(entries).ToDictionary(Slot.Of);

    /// <summary>True when both entries leave the registry value in the same condition.</summary>
    public static bool SameEffect(PolEntry a, PolEntry b)
    {
        if (a.Marker != b.Marker || a.Soft != b.Soft)
            return false;
        if (a.IsMarker)
            return true; // the data of a marker is ignored
        return a.Type == b.Type && a.Raw.AsSpan().SequenceEqual(b.Raw);
    }

    public static bool Equivalent(IEnumerable<PolEntry> a, IEnumerable<PolEntry> b)
    {
        var left = ToMap(a);
        var right = ToMap(b);
        return left.Count == right.Count
            && left.All(kv => right.TryGetValue(kv.Key, out var other) && SameEffect(kv.Value, other));
    }

    /// <summary>Identifies a set of entries independent of order and letter case of names.</summary>
    public static string Hash(IEnumerable<PolEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var (slot, e) in ToMap(entries).OrderBy(kv => kv.Key.Key, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            sb.Append(slot.Key).Append('\n').Append(slot.Name).Append('\n').Append(e.Marker).Append('\n');
            if (!e.IsMarker)
                sb.Append(e.Type).Append('\n').Append(Convert.ToHexStringLower(e.Raw));
            sb.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static bool ClearsKey(PolEntry e) => e.Marker is PolMarker.DelVals or PolMarker.DeleteValues;
}
