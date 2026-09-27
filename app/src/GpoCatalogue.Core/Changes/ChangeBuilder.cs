using GpoCatalogue.Core.Encode;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Core.Changes;

/// <summary>Why a policy was left out when changes were staged for a selection.</summary>
public enum SkipReason
{
    /// <summary>The policy already is what was asked for.</summary>
    AlreadySet,

    /// <summary>The source says nothing about the policy.</summary>
    NoValue,
}

public sealed class StageResult
{
    public List<PendingChange> Staged { get; } = [];
    public List<(PolicyDef Policy, SkipReason Reason)> Skipped { get; } = [];

    /// <summary>Changes that were staged before and are no longer needed.</summary>
    public List<PendingChange> Withdrawn { get; } = [];

    public int Count(SkipReason reason) => Skipped.Count(s => s.Reason == reason);

    /// <summary>One line for the status bar.</summary>
    public string Describe(ChangeSource source)
    {
        var parts = new List<string> { $"{Staged.Count} staged from {source.Display()}" };
        if (Staged.Count(c => c.NeedsInput) is > 0 and var incomplete)
            parts.Add($"{incomplete} need input");
        if (Count(SkipReason.AlreadySet) is > 0 and var same)
            parts.Add($"{same} already set");
        if (Count(SkipReason.NoValue) is > 0 and var none)
        {
            parts.Add(source is ChangeSource.ModelPrivacy or ChangeSource.ModelSecurity
                ? $"{none} without a recommendation"
                : $"{none} not in the baseline");
        }
        return string.Join(" · ", parts);
    }
}

/// <summary>Builds pending changes from a dialog result, a baseline, or the model's recommendation.</summary>
public sealed class ChangeBuilder(PolicyStateService states)
{
    /// <summary>The scope a change applies to when its source does not name one.</summary>
    public static Scope DefaultScope(PolicyDef pol) => pol.Scopes[0];

    public StageResult FromConfig(PolicyDef pol, Scope scope, PolicyConfig config, ChangeSource source = ChangeSource.Manual,
        bool templateDefaults = false)
    {
        var result = new StageResult();
        Add(result, pol, scope, source, PolicyEncoder.Encode(pol, config), Missing(pol, config), templateDefaults,
            intended: config.State);
        return result;
    }

    /// <summary>What has to be filled in before <paramref name="config"/> can be written.</summary>
    public static List<string> Missing(PolicyDef pol, PolicyConfig config)
    {
        var missing = PolicyEncoder.Validate(pol, config);
        if (missing.Count == 0 && config.State != PolicyState.NotConfigured
            && PolicyEncoder.Encode(pol, config).Count == 0)
        {
            // Nothing would be written, and the policy would stay Not configured.
            missing.Add("This policy consists of its options only; give at least one of them a value.");
        }
        return missing;
    }

    public StageResult NotConfigured(IEnumerable<PolicyDef> policies)
    {
        var result = new StageResult();
        foreach (var pol in policies)
        {
            var before = result.Staged.Count + result.Withdrawn.Count;
            foreach (var scope in pol.Scopes)
                Add(result, pol, scope, ChangeSource.NotConfigured, [], [], false, reportSkip: false);
            if (result.Staged.Count + result.Withdrawn.Count == before)
                result.Skipped.Add((pol, SkipReason.AlreadySet));
        }
        return result;
    }

    /// <summary>Copies what a baseline sets, entry for entry, in the section the baseline sets it in.</summary>
    public StageResult FromBaseline(IEnumerable<PolicyDef> policies, ChangeSource source)
    {
        var result = new StageResult();
        foreach (var pol in policies)
        {
            var applications = source == ChangeSource.Rtlfb ? pol.Rtlfb : pol.Sb;
            if (applications.Count == 0)
            {
                result.Skipped.Add((pol, SkipReason.NoValue));
                continue;
            }
            // Several policy objects of a baseline may set the same policy; together they are one setting.
            foreach (var group in applications.GroupBy(a => a.ScopeValue))
            {
                var target = SectionState.Merge([], group.SelectMany(a => a.Entries).Select(PolEntry.FromRaw));
                Add(result, pol, group.Key, source, target, [], false);
            }
        }
        return result;
    }

    /// <summary>Sets the state the model considers best, with the template's default option values.</summary>
    public StageResult FromModel(IEnumerable<PolicyDef> policies, ChangeSource source)
    {
        var result = new StageResult();
        foreach (var pol in policies)
        {
            var state = Recommendation(pol, source == ChangeSource.ModelPrivacy);
            if (state == PolicyState.NotConfigured)
            {
                result.Skipped.Add((pol, SkipReason.NoValue));
                continue;
            }
            var config = state == PolicyState.Enabled
                ? PolicyConfig.Enabled(OptionReader.Defaults(pol))
                : PolicyConfig.Disabled;
            Add(result, pol, DefaultScope(pol), source, PolicyEncoder.Encode(pol, config), Missing(pol, config),
                templateDefaults: state == PolicyState.Enabled && pol.Elements.Count > 0, intended: state);
        }
        return result;
    }

    /// <summary>
    /// The direction the grid shows: the model's choice, but only when it also judged that configuring the
    /// policy matters.
    /// </summary>
    public static PolicyState Recommendation(PolicyDef pol, bool privacy)
    {
        if (pol.Ml is not { } ml)
            return PolicyState.NotConfigured;
        var (probability, choice) = privacy ? (ml.Privacy, ml.PrivacyDir) : (ml.Security, ml.SecurityDir);
        if (probability < 0.5)
            return PolicyState.NotConfigured;
        return choice switch
        {
            "enabled" => PolicyState.Enabled,
            "disabled" => PolicyState.Disabled,
            _ => PolicyState.NotConfigured,
        };
    }

    private void Add(StageResult result, PolicyDef pol, Scope scope, ChangeSource source,
        IReadOnlyList<PolEntry> target, IReadOnlyList<string> missing, bool templateDefaults, bool reportSkip = true,
        PolicyState? intended = null)
    {
        var current = states[pol.Uid, scope];
        if (SectionState.Equivalent(current.Entries, target) && missing.Count == 0)
        {
            if (reportSkip)
                result.Skipped.Add((pol, SkipReason.AlreadySet));
            result.Withdrawn.Add(new PendingChange { Uid = pol.Uid, Scope = scope, Source = source, Target = target });
            return;
        }
        result.Staged.Add(new PendingChange
        {
            Uid = pol.Uid,
            Scope = scope,
            Source = source,
            Target = target,
            Missing = missing,
            TemplateDefaults = templateDefaults,
            BaseHash = current.Hash,
            Intended = intended,
        });
    }
}
