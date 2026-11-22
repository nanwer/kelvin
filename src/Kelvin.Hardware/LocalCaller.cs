using System.Runtime.InteropServices;

namespace Kelvin.Hardware;

/// <summary>
/// Works out which program is on the other end of a loopback connection and whether it may use
/// Kelvin's API. The service runs as LocalSystem, and a loopback port is open to every account and
/// every program on the PC, so "it came from this PC" says nothing about who is asking.
///
/// A caller is accepted when it is either
///   - running with administrator or system rights, or
///   - an ordinary program in the session of the person actively using the PC (at the screen or
///     over Remote Desktop), not a restricted low-integrity process.
/// That covers Kelvin's own app and the owner's scripts. It refuses another account's programs
/// (a second user left signed in, a service account) and sandboxed processes.
/// </summary>
public static class LocalCaller
{
    public sealed record Verdict(bool Allowed, string Reason, int ProcessId = 0);

    /// <param name="callerPort">The port the request came from.</param>
    /// <param name="servicePort">The port Kelvin listens on.</param>
    public static Verdict Check(int callerPort, int servicePort)
    {
        if (!OperatingSystem.IsWindows()) return new(true, "not Windows");
        try
        {
            var pid = OwnerOf(callerPort, servicePort);
            if (pid <= 0) return new(false, "Kelvin couldn't tell which program sent this request.");

            var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (process == IntPtr.Zero) return new(false, "Kelvin couldn't identify the program that sent this request.", pid);
            try
            {
                if (!OpenProcessToken(process, TokenQuery, out var token))
                    return new(false, "Kelvin couldn't identify the program that sent this request.", pid);
                try
                {
                    if (Number(token, TokenElevation) != 0) return new(true, "administrator", pid);

                    var session = Number(token, TokenSessionId);
                    if (!SessionActive(session))
                        return new(false, "Only the person using this PC, or an administrator, can control Kelvin.", pid);
                    if (Integrity(token) < MediumIntegrity)
                        return new(false, "Restricted programs can't control Kelvin.", pid);
                    return new(true, "active user", pid);
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }
        catch (Exception)
        {
            return new(false, "Kelvin couldn't identify the program that sent this request.");
        }
    }

    /// <summary>The process that owns the caller's end of the connection, from Windows' own table.</summary>
    private static int OwnerOf(int callerPort, int servicePort)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (result == ErrorInsufficientBuffer) continue; // the table grew; try again with the new size
                if (result != 0) return 0;
                var rows = Marshal.ReadInt32(buffer);
                var row = buffer + 4;
                for (var i = 0; i < rows; i++, row += RowSize)
                {
                    var localAddr = (uint)Marshal.ReadInt32(row, 4);
                    var localPort = HostPort(Marshal.ReadInt32(row, 8));
                    var remotePort = HostPort(Marshal.ReadInt32(row, 16));
                    if (localAddr == Loopback && localPort == callerPort && remotePort == servicePort)
                        return Marshal.ReadInt32(row, 20);
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return 0;
    }

    /// <summary>Ports in the table are in network byte order in the low 16 bits.</summary>
    private static int HostPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static uint Number(IntPtr token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, 4, out _)) throw new InvalidOperationException();
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The token's integrity level: 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system.</summary>
    private static uint Integrity(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
        if (size <= 0) throw new InvalidOperationException();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) throw new InvalidOperationException();
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL starts with a pointer to the SID
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool SessionActive(uint session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WtsConnectState, out var buffer, out var bytes) || buffer == IntPtr.Zero)
            return false;
        try { return bytes >= 4 && Marshal.ReadInt32(buffer) == WtsActive; }
        finally { WTSFreeMemory(buffer); }
    }

    private const int AfInet = 2, TcpTableOwnerPidConnections = 4, RowSize = 24, ErrorInsufficientBuffer = 122;
    private const uint Loopback = 0x0100007F; // 127.0.0.1 as the table stores it
    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008, MediumIntegrity = 0x2000;
    private const int TokenSessionId = 12, TokenElevation = 20, TokenIntegrityLevel = 25;
    private const int WtsConnectState = 8, WtsActive = 0;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
