using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Core.Gpo;

/// <summary>
/// A group policy object kept as plain files in a folder. Used to try changes without touching the policy
/// of the computer: nothing here is ever applied to the registry.
/// </summary>
public sealed class FileGpoStore(string folder) : IGpoStore
{
    public string Folder { get; } = Path.GetFullPath(folder);

    public bool IsTestFolder => true;

    public GpoSnapshot Read() => GpoSnapshot.Read(Folder);

    public IReadOnlyList<Obstacle> Preflight() => [];

    public void Apply(IReadOnlyDictionary<Scope, SectionChange> changes)
    {
        var iniPath = GpoSnapshot.IniPath(Folder);
        var ini = GptIni.Load(iniPath);
        foreach (var (scope, change) in changes)
        {
            PolFile.Write(GpoSnapshot.PolPath(Folder, scope), change.Target);
            ini.EnsureExtension(scope, GptIni.RegistryExtension, GptIni.ToolFor(scope));
            foreach (var extension in change.Extensions)
                ini.EnsureExtension(scope, extension, GptIni.ToolFor(scope));
            ini.Bump(scope);
        }
        ini.Save(iniPath);
    }

    public void Refresh(IEnumerable<Scope> scopes, bool wait)
    {
    }
}
