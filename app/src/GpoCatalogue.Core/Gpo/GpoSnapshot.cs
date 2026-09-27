using System.Security.Cryptography;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Gpo;

/// <summary>The registry settings of a group policy object as they were on disk at one moment.</summary>
public sealed class GpoSnapshot
{
    private readonly Dictionary<Scope, SectionSnapshot> _sections;

    private GpoSnapshot(string folder, uint version, Dictionary<Scope, SectionSnapshot> sections)
    {
        Folder = folder;
        Version = version;
        _sections = sections;
    }

    public string Folder { get; }
    public uint Version { get; }

    public SectionSnapshot this[Scope scope] => _sections[scope];

    /// <summary>Entries reduced to what the registry extension ends up applying.</summary>
    public IReadOnlyList<PolEntry> Entries(Scope scope) => _sections[scope].Entries;

    public static string PolPath(string folder, Scope scope) =>
        Path.Combine(folder, scope == Scope.Machine ? "Machine" : "User", "Registry.pol");

    public static string IniPath(string folder) => Path.Combine(folder, "gpt.ini");

    public static GpoSnapshot Read(string folder)
    {
        var sections = new Dictionary<Scope, SectionSnapshot>();
        foreach (var scope in new[] { Scope.Machine, Scope.User })
            sections[scope] = SectionSnapshot.Read(PolPath(folder, scope));
        return new GpoSnapshot(folder, GptIni.Load(IniPath(folder)).Version, sections);
    }
}

public sealed class SectionSnapshot
{
    private SectionSnapshot(string path, IReadOnlyList<PolEntry> raw, string fileHash, string? error)
    {
        Path = path;
        FileEntries = raw;
        Entries = SectionState.Canonicalize(raw);
        FileHash = fileHash;
        Error = error;
    }

    public string Path { get; }

    /// <summary>Entries exactly as the file lists them.</summary>
    public IReadOnlyList<PolEntry> FileEntries { get; }

    public IReadOnlyList<PolEntry> Entries { get; }

    /// <summary>Empty when the section has no file.</summary>
    public string FileHash { get; }

    /// <summary>Why the file could not be read, if it could not.</summary>
    public string? Error { get; }

    public static SectionSnapshot Read(string path)
    {
        if (!File.Exists(path))
            return new SectionSnapshot(path, [], "", null);
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            // Windows leaves a zero-length file behind when a section has been emptied.
            return new SectionSnapshot(path, bytes.Length == 0 ? [] : PolFile.Parse(bytes, path), hash, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SectionSnapshot(path, [], "", ex.Message);
        }
    }
}
