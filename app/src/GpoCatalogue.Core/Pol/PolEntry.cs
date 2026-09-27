using System.Buffers.Binary;
using System.Text;
using GpoCatalogue.Core.Model;

namespace GpoCatalogue.Core.Pol;

/// <summary>Value-name prefixes with special meaning to the registry extension (MS-GPREG 2.2.1).</summary>
public enum PolMarker
{
    None,
    Del,
    DelVals,
    DeleteValues,
    DeleteKeys,
    SecureKey,
    SoftReboot,
}

public static class RegType
{
    public const uint None = 0, Sz = 1, ExpandSz = 2, Binary = 3, Dword = 4, DwordBigEndian = 5, Link = 6,
        MultiSz = 7, Qword = 11;

    public static string Name(uint type) => type switch
    {
        0 => "REG_NONE",
        1 => "REG_SZ",
        2 => "REG_EXPAND_SZ",
        3 => "REG_BINARY",
        4 => "REG_DWORD",
        5 => "REG_DWORD_BIG_ENDIAN",
        6 => "REG_LINK",
        7 => "REG_MULTI_SZ",
        11 => "REG_QWORD",
        _ => $"REG_{type}",
    };
}

/// <summary>One record of a registry.pol file. Mirrors pipeline/preg.py <c>Entry</c>.</summary>
public sealed class PolEntry
{
    public const string DelPrefix = "**del.", DelValsName = "**delvals.", SoftPrefix = "**soft.";

    /// <summary>What gpedit stores as the data of a marker value: one space.</summary>
    public static readonly byte[] MarkerData = EncodeString(" ");

    private static readonly (string Prefix, PolMarker Marker)[] Markers =
    [
        (DelPrefix, PolMarker.Del),
        (DelValsName, PolMarker.DelVals),
        ("**deletevalues", PolMarker.DeleteValues),
        ("**deletekeys", PolMarker.DeleteKeys),
        ("**securekey", PolMarker.SecureKey),
        ("**softreboot", PolMarker.SoftReboot),
    ];

    private object? _data;
    private bool _decoded;

    public PolEntry(string key, string valueName, uint type, byte[] raw)
    {
        Key = key;
        ValueName = valueName;
        Type = type;
        Raw = raw;
        Marker = Classify(valueName);
        Soft = Marker == PolMarker.None && valueName.StartsWith(SoftPrefix, StringComparison.OrdinalIgnoreCase);
        Name = Marker == PolMarker.Del ? valueName[DelPrefix.Length..]
            : Soft ? valueName[SoftPrefix.Length..]
            : valueName;
    }

    public string Key { get; }

    /// <summary>The literal value name, including any <c>**</c> prefix.</summary>
    public string ValueName { get; }

    public uint Type { get; }
    public byte[] Raw { get; }
    public PolMarker Marker { get; }

    /// <summary><c>**soft.</c>: the value is written only when it does not exist yet.</summary>
    public bool Soft { get; }

    /// <summary>The value the entry acts on (the target of a <c>**del.</c> or <c>**soft.</c> prefix).</summary>
    public string Name { get; }

    public bool IsMarker => Marker != PolMarker.None;
    public string TypeName => RegType.Name(Type);

    /// <summary>
    /// null, string, string[], or long (ulong beyond its range); other registry types decode to a lowercase
    /// hex string.
    /// </summary>
    public object? Data
    {
        get
        {
            if (!_decoded)
            {
                _data = Decode(Type, Raw);
                _decoded = true;
            }
            return _data;
        }
    }

    public string Render() => IsMarker ? ValueName : Data switch
    {
        string[] list => string.Join(" | ", list),
        string s => PyFormat.Repr(s),
        var other => PyFormat.Str(other),
    };

    /// <summary>The line shown for the entry in details and differences.</summary>
    public string RawLine(Scope scope) =>
        $"{scope.Hive()}\\{Key}!{ValueName}" + (IsMarker ? "" : $" = {TypeName} {Render()}");

    public override string ToString() => $"{Key}!{ValueName}" + (IsMarker ? "" : $" = {TypeName} {Render()}");

    public static PolEntry FromRaw(RawEntry e) => new(e.Key, e.Name, e.Type, Convert.FromHexString(e.Hex));

    public RawEntry ToRaw() => new() { Key = Key, Name = ValueName, Type = Type, Hex = Convert.ToHexStringLower(Raw) };

    public static PolEntry Delete(string key, string name) => new(key, DelPrefix + name, RegType.Sz, MarkerData);

    public static PolEntry DeleteValues(string key) => new(key, DelValsName, RegType.Sz, MarkerData);

    public static PolEntry Dword(string key, string name, uint value)
    {
        var raw = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
        return new PolEntry(key, name, RegType.Dword, raw);
    }

    public static PolEntry Qword(string key, string name, ulong value)
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, value);
        return new PolEntry(key, name, RegType.Qword, raw);
    }

    public static PolEntry Text(string key, string name, string value, bool expandable = false) =>
        new(key, name, expandable ? RegType.ExpandSz : RegType.Sz, EncodeString(value));

    public static PolEntry MultiText(string key, string name, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(line).Append('\0');
        sb.Append('\0');
        return new PolEntry(key, name, RegType.MultiSz, Encoding.Unicode.GetBytes(sb.ToString()));
    }

    public static byte[] EncodeString(string value) => Encoding.Unicode.GetBytes(value + "\0");

    private static PolMarker Classify(string valueName)
    {
        foreach (var (prefix, marker) in Markers)
        {
            if (valueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return marker;
        }
        return PolMarker.None;
    }

    private static object? Decode(uint type, byte[] raw)
    {
        if (raw.Length == 0)
            return null;
        switch (type)
        {
            case RegType.Sz or RegType.ExpandSz:
                return DecodeUtf16(raw).TrimEnd('\0');
            case RegType.MultiSz:
                var parts = DecodeUtf16(raw).Split('\0').ToList();
                while (parts.Count > 0 && parts[^1].Length == 0)
                    parts.RemoveAt(parts.Count - 1);
                return parts.ToArray();
            case RegType.Dword when raw.Length == 4:
                return (long)BinaryPrimitives.ReadUInt32LittleEndian(raw);
            case RegType.DwordBigEndian when raw.Length == 4:
                return (long)BinaryPrimitives.ReadUInt32BigEndian(raw);
            case RegType.Qword when raw.Length == 8:
                var qword = BinaryPrimitives.ReadUInt64LittleEndian(raw);
                return qword <= long.MaxValue ? (long)qword : qword;
            default:
                return Convert.ToHexStringLower(raw);
        }
    }

    private static string DecodeUtf16(byte[] raw)
    {
        // An odd trailing byte is dropped into a replacement character, as Python's errors="replace" does.
        var text = Encoding.Unicode.GetString(raw, 0, raw.Length & ~1);
        return (raw.Length & 1) == 1 ? text + '�' : text;
    }
}
