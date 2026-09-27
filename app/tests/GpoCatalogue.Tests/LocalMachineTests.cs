using System.Diagnostics;
using System.Text.Json;
using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Gpo;
using GpoCatalogue.Core.Live;
using GpoCatalogue.Core.Model;
using GpoCatalogue.Core.State;

namespace GpoCatalogue.Tests;

/// <summary>
/// Reads the local group policy of the computer the tests run on, without changing anything, and compares
/// with what the Python pipeline makes of the same files.
/// </summary>
[Collection("Local machine")]
public class LocalMachineTests
{
    private static readonly ComGpoStore Store = new();

    [Theory]
    [InlineData(Scope.Machine)]
    [InlineData(Scope.User)]
    public void Local_policy_decodes_as_the_pipeline_decodes_it(Scope scope)
    {
        var path = GpoSnapshot.PolPath(Store.Folder, scope);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            Assert.Skip($"This computer has no {scope} policy file.");
        var oracle = RunPython(Path.Combine(Repo.Root, "tools", "decode_pol.py"), path, scope.ToString());
        if (oracle is null)
            Assert.Skip("Python is not available.");

        var states = new PolicyStateService(Repo.Catalog, Store, new NoRegistry());
        var expected = oracle.RootElement.GetProperty("policies").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value[0]);
        var configured = Repo.Catalog.Policies.Where(p => p.Scopes.Contains(scope))
            .Select(p => states[p.Uid, scope]).Where(s => s.IsConfigured).ToList();

        Assert.Equal(expected.Keys.Order(), configured.Select(s => s.Policy.Uid).Order());
        foreach (var state in configured)
        {
            var e = expected[state.Policy.Uid];
            Assert.Equal(e.GetProperty("state").GetString(), state.State.ToString());
            Assert.Equal(e.GetProperty("summary").GetString(), state.Summary);
            Assert.Equal(e.GetProperty("raw").EnumerateArray().Select(x => x.GetString()), state.Configured!.Raw);
            Assert.Equal(e.GetProperty("flags").EnumerateArray().Select(x => x.GetString()),
                state.Configured.Flags.Where(f => f != Flag.Soft));
        }
        Assert.Equal(oracle.RootElement.GetProperty("unmapped").GetArrayLength(), states.Unmapped(scope).Count);
    }

    [Fact]
    public void Reading_the_state_of_this_computer_is_quick_and_changes_nothing()
    {
        var before = Fingerprint();
        var watch = Stopwatch.StartNew();

        var states = new PolicyStateService(Repo.Catalog, Store, new WindowsRegistryReader());

        var elapsed = watch.Elapsed;
        var all = Repo.Catalog.Policies.SelectMany(states.For).ToList();
        Assert.Equal(3626 + 1069, all.Count);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed.TotalMilliseconds:F0} ms");
        // Whatever is configured here was either applied by Windows or is about to be.
        Assert.All(all.Where(s => s.IsConfigured), s =>
            Assert.Contains(s.Live.Status, new[] { LiveStatus.Applied, LiveStatus.NotApplied }));
        Assert.Equal(before, Fingerprint());
        TestContext.Current.SendDiagnosticMessage(
            $"{all.Count} states in {elapsed.TotalMilliseconds:F0} ms: " + string.Join(", ",
                all.GroupBy(s => (s.State, s.Live.Status)).OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key.State}/{g.Key.Status} {g.Count()}")));
    }

    private static string Fingerprint() => string.Join("|", new[]
        {
            GpoSnapshot.IniPath(Store.Folder),
            GpoSnapshot.PolPath(Store.Folder, Scope.Machine),
            GpoSnapshot.PolPath(Store.Folder, Scope.User),
        }
        .Select(p => File.Exists(p) ? $"{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : "-"));

    private static JsonDocument? RunPython(params string[] arguments)
    {
        var info = new ProcessStartInfo("python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in arguments)
            info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors.Result);
            return JsonDocument.Parse(output.Result);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
