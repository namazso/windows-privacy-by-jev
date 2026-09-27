using System.Diagnostics;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Pol;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Core.Changes;

public sealed class ApplyOutcome
{
    public required BackupInfo Backup { get; init; }

    /// <summary>Changes whose policy did not end up with the entries that were planned.</summary>
    public required IReadOnlyList<PlannedChange> NotAsPlanned { get; init; }

    /// <summary>Changes written to the policy object that the registry did not reflect yet when checked.</summary>
    public IReadOnlyList<PlannedChange> NotYetApplied { get; init; } = [];

    /// <summary>Set when the policy object was written but Windows could not be asked to apply it.</summary>
    public string? RefreshError { get; init; }

    public bool Succeeded => NotAsPlanned.Count == 0;
}

/// <summary>Writes a plan to the policy object: checks, backup, write, verification, refresh.</summary>
public sealed class ChangeApplier(PolicyStateService states, BackupStore backups)
{
    /// <summary>How long to wait for Windows to bring the registry in line after a refresh.</summary>
    public TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>What stands in the way of applying right now.</summary>
    public IReadOnlyList<Obstacle> Preflight(ChangePlan plan)
    {
        var obstacles = states.Store.Preflight().ToList();
        var now = states.Store.Read();
        foreach (var (scope, change) in plan.Sections)
        {
            if (now[scope].Error is { } error)
                obstacles.Add(new Obstacle($"The {scope} policy file cannot be read: {error}", Blocking: true));
            else if (!SectionState.Equivalent(now.Entries(scope), change.Current))
            {
                obstacles.Add(new Obstacle(
                    "The policy was changed by another program since these differences were worked out. "
                    + "Refresh and review again.", Blocking: true));
            }
        }
        return obstacles;
    }

    public ApplyOutcome Apply(ChangePlan plan, string note, bool waitForRefresh)
    {
        var blocking = Preflight(plan).FirstOrDefault(o => o.Blocking);
        if (blocking is not null)
            throw new GpoException("Check", blocking.Message);

        // Policies whose values were in the registry because of this policy object, before the change.
        var wasApplied = plan.Changes
            .Where(c => states[c.Policy.Uid, c.Scope].Live.Status == LiveStatus.Applied)
            .Select(c => (c.Policy.Uid, c.Scope)).ToHashSet();

        var backup = backups.Create(states.Store.Read(), note);
        states.Store.Apply(plan.Sections);

        string? refreshError = null;
        try
        {
            states.Store.Refresh(plan.Sections.Keys, waitForRefresh);
        }
        catch (GpoException ex)
        {
            refreshError = ex.Message;
        }

        var applied = plan.Changes.Where(c => c.CanApply).ToList();
        List<PlannedChange> pending;
        var watch = Stopwatch.StartNew();
        while (true)
        {
            states.Refresh();
            pending = applied.Where(c => !Settled(c, wasApplied)).ToList();
            if (!waitForRefresh || refreshError is not null || pending.Count == 0 || watch.Elapsed >= SettleTimeout)
                break;
            Thread.Sleep(250);
        }

        var wrong = applied
            .Where(c => !SectionState.Equivalent(states[c.Policy.Uid, c.Scope].Entries, c.Change.Target))
            .ToList();
        return new ApplyOutcome
        {
            Backup = backup,
            NotAsPlanned = wrong,
            NotYetApplied = waitForRefresh ? pending : [],
            RefreshError = refreshError,
        };
    }

    /// <summary>True when the registry shows what the change asked for.</summary>
    private bool Settled(PlannedChange change, HashSet<(string, Model.Scope)> wasApplied)
    {
        if (!states.ComparesRegistry)
            return true;
        var state = states[change.Policy.Uid, change.Scope];
        if (change.Change.Target.Count > 0)
            return state.Live.Status == LiveStatus.Applied;
        // Withdrawn: Windows removes what it had applied from the keys reserved for policy.
        return !(wasApplied.Contains((change.Policy.Uid, change.Scope)) && state.Live.Status == LiveStatus.RegistryOnly);
    }
}
