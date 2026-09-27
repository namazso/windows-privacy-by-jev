using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace GpoCatalogue.Core;

public static class ElevationHelper
{
    private const int CancelledByUser = 1223;

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => Elevated.Value;

    /// <summary>Starts this program again with administrator rights. False when the user declined the prompt.</summary>
    public static bool RestartElevated(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Program path unknown."),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledByUser)
        {
            return false;
        }
    }
}
