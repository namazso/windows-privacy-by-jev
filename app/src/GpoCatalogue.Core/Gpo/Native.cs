using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GpoCatalogue.Core.Gpo;

[ComImport, Guid("EA502722-A23D-11D1-A7D3-0000F87571E3")]
internal class GroupPolicyObjectClass
{
}

/// <summary>IGroupPolicyObject from GPEdit.h. The order of the methods is the layout of the interface.</summary>
[ComImport, Guid("EA502723-A23D-11D1-A7D3-0000F87571E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGroupPolicyObject
{
    [PreserveSig] int New([MarshalAs(UnmanagedType.LPWStr)] string domainName,
        [MarshalAs(UnmanagedType.LPWStr)] string? displayName, uint flags);

    [PreserveSig] int OpenDSGPO([MarshalAs(UnmanagedType.LPWStr)] string path, uint flags);

    [PreserveSig] int OpenLocalMachineGPO(uint flags);

    [PreserveSig] int OpenRemoteMachineGPO([MarshalAs(UnmanagedType.LPWStr)] string computerName, uint flags);

    [PreserveSig] int Save([MarshalAs(UnmanagedType.Bool)] bool machine, [MarshalAs(UnmanagedType.Bool)] bool add,
        ref Guid extension, ref Guid tool);

    [PreserveSig] int Delete();

    [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxLength);

    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxLength);

    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxLength);

    [PreserveSig] int GetDSPath(uint section, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxLength);

    [PreserveSig] int GetFileSysPath(uint section, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxLength);

    [PreserveSig] int GetRegistryKey(uint section, out IntPtr key);

    [PreserveSig] int GetOptions(out uint options);

    [PreserveSig] int SetOptions(uint options, uint mask);

    [PreserveSig] int GetGpoType(out int type);

    [PreserveSig] int GetMachineName([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxLength);

    [PreserveSig] int GetPropertySheetPages(out IntPtr pages, out uint count);
}

internal static partial class Native
{
    public const uint GPO_OPEN_LOAD_REGISTRY = 1;
    public const uint GPO_SECTION_USER = 1, GPO_SECTION_MACHINE = 2;
    public const uint RP_FORCE = 1, RP_SYNC = 2;

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RefreshPolicyEx([MarshalAs(UnmanagedType.Bool)] bool machine, uint options);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegSetValueExW(SafeRegistryHandle key, string? valueName, int reserved, uint type,
        byte[] data, int size);
}
