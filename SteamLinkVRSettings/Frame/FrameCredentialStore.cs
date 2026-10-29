using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLinkVRSettings.Frame;

/// <summary>
/// Keeps the Frame user password in Windows Credential Manager, encrypted for this Windows account,
/// so a kept H.264 session can re-authenticate without a prompt. It is never written to the app's
/// own preference or status files, and it is only stored when the user turns on keeping H.264.
/// </summary>
public sealed class FrameCredentialStore
{
    private const string DefaultTarget = "SteamFrameAdvancedOptions/FrameUserPassword";
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private readonly string _target;

    public FrameCredentialStore(string? target = null) => _target = string.IsNullOrWhiteSpace(target) ? DefaultTarget : target;

    public bool Exists => Load() is not null;

    public string? Load()
    {
        if (!CredRead(_target, TypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "The stored Frame password could not be read.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128 || password.Contains('\r') || password.Contains('\n'))
            throw new ArgumentException("Use the Frame user password.", nameof(password));
        var bytes = Encoding.UTF8.GetBytes(password);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = TypeGeneric, TargetName = _target, Comment = "Frame user password for kept H.264 sessions",
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = PersistLocalMachine, UserName = "steamos"
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The Frame password could not be stored.");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete()
    {
        if (!CredDelete(_target, TypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error, "The stored Frame password could not be removed.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
