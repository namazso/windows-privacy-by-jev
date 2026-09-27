using GpoCatalogue.Core.Changes;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Core.View;

/// <summary>One line of the table: a policy with the values the columns show, filter on and sort by.</summary>
public sealed class PolicyRow
{
    public const string NoDirection = "—";
    private const string MsftPrefix = "MSFT ";

    public PolicyRow(PolicyDef def)
    {
        Def = def;
        Search = string.Join('\n', def.Name, def.CategoryPath, def.Admx, def.InternalName, def.ValueName ?? "",
            def.RegistryKey, def.Explanation).ToLowerInvariant();
        Privacy = Percent(def.Ml?.Privacy);
        Security = Percent(def.Ml?.Security);
        Ux = Percent(def.Ml?.UxDegrade);
        PrivacyDirection = Direction(def, privacy: true);
        SecurityDirection = Direction(def, privacy: false);
        RtlfbText = string.Join(" | ", def.Rtlfb.Select(a => (def.Class == "Both" ? a.Scope + ": " : "") + a.Summary));
        SbText = string.Join(" | ", def.Sb.Select(a => StripMsft(a.Gpo) + ": " + a.Summary));
    }

    public PolicyDef Def { get; }
    public string Uid => Def.Uid;

    /// <summary>Lowercased text the search box looks in.</summary>
    public string Search { get; }

    public int? Privacy { get; }
    public int? Security { get; }
    public int? Ux { get; }
    public string PrivacyDirection { get; }
    public string SecurityDirection { get; }
    public string RtlfbText { get; }
    public string SbText { get; }

    // What follows changes with the computer and with staged changes.

    public IReadOnlyList<ScopeState> States { get; private set; } = [];
    public IReadOnlyList<PendingChange> Pending { get; private set; } = [];
    public string StateText { get; private set; } = "";
    public string LiveText { get; private set; } = "";
    public string PendingText { get; private set; } = "";

    /// <summary>null when the baseline does not set the policy.</summary>
    public bool? MatchesRtlfb { get; private set; }

    public bool? MatchesSb { get; private set; }

    public bool IsConfigured => States.Any(s => s.IsConfigured);
    public bool HasPending => Pending.Count > 0;

    public static string StripMsft(string gpo) =>
        gpo.StartsWith(MsftPrefix, StringComparison.Ordinal) ? gpo[MsftPrefix.Length..] : gpo;

    /// <summary>JavaScript's Math.round(v * 100): halves go up.</summary>
    public static int? Percent(double? value) => value is { } v ? (int)Math.Floor(v * 100 + 0.5) : null;

    public void Update(IReadOnlyList<ScopeState> states, IReadOnlyList<PendingChange> pending,
        Func<PendingChange, string> describe)
    {
        States = states;
        Pending = pending;
        // A policy that can be set in both sections names the section only when there is something to tell apart.
        var both = states.Count > 1 && states.Any(s => s.IsConfigured || s.Live.Status == LiveStatus.RegistryOnly);
        StateText = both
            ? string.Join(" | ", states.Select(s => $"{s.Scope.Display()}: {s.Text}"))
            : states.Count > 0 ? states[0].Text : "";
        LiveText = string.Join(" | ", states.Where(s => s.Live.Status != LiveStatus.None)
            .Select(s => (states.Count > 1 ? s.Scope.Display() + ": " : "") + LiveLabel(s.Live.Status)));
        PendingText = string.Join(" | ", pending.Select(p =>
            (Def.Scopes.Count > 1 ? p.Scope.Display() + ": " : "") + describe(p)));
        MatchesRtlfb = Matches(Def.Rtlfb, states);
        MatchesSb = Matches(Def.Sb, states);
    }

    public static string LiveLabel(LiveStatus status) => status switch
    {
        LiveStatus.Applied => "applied",
        LiveStatus.NotApplied => "not applied",
        LiveStatus.RegistryOnly => "registry only",
        LiveStatus.OrdinarySetting => "value present",
        _ => "",
    };

    private static string Direction(PolicyDef def, bool privacy)
    {
        if (def.Ml is not { } ml)
            return "";
        var choice = privacy ? ml.PrivacyDir : ml.SecurityDir;
        // The direction question is independent of the yes/no question; only show a direction when the
        // matching probability also says the policy matters.
        if (choice == "neither" || (privacy ? ml.Privacy : ml.Security) < 0.5)
            return NoDirection;
        return choice switch
        {
            "enabled" => "Enabled",
            "disabled" => "Disabled",
            _ => choice,
        };
    }

    private static bool? Matches(List<BaselineApplication> applications, IReadOnlyList<ScopeState> states)
    {
        if (applications.Count == 0)
            return null;
        return applications.GroupBy(a => a.ScopeValue).All(group =>
        {
            var state = states.FirstOrDefault(s => s.Scope == group.Key);
            return state is not null && SectionState.Equivalent(state.Entries,
                group.SelectMany(a => a.Entries).Select(PolEntry.FromRaw));
        });
    }
}
