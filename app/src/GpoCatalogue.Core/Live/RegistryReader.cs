using System.Buffers.Binary;
using System.Security;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using Microsoft.Win32;

namespace GpoCatalogue.Core.Live;

/// <summary>Read access to the registry values policies are applied to.</summary>
public interface IRegistryReader
{
    /// <summary>False when there is no registry to compare policy with.</summary>
    bool IsAvailable => true;

    /// <summary>The values of a key as entries, or an empty list when the key does not exist.</summary>
    IReadOnlyList<PolEntry> ReadKey(Scope scope, string key);
}

public static class RegistryReaderExtensions
{
    public static PolEntry? ReadValue(this IRegistryReader reader, Scope scope, string key, string name) =>
        reader.ReadKey(scope, key).FirstOrDefault(v => string.Equals(v.ValueName, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>No registry at all: used where only the policy files matter.</summary>
public sealed class NoRegistry : IRegistryReader
{
    public bool IsAvailable => false;

    public IReadOnlyList<PolEntry> ReadKey(Scope scope, string key) => [];
}

/// <summary>
/// The registry of this computer and of the user running the program. Keys are read once and remembered
/// until <see cref="Reset"/>.
/// </summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    private readonly Dictionary<(Scope, string), IReadOnlyList<PolEntry>> _cache = [];

    public void Reset() => _cache.Clear();

    public IReadOnlyList<PolEntry> ReadKey(Scope scope, string key)
    {
        var id = (scope, Slot.NormKey(key));
        if (!_cache.TryGetValue(id, out var values))
            _cache[id] = values = Read(scope, key.Trim('\\'));
        return values;
    }

    private static List<PolEntry> Read(Scope scope, string path)
    {
        var values = new List<PolEntry>();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(
                scope == Scope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey(path);
            if (key is null)
                return values;
            foreach (var name in key.GetValueNames())
            {
                if (ToEntry(key, path, name) is { } entry)
                    values.Add(entry);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A key that cannot be read is treated as holding nothing we can compare.
        }
        return values;
    }

    private static PolEntry? ToEntry(RegistryKey key, string path, string name)
    {
        try
        {
            var kind = key.GetValueKind(name);
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (kind, value) switch
            {
                (RegistryValueKind.DWord, int n) => PolEntry.Dword(path, name, unchecked((uint)n)),
                (RegistryValueKind.QWord, long n) => PolEntry.Qword(path, name, unchecked((ulong)n)),
                (RegistryValueKind.String, string s) => PolEntry.Text(path, name, s),
                (RegistryValueKind.ExpandString, string s) => PolEntry.Text(path, name, s, expandable: true),
                (RegistryValueKind.MultiString, string[] lines) => PolEntry.MultiText(path, name, lines),
                (RegistryValueKind.Binary, byte[] bytes) => new PolEntry(path, name, RegType.Binary, bytes),
                (_, byte[] bytes) => new PolEntry(path, name, kind < 0 ? RegType.None : (uint)kind, bytes),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>A registry held in memory, for tests.</summary>
public sealed class MemoryRegistry : IRegistryReader
{
    private readonly Dictionary<(Scope, string), List<PolEntry>> _keys = [];

    public MemoryRegistry Set(Scope scope, PolEntry value)
    {
        var id = (scope, Slot.NormKey(value.Key));
        if (!_keys.TryGetValue(id, out var values))
            _keys[id] = values = [];
        values.RemoveAll(v => string.Equals(v.ValueName, value.ValueName, StringComparison.OrdinalIgnoreCase));
        values.Add(value);
        return this;
    }

    public IReadOnlyList<PolEntry> ReadKey(Scope scope, string key) =>
        _keys.TryGetValue((scope, Slot.NormKey(key)), out var values) ? values : [];
}
