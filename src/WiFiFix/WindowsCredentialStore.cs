using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace XAOCEN.ReWiFi;

internal sealed class WindowsCredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string RefreshTokenTarget = "XAOCEN.ReWiFi/auth.xaocen.studio";
    // Keep the historical target value so existing installations retain the same
    // Account device identity after offline authorization was removed from v2.3.
    private const string DeviceRegistrationPrivateKeyTarget = "XAOCEN.ReWiFi/offline-device-key";

    public void WriteRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new ArgumentException("刷新令牌不能为空。", nameof(refreshToken));
        }

        WriteSecret(RefreshTokenTarget, refreshToken);
    }

    public string? ReadDeviceRegistrationPrivateKey() => ReadSecret(DeviceRegistrationPrivateKeyTarget);

    public void WriteDeviceRegistrationPrivateKey(string privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new ArgumentException("设备私钥不能为空。", nameof(privateKey));
        }

        WriteSecret(DeviceRegistrationPrivateKeyTarget, privateKey);
    }

    private static void WriteSecret(string targetName, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows Credential Manager。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public string? ReadRefreshToken() => ReadSecret(RefreshTokenTarget);

    private static string? ReadSecret(string targetName)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "无法读取 Windows Credential Manager。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void DeleteRefreshToken() => DeleteSecret(RefreshTokenTarget);

    private static void DeleteSecret(string targetName)
    {
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法删除 Windows Credential Manager 中的刷新令牌。");
            }
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
}
