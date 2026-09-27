using System.Text.Json;
using GpoCatalogue.Core;

namespace GpoCatalogue.Tests;

/// <summary>Locates the repository the tests run in and the reference data the Python pipeline produced.</summary>
internal static class Repo
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "policies.json"))
                && File.Exists(Path.Combine(dir.FullName, "build.py")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (data/policies.json, build.py) not found.");
    });

    private static readonly Lazy<CatalogStore> Store = new(CatalogStore.LoadEmbedded);

    private static readonly Lazy<JsonDocument> Site = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "data", "policies.json"))));

    public static string Root => RootPath.Value;

    public static CatalogStore Catalog => Store.Value;

    /// <summary>data/policies.json, the dataset behind the website.</summary>
    public static JsonElement SiteData => Site.Value.RootElement;

    public static string Fixture(string name) =>
        Path.Combine(Root, "app", "tests", "GpoCatalogue.Tests", "Fixtures", name);

    public static IEnumerable<string> BaselinePolFiles() =>
        new[] { "Restricted Traffic Limited Functionality Baseline", "Security Baseline" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Root, folder), "registry.pol",
                SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
}
