using GpoCatalogue.Core;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>Command line of the program.</summary>
public sealed class StartOptions
{
    public const string Usage =
        "GpoCatalogue [--gpo-root <folder>] [--data <folder>] [--pending <file>] [--read-only]\n"
        + "             [--theme system|light|dark] [--select <uid>] [--export-csv <file>]\n\n"
        + "--gpo-root    work on a copy of a GroupPolicy folder instead of this computer's policy\n"
        + "--data        where the view, staged changes and (with --gpo-root) backups are kept\n"
        + "--pending     staged changes to load at start\n"
        + "--read-only   do not offer to apply changes, even as administrator\n"
        + "--select      policy to select at start, by uid (e.g. DataCollection:AllowTelemetry)\n"
        + "--export-csv  write the rows of the saved view as CSV and exit";

    public string? GpoRoot { get; private set; }
    public string? DataFolder { get; private set; }
    public string? PendingFile { get; private set; }
    public string? ExportCsv { get; private set; }
    public string? Select { get; private set; }
    public string Theme { get; private set; } = "system";
    public bool ReadOnly { get; private set; }

    /// <summary>Arguments to hand to a restarted instance.</summary>
    public List<string> Passthrough { get; } = [];

    public static StartOptions Parse(string[] args)
    {
        var o = new StartOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--gpo-root": o.GpoRoot = Path.GetFullPath(Value()); o.Passthrough.AddRange(["--gpo-root", o.GpoRoot]); break;
                case "--data": o.DataFolder = Path.GetFullPath(Value()); o.Passthrough.AddRange(["--data", o.DataFolder]); break;
                case "--pending": o.PendingFile = Path.GetFullPath(Value()); break;
                case "--export-csv": o.ExportCsv = Path.GetFullPath(Value()); break;
                case "--select": o.Select = Value(); break;
                case "--read-only": o.ReadOnly = true; break;
                case "--theme":
                    o.Theme = Value().ToLowerInvariant();
                    if (o.Theme is not ("system" or "light" or "dark"))
                        throw new ArgumentException("--theme takes system, light or dark.");
                    o.Passthrough.AddRange(["--theme", o.Theme]);
                    break;
                default: throw new ArgumentException($"Unknown argument {args[i]}.");
            }
        }
        return o;
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        StartOptions options;
        try
        {
            options = StartOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message + "\n\n" + StartOptions.Usage, "GPO Catalogue", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        Application.SetColorMode(options.Theme switch
        {
            "dark" => SystemColorMode.Dark,
            "light" => SystemColorMode.Classic,
            _ => SystemColorMode.System,
        });
        Theme.Use(Application.IsDarkModeEnabled);

        Workspace workspace;
        try
        {
            var catalog = CatalogStore.LoadEmbedded();
            IGpoStore store = options.GpoRoot is null ? new ComGpoStore() : new FileGpoStore(options.GpoRoot);
            IRegistryReader registry = options.GpoRoot is null ? new WindowsRegistryReader() : new NoRegistry();
            workspace = new Workspace(catalog, store, registry, new AppPaths(options.DataFolder));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show("The policy catalogue could not be loaded.\n\n" + ex.Message, "GPO Catalogue",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (options.ExportCsv is not null)
        {
            FilterEngine.WriteCsv(options.ExportCsv, workspace.Engine.ToCsv(workspace.Compute(), workspace.View));
            return 0;
        }

        Application.Run(new MainForm(workspace, options));
        return 0;
    }
}
