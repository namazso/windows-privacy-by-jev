using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Gpo;

/// <summary>The new content of one section, with what it replaces.</summary>
/// <param name="Current">Entries the change was computed from.</param>
/// <param name="Target">Entries the section must hold afterwards.</param>
/// <param name="Extensions">Extensions beyond the registry one that must process the section.</param>
public sealed record SectionChange(
    IReadOnlyList<PolEntry> Current,
    IReadOnlyList<PolEntry> Target,
    IReadOnlyList<Guid> Extensions);

/// <summary>Something that stops changes from being applied, or that the user should know before applying.</summary>
public sealed record Obstacle(string Message, bool Blocking);

/// <summary>A group policy object whose registry settings can be read and replaced.</summary>
public interface IGpoStore
{
    string Folder { get; }

    /// <summary>True for a plain folder used for testing instead of the computer's policy.</summary>
    bool IsTestFolder { get; }

    GpoSnapshot Read();

    IReadOnlyList<Obstacle> Preflight();

    void Apply(IReadOnlyDictionary<Scope, SectionChange> changes);

    /// <summary>Asks Windows to apply policy now.</summary>
    void Refresh(IEnumerable<Scope> scopes, bool wait);
}

public sealed class GpoException(string step, string message, Exception? inner = null)
    : Exception($"{step}: {message}", inner)
{
    public string Step { get; } = step;
}
