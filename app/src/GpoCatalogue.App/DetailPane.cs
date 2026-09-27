using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.View;

namespace GpoCatalogue.App;

/// <summary>Everything about the selected policy: its state here, staged change, explanation and baselines.</summary>
internal sealed class DetailPane : TableLayoutPanel
{
    private static readonly Lazy<HashSet<string>> InstalledTemplates = new(() =>
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.admx").Select(p => Path.GetFileName(p).ToLowerInvariant()).ToHashSet()
            : [];
    });

    private readonly Workspace _ws;
    private readonly RichTextBox _left = RichText.Create("Policy explanation", "detailLeft");
    private readonly RichTextBox _right = RichText.Create("Policy details", "detailRight");

    public DetailPane(Workspace ws)
    {
        _ws = ws;
        ColumnCount = 2;
        RowCount = 1;
        Dock = DockStyle.Fill;
        Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(8), LogicalToDeviceUnits(6), 0);
        Tag = "head";
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _left.Margin = new Padding(0, 0, LogicalToDeviceUnits(16), 0);
        Controls.Add(_left, 0, 0);
        Controls.Add(_right, 1, 0);
    }

    public static bool TemplateInstalled(PolicyDef pol) => InstalledTemplates.Value.Contains(pol.Admx.ToLowerInvariant());

    public void Show(PolicyRow? row, bool canApply)
    {
        if (row is null)
        {
            new RichText(_left).Render(Legend);
            new RichText(_right).Render(r => r.Muted("Select a policy to see its details."));
            return;
        }
        new RichText(_left).Render(r => DescribePolicy(r, row, canApply));
        new RichText(_right).Render(r => DescribeSources(r, row));
    }

    private void DescribePolicy(RichText r, PolicyRow row, bool canApply)
    {
        var pol = row.Def;
        var t = Theme.Current;
        r.Title(pol.Name);

        r.Heading("On this computer");
        foreach (var s in row.States)
        {
            var label = pol.Scopes.Count > 1 ? s.Scope.Display() + ": " : "";
            r.Add(label, t.Body, t.Muted).Line(s.Summary, t.Bold);
            var live = s.Live.Status switch
            {
                LiveStatus.Applied => "The registry holds these values.",
                LiveStatus.NotApplied => "The registry does not hold these values yet, or something overrides them.",
                LiveStatus.RegistryOnly => $"Not set by local group policy, but the registry holds policy values"
                                           + (s.Live.Decoded is { } d ? $" that read as: {d.Summary}." : ".")
                                           + " A domain policy, an MDM profile or another program set them.",
                LiveStatus.OrdinarySetting => "Values exist in the registry, in a place where Windows also keeps its own settings.",
                _ => null,
            };
            if (live is not null)
                r.Muted(live, 12);
            foreach (var line in s.Configured?.Raw ?? [])
                r.Mono(line, 12);
            foreach (var line in s.Live.Status is LiveStatus.RegistryOnly or LiveStatus.OrdinarySetting or LiveStatus.NotApplied
                         ? s.Live.Lines : [])
                r.Mono("registry: " + line, 12);
        }
        if (LiveComparer.LeavesValuesBehind(pol))
            r.Line("Writes outside the registry keys reserved for policy: setting it to Not configured leaves the last value in place.",
                t.Body, t.MidFg);
        if (!TemplateInstalled(pol))
            r.Line($"{pol.Admx} is not installed in C:\\Windows\\PolicyDefinitions, so the Group Policy editor does not list this policy. It still applies.",
                t.Body, t.MidFg);

        foreach (var change in row.Pending)
        {
            r.Gap().Heading("Staged change" + (pol.Scopes.Count > 1 ? $" ({change.Scope.Display()})" : ""));
            r.Add("New: ", t.Body, t.Muted).Line(_ws.DescribePending(change), t.Bold, change.NeedsInput ? t.FlagFg : t.Fg);
            r.Muted("From: " + change.Source.Display());
            foreach (var m in change.Missing)
                r.Line(m, t.Body, t.FlagFg);
            if (change.TemplateDefaults)
                r.Line("Option values are the template's defaults; the model recommends the state only.", t.Body, t.MidFg);
            foreach (var d in ChangePlanner.Diff(change.Scope, _ws.States[pol.Uid, change.Scope].Entries, change.Target))
                r.Mono(d.Line, 12);
            if (!canApply)
                r.Muted("Applying needs administrator rights.");
        }

        r.Gap().Heading("Explanation");
        foreach (var paragraph in pol.Explanation.Split("\n\n"))
            r.Line(paragraph).Gap();
        if (pol.Options.Count > 0)
        {
            r.Heading("Options");
            foreach (var o in pol.Options)
            {
                var type = o.Type.Replace("_", " ");
                r.Line("\u2022 " + o.Label + " (" + type + (o.Choices is { } c ? ": " + string.Join(" / ", c) : "") + ")", indent: 6);
            }
        }
    }

    private static void DescribeSources(RichText r, PolicyRow row)
    {
        var pol = row.Def;
        r.Heading("Details");
        r.Field("Template", $"{pol.Admx} ({pol.InternalName})");
        r.Field("Class", pol.Class);
        r.Field("Category", pol.CategoryPath);
        r.Field("Supported on", pol.SupportedOn);
        r.Add("Registry:\n", Theme.Current.Body, Theme.Current.Muted);
        foreach (var v in pol.RegistryValues)
            r.Mono(v, 12);
        if (pol.Ml is { } m)
        {
            r.Field("Model", $"privacy {PolicyRow.Percent(m.Privacy)}%, security {PolicyRow.Percent(m.Security)}%, "
                             + $"UX loss {PolicyRow.Percent(m.UxDegrade)}%; privacy direction {m.PrivacyDir} "
                             + $"({PolicyRow.Percent(m.PrivacyDirConf)}%), security direction {m.SecurityDir} "
                             + $"({PolicyRow.Percent(m.SecurityDirConf)}%)");
        }
        else
        {
            r.Field("Model", "not scored");
        }
        if (pol.Flags.Count > 0)
            r.Field("Flags", string.Join(", ", pol.Flags));
        r.Field("Uid", pol.Uid);

        r.Gap().Heading("Restricted Traffic Limited Functionality Baseline");
        Baseline(r, pol.Rtlfb, withGpo: false);
        r.Gap().Heading("Security Baseline");
        Baseline(r, pol.Sb, withGpo: true);
    }

    private static void Baseline(RichText r, List<BaselineApplication> entries, bool withGpo)
    {
        if (entries.Count == 0)
        {
            r.Muted("not configured");
            return;
        }
        foreach (var a in entries)
        {
            var scope = a.Scope == "Machine" ? "Computer" : "User";
            r.Line(withGpo ? a.Gpo + " \u00b7 " + scope : scope, Theme.Current.Small, Theme.Current.Muted);
            r.Line(a.Summary, Theme.Current.Bold);
            foreach (var line in a.Raw)
                r.Mono(line);
        }
    }

    /// <summary>The website's "How to read the columns", plus the columns this program adds.</summary>
    public static void Legend(RichText r)
    {
        var t = Theme.Current;
        r.Heading("How to read the columns");
        r.Line("Every row is an Administrative Template policy from the Windows 11 24H2 policy definitions, plus the "
               + "templates shipped with the two Microsoft baselines. The three percentage columns are the probability "
               + "the model assigned to a yes answer; they are independent judgements, not a ranking.").Gap();
        void Item(string name, string text) => r.Add("\u2022 " + name + ": ", t.Bold).Line(text);
        Item("Privacy", "configuring the policy (Enabled or Disabled, with data-minimising options) would reduce "
                        + "collection, transmission or exposure of user and device data compared with leaving it Not Configured.");
        Item("Security", "configuring the policy would make the device harder to attack, compromise or misuse.");
        Item("UX loss", "configuring the policy in its most restrictive direction would directly degrade the experience "
                        + "of ordinary users (lost features, blocked actions, extra prompts, slower performance). Less "
                        + "telemetry, fewer ads or tips, and less personalisation do not count.");
        Item("Privacy: set to / Security: set to", "the state the model considers most privacy-protective or most "
                        + "secure. Shown as \u201c\u2014\u201d when the model chose neither or the matching probability is "
                        + "below 50%; the underlying choice is in the row details.");
        Item("RTLFB / Security Baseline", "what Microsoft's Restricted Traffic Limited Functionality Baseline and "
                        + "Windows 11 24H2 Security Baseline set the policy to, decoded from the baseline registry.pol "
                        + "files. Only Administrative Template settings are shown.");
        Item("Current state", "what the local group policy of this computer sets the policy to. \u201cnot applied\u201d: "
                        + "the registry does not hold those values (yet). \u201cregistry only\u201d: local policy leaves it "
                        + "Not configured, but policy values are in the registry, set by something else.");
        Item("Pending", "changes staged with Edit or Copy from, not written yet. Review and apply shows every "
                        + "registry value that will change before anything is written.");
        r.Gap().Add("Bands: ").Add(" below 35% ", t.Body, t.LowFg, back: t.LowBg).Add("  ")
            .Add(" 35 to 65% ", t.Body, t.MidFg, back: t.MidBg).Add("  ")
            .Add(" above 65% ", t.Body, t.HighFg, back: t.HighBg).Line("");
        r.Line("Click a column header to sort; use the row under the headers to filter. The view is kept between sessions.");
    }
}
