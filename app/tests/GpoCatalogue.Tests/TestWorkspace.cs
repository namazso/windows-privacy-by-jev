using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.Tests;

/// <summary>
/// A workspace over a policy object in a temporary folder. With <c>registry</c> it compares policy with a
/// registry held in memory, which starts out empty; without, it looks at the policy files alone.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace(IEnumerable<PolEntry>? machine = null, IEnumerable<PolEntry>? user = null,
        bool registry = false)
    {
        Folder = Path.Combine(Path.GetTempPath(), "GpoCatalogueTests", Guid.NewGuid().ToString("N"));
        GpoFolder = Path.Combine(Folder, "GroupPolicy");
        Directory.CreateDirectory(GpoFolder);
        if (machine is not null)
            PolFile.Write(GpoSnapshot.PolPath(GpoFolder, Scope.Machine), machine);
        if (user is not null)
            PolFile.Write(GpoSnapshot.PolPath(GpoFolder, Scope.User), user);
        Registry = new MemoryRegistry();
        Store = new FileGpoStore(GpoFolder);
        Workspace = new Workspace(Repo.Catalog, Store, registry ? Registry : new NoRegistry(),
            new AppPaths(Path.Combine(Folder, "data")));
    }

    public string Folder { get; }
    public string GpoFolder { get; }
    public MemoryRegistry Registry { get; }
    public FileGpoStore Store { get; }
    public Workspace Workspace { get; }

    public PolicyRow Row(string uid) => Workspace.Rows.Single(r => r.Uid == uid);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
