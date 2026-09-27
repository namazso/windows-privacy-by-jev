using GpoCatalogue.Core.Live;

namespace GpoCatalogue.Core.View;

public enum ColumnType
{
    Text,
    Percent,
}

public enum FilterKind
{
    Text,
    Select,
    Range,
    Baseline,
    State,
    Pending,
}

/// <summary>A choice in a filter's list: what is stored and what is shown.</summary>
public sealed record FilterOption(string Value, string Label);

public sealed class ColumnDef
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Func<PolicyRow, object?> Get { get; init; }
    public ColumnType Type { get; init; } = ColumnType.Text;
    public FilterKind Filter { get; init; } = FilterKind.Text;
    public IReadOnlyList<FilterOption> Options { get; init; } = [];
    public bool Hidden { get; init; }
    public string? Title { get; init; }

    /// <summary>Width in pixels at 96 dpi.</summary>
    public int Width { get; init; } = 120;

    public string Tooltip => Title ?? "Sort by " + Label;
}

/// <summary>
/// The columns of the table. Those of the website keep its identifiers, labels, order and default
/// visibility (site/app.js COLUMNS); the state columns are additions of this program.
/// </summary>
public sealed class Columns
{
    public const string Name = "name", Category = "category", Class = "class", State = "state", Pending = "pending",
        Live = "live", Admx = "admx", Supported = "supported", Privacy = "privacy", Security = "security", Ux = "ux",
        PrivacyDir = "pdir", SecurityDir = "sdir", Rtlfb = "rtlfb", Sb = "sb", RegKey = "regkey",
        ValueName = "valuename";

    public const string ModeSet = "set", ModeUnset = "unset", ModeMatch = "match", ModeDiffer = "differ",
        GpoPrefix = "gpo:";

    public const string StateConfigured = "configured", StateNotConfigured = "unconfigured", StateEnabled = "Enabled",
        StateDisabled = "Disabled", StateRegistryOnly = "registry", StateNotApplied = "unapplied";

    public const string PendingAny = "any", PendingNone = "none", PendingIncomplete = "incomplete";

    private readonly Dictionary<string, ColumnDef> _byId;

    public Columns(IReadOnlyList<PolicyRow> rows)
    {
        var templates = rows.Select(r => r.Def.Admx).Distinct()
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        SbGpos = rows.SelectMany(r => r.Def.Sb.Select(a => a.Gpo)).Distinct()
            .OrderBy(g => g, StringComparer.Ordinal).ToList();
        FilterOption[] directions = [Same("Enabled"), Same("Disabled"), Same(PolicyRow.NoDirection)];

        All =
        [
            new ColumnDef { Id = Name, Label = "Policy", Get = r => r.Def.Name, Width = 300 },
            new ColumnDef { Id = Category, Label = "Category", Get = r => r.Def.CategoryPath, Width = 200 },
            new ColumnDef
            {
                Id = Class, Label = "Class", Get = r => r.Def.Class, Filter = FilterKind.Select,
                Options = [Same("Machine"), Same("User"), Same("Both")], Width = 80,
            },
            new ColumnDef
            {
                Id = State, Label = "Current state", Get = r => r.StateText, Filter = FilterKind.State,
                Title = "What local group policy sets the policy to on this computer",
                Options =
                [
                    new(StateConfigured, "configured"), new(StateNotConfigured, "not configured"),
                    Same(StateEnabled), Same(StateDisabled),
                    new(StateRegistryOnly, "registry only"), new(StateNotApplied, "not applied"),
                ],
                Width = 170,
            },
            new ColumnDef
            {
                Id = Pending, Label = "Pending", Get = r => r.PendingText, Filter = FilterKind.Pending,
                Title = "Changes staged but not applied yet",
                Options = [new(PendingAny, "staged"), new(PendingNone, "none"), new(PendingIncomplete, "needs input")],
                Width = 170,
            },
            new ColumnDef
            {
                Id = Live, Label = "Live registry", Get = r => r.LiveText, Filter = FilterKind.Select, Hidden = true,
                Title = "Whether the registry holds what local group policy prescribes",
                Options = Enum.GetValues<LiveStatus>().Where(s => s != LiveStatus.None)
                    .Select(s => Same(PolicyRow.LiveLabel(s))).ToList(),
                Width = 110,
            },
            new ColumnDef
            {
                Id = Admx, Label = "Template", Get = r => r.Def.Admx, Filter = FilterKind.Select,
                Options = templates.Select(Same).ToList(), Hidden = true, Width = 170,
            },
            new ColumnDef { Id = Supported, Label = "Supported on", Get = r => r.Def.SupportedOn, Hidden = true, Width = 190 },
            new ColumnDef
            {
                Id = Privacy, Label = "Privacy", Get = r => r.Privacy, Type = ColumnType.Percent,
                Filter = FilterKind.Range, Width = 96,
                Title = "Probability that configuring the policy improves privacy",
            },
            new ColumnDef
            {
                Id = Security, Label = "Security", Get = r => r.Security, Type = ColumnType.Percent,
                Filter = FilterKind.Range, Width = 96,
                Title = "Probability that configuring the policy improves security",
            },
            new ColumnDef
            {
                Id = Ux, Label = "UX loss", Get = r => r.Ux, Type = ColumnType.Percent,
                Filter = FilterKind.Range, Width = 96,
                Title = "Probability that the restrictive setting directly degrades user experience",
            },
            new ColumnDef
            {
                Id = PrivacyDir, Label = "Privacy: set to", Get = r => r.PrivacyDirection,
                Filter = FilterKind.Select, Options = directions, Width = 110,
            },
            new ColumnDef
            {
                Id = SecurityDir, Label = "Security: set to", Get = r => r.SecurityDirection,
                Filter = FilterKind.Select, Options = directions, Width = 110,
            },
            new ColumnDef
            {
                Id = Rtlfb, Label = "RTLFB", Get = r => r.RtlfbText, Filter = FilterKind.Baseline,
                Options = BaselineOptions([]), Width = 220,
            },
            new ColumnDef
            {
                Id = Sb, Label = "Security Baseline", Get = r => r.SbText, Filter = FilterKind.Baseline,
                Options = BaselineOptions(SbGpos), Width = 230,
            },
            new ColumnDef { Id = RegKey, Label = "Registry key", Get = r => r.Def.RegistryKey, Hidden = true, Width = 300 },
            new ColumnDef { Id = ValueName, Label = "Value name", Get = r => r.Def.ValueName ?? "", Hidden = true, Width = 170 },
        ];
        _byId = All.ToDictionary(c => c.Id);
        DefaultHidden = All.Where(c => c.Hidden).Select(c => c.Id).ToHashSet();
    }

    public IReadOnlyList<ColumnDef> All { get; }
    public IReadOnlyList<string> SbGpos { get; }
    public IReadOnlySet<string> DefaultHidden { get; }

    public ColumnDef this[string id] => _byId[id];

    public ColumnDef? Find(string id) => _byId.GetValueOrDefault(id);

    private static FilterOption Same(string value) => new(value, value);

    private static List<FilterOption> BaselineOptions(IEnumerable<string> gpos) =>
    [
        new(ModeSet, "configured"), new(ModeUnset, "not configured"), Same("Enabled"), Same("Disabled"),
        new(ModeMatch, "same as current state"), new(ModeDiffer, "differs from current state"),
        .. gpos.Select(g => new FilterOption(GpoPrefix + g, PolicyRow.StripMsft(g))),
    ];
}
