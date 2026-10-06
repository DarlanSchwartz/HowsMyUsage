using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Usage;

// Read only the two Claude cookies needed for the quota request. No database copies or credential persistence.
static class ClaudeDesktopSession
{
    public static (string SessionKey, string? Organization) Read(string folder)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Local State")));
        var wrapped = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
        if (!wrapped.AsSpan().StartsWith("DPAPI"u8)) throw new InvalidOperationException("Unsupported Claude Desktop sign-in format.");
        var key = Unprotect(wrapped[5..]);
        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var path = Path.Combine(folder, "Network", "Cookies");
            if (sqlite3_open_v2("file:///" + path.Replace('\\', '/') + "?immutable=1", out database, 1 | 0x40, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Live Claude usage is unavailable while the session file is locked. Exit Claude Desktop briefly, then refresh Usage to connect.");
            sqlite3_busy_timeout(database, 1000);
            const string query = "SELECT host_key,name,value,encrypted_value FROM cookies WHERE host_key IN ('.claude.ai','claude.ai') AND name IN ('sessionKey','lastActiveOrg') ORDER BY last_access_utc DESC";
            if (sqlite3_prepare_v2(database, query, -1, out statement, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Claude Desktop session unavailable.");
            string? session = null, organization = null;
            int result;
            while ((result = sqlite3_step(statement)) == 100)
            {
                var host = Text(statement, 0);
                var name = Text(statement, 1);
                var value = Text(statement, 2);
                if (value.Length == 0)
                {
                    int length = sqlite3_column_bytes(statement, 3);
                    if (length == 0) continue;
                    var encrypted = new byte[length];
                    Marshal.Copy(sqlite3_column_blob(statement, 3), encrypted, 0, length);
                    value = DecryptCookie(encrypted, key, host);
                }
                if (name == "sessionKey" && session == null) session = value;
                if (name == "lastActiveOrg" && organization == null && Guid.TryParse(value, out var org)) organization = org.ToString();
            }
            if (result != 101) throw new InvalidOperationException("Claude session is busy. Please retry.");
            if (string.IsNullOrWhiteSpace(session)) throw new InvalidOperationException("Sign in to Claude Desktop.");
            return (session, organization);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
        }
    }

    static string DecryptCookie(byte[] value, byte[] key, string host)
    {
        byte[] clear;
        if (value.AsSpan().StartsWith("v10"u8) || value.AsSpan().StartsWith("v11"u8))
        {
            if (value.Length < 31) throw new InvalidOperationException("Invalid Claude session cookie.");
            clear = new byte[value.Length - 31];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(value.AsSpan(3, 12), value.AsSpan(15, value.Length - 31), value.AsSpan(value.Length - 16), clear);
        }
        else if (value.AsSpan().StartsWith("v20"u8))
            throw new InvalidOperationException("Windows protects this session per app; the widget cannot access it.");
        else clear = Unprotect(value);
        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
            int offset = clear.AsSpan().StartsWith(hash) ? 32 : 0;
            return Encoding.UTF8.GetString(clear, offset, clear.Length - offset);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    static byte[] Unprotect(byte[] encrypted)
    {
        var input = new Blob { Size = encrypted.Length, Data = Marshal.AllocHGlobal(encrypted.Length) };
        try
        {
            Marshal.Copy(encrypted, 0, input.Data, encrypted.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new InvalidOperationException("Cannot access the Claude sign-in for this Windows user.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }

    static string Text(IntPtr statement, int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr data);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr database);
}
