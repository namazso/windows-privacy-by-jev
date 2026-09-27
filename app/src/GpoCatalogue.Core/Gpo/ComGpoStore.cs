using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.Pol;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GpoCatalogue.Core.Gpo;

/// <summary>
/// The local group policy object of this computer. Reading goes straight to its files; writing goes through
/// the Group Policy editor's own interface, which maintains gpt.ini and tells Windows what changed.
/// </summary>
public sealed class ComGpoStore : IGpoStore
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int SharingViolation = unchecked((int)0x80070020);

    public string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "GroupPolicy");

    public bool IsTestFolder => false;

    public GpoSnapshot Read() => GpoSnapshot.Read(Folder);

    public IReadOnlyList<Obstacle> Preflight()
    {
        var obstacles = new List<Obstacle>();
        if (!ElevationHelper.IsElevated)
            obstacles.Add(new Obstacle("Changing policy needs administrator rights.", Blocking: true));
        if (EditorIsOpen())
        {
            obstacles.Add(new Obstacle(
                "The Local Group Policy Editor is open. It keeps its own copy of the policy and would overwrite "
                + "these changes with its next edit. Close it first.", Blocking: true));
        }
        if (LocalPolicyProcessingDisabled())
        {
            obstacles.Add(new Obstacle(
                "Processing of local group policy is turned off on this computer (DisableLGPOProcessing). "
                + "Changes are recorded but Windows will not apply them.", Blocking: false));
        }
        return obstacles;
    }

    public void Apply(IReadOnlyDictionary<Scope, SectionChange> changes)
    {
        if (changes.Count == 0)
            return;
        RunSta(() =>
        {
            IGroupPolicyObject gpo;
            try
            {
                gpo = (IGroupPolicyObject)new GroupPolicyObjectClass();
            }
            catch (COMException ex) when (ex.HResult == ClassNotRegistered)
            {
                throw new GpoException("Open",
                    "This edition of Windows has no Group Policy editor component (gpedit.dll).", ex);
            }
            try
            {
                Check("Open", Retry(() => gpo.OpenLocalMachineGPO(Native.GPO_OPEN_LOAD_REGISTRY)));
                // Computer first: the order in which the halves of the version number rise is then predictable.
                foreach (var (scope, change) in changes.OrderBy(c => c.Key))
                    ApplySection(gpo, scope, change);
            }
            finally
            {
                Marshal.FinalReleaseComObject(gpo);
            }
        });
    }

    public void Refresh(IEnumerable<Scope> scopes, bool wait)
    {
        foreach (var scope in scopes.Distinct())
        {
            // Save starts a background refresh of its own. Without RP_FORCE a synchronous refresh that overlaps
            // it finds nothing new and returns before the registry extension has run.
            if (!Native.RefreshPolicyEx(scope == Scope.Machine, wait ? Native.RP_SYNC | Native.RP_FORCE : 0))
                throw new GpoException("Refresh", new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }
    }

    private static void ApplySection(IGroupPolicyObject gpo, Scope scope, SectionChange change)
    {
        var section = scope == Scope.Machine ? Native.GPO_SECTION_MACHINE : Native.GPO_SECTION_USER;
        Check($"Open {scope.Display()} section", gpo.GetRegistryKey(section, out var handle));
        // The key must be closed before Save, which writes the hive out.
        using (var root = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true)))
            RewriteChangedKeys(root, change);

        var registry = GptIni.RegistryExtension;
        var tool = GptIni.ToolFor(scope);
        Check($"Save {scope.Display()} section",
            Retry(() => gpo.Save(scope == Scope.Machine, true, ref registry, ref tool)));
        foreach (var extension in change.Extensions.Distinct())
        {
            var id = extension;
            Check($"Save {scope.Display()} section", Retry(() => gpo.Save(scope == Scope.Machine, true, ref id, ref tool)));
        }
    }

    /// <summary>
    /// Replaces the values of every key whose entries differ. A key is rewritten as a whole so that its
    /// values come out in the intended order, which matters when one of them clears the key.
    /// </summary>
    internal static void RewriteChangedKeys(RegistryKey root, SectionChange change)
    {
        var current = ByKey(change.Current);
        var target = ByKey(change.Target);
        foreach (var key in current.Keys.Union(target.Keys).ToList())
        {
            var before = current.GetValueOrDefault(key) ?? [];
            var after = target.GetValueOrDefault(key) ?? [];
            if (before.Count == after.Count && before.Zip(after).All(p => Identical(p.First, p.Second)))
                continue;

            var path = (after.Count > 0 ? after : before)[0].Key.Trim('\\');
            using (var sub = after.Count > 0 ? root.CreateSubKey(path, writable: true) : root.OpenSubKey(path, writable: true))
            {
                if (sub is null)
                    continue;
                foreach (var name in sub.GetValueNames())
                    sub.DeleteValue(name, throwOnMissingValue: false);
                foreach (var e in after)
                {
                    var error = Native.RegSetValueExW(sub.Handle, e.ValueName, 0, e.Type, e.Raw, e.Raw.Length);
                    if (error != 0)
                        throw new GpoException("Write", $"{e.Key}!{e.ValueName}: {new Win32Exception(error).Message}");
                }
            }
            if (after.Count == 0)
                DeleteEmptyKeys(root, path);
        }
    }

    private static Dictionary<string, List<PolEntry>> ByKey(IEnumerable<PolEntry> entries) =>
        entries.GroupBy(e => Slot.NormKey(e.Key)).ToDictionary(g => g.Key, g => g.ToList());

    private static bool Identical(PolEntry a, PolEntry b) =>
        string.Equals(a.ValueName, b.ValueName, StringComparison.OrdinalIgnoreCase)
        && a.Type == b.Type && a.Raw.AsSpan().SequenceEqual(b.Raw);

    private static void DeleteEmptyKeys(RegistryKey root, string path)
    {
        while (path.Length > 0)
        {
            using (var key = root.OpenSubKey(path))
            {
                if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
                    return;
            }
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            var cut = path.LastIndexOf('\\');
            path = cut < 0 ? "" : path[..cut];
        }
    }

    private static bool EditorIsOpen()
    {
        try
        {
            return Process.GetProcessesByName("mmc").Any(p =>
            {
                using (p)
                    return p.MainWindowTitle.Contains("Group Policy", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool LocalPolicyProcessingDisabled()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"Software\Policies\Microsoft\Windows\System");
        return key?.GetValue("DisableLGPOProcessing") is int value && value != 0;
    }

    /// <summary>The policy files are briefly held open while Windows applies policy.</summary>
    private static int Retry(Func<int> call)
    {
        var hr = call();
        for (var attempt = 1; hr == SharingViolation && attempt <= 3; attempt++)
        {
            Thread.Sleep(250 * attempt);
            hr = call();
        }
        return hr;
    }

    private static void Check(string step, int hr)
    {
        if (hr >= 0)
            return;
        var message = hr switch
        {
            AccessDenied => "Access denied. Changing policy needs administrator rights.",
            SharingViolation => "The policy files are in use by another program.",
            _ => Marshal.GetExceptionForHR(hr)?.Message ?? $"error 0x{hr:X8}",
        };
        throw new GpoException(step, message, Marshal.GetExceptionForHR(hr));
    }

    /// <summary>The Group Policy object has no marshalling support, so every call stays on one STA thread.</summary>
    private static void RunSta(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Group Policy",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
