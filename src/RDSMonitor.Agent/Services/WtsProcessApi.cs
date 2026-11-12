using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RDSMonitor.Agent.Services;

/// <summary>
/// Enumeração de processos via Remote Desktop Services API
/// (<c>WTSEnumerateProcessesEx</c>, nível 1). Numa única chamada devolve,
/// para todos os processos: sessão, PID, nome, SID do dono, memória e tempo
/// de CPU. É ordens de grandeza mais rápida que WMI Win32_Process +
/// GetOwner (que leva ~0,5 s POR processo) — essencial em servidores RDS
/// com milhares de processos.
/// </summary>
internal static class WtsProcessApi
{
    public readonly record struct WtsProcess(
        int SessionId,
        int Pid,
        string Name,
        string? OwnerSid,
        ulong WorkingSetBytes,
        ulong CpuTicks);   // UserTime + KernelTime, em unidades de 100 ns

    public static List<WtsProcess> Enumerate()
    {
        int level = 1;
        if (!WTSEnumerateProcessesEx(IntPtr.Zero, ref level, WTS_ANY_SESSION, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateProcessesEx falhou");

        try
        {
            var result = new List<WtsProcess>(count);
            var size   = Marshal.SizeOf<WTS_PROCESS_INFO_EX>();

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_PROCESS_INFO_EX>(buffer + i * size);

                string? sid = null;
                if (info.pUserSid != IntPtr.Zero)
                {
                    try { sid = new SecurityIdentifier(info.pUserSid).Value; }
                    catch { /* SID inválido — fica sem dono (protegido) */ }
                }

                result.Add(new WtsProcess(
                    SessionId:       (int)info.SessionId,
                    Pid:             (int)info.ProcessId,
                    Name:            Marshal.PtrToStringUni(info.pProcessName) ?? "?",
                    OwnerSid:        sid,
                    WorkingSetBytes: info.WorkingSetSize,
                    CpuTicks:        (ulong)Math.Max(0, info.UserTime) + (ulong)Math.Max(0, info.KernelTime)));
            }

            return result;
        }
        finally
        {
            WTSFreeMemoryEx(WTS_TYPE_PROCESS_INFO_LEVEL1, buffer, count);
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private const int WTS_ANY_SESSION                = -2;
    private const int WTS_TYPE_PROCESS_INFO_LEVEL1   = 1;   // WTS_TYPE_CLASS.WTSTypeProcessInfoLevel1

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_PROCESS_INFO_EX
    {
        public uint   SessionId;
        public uint   ProcessId;
        public IntPtr pProcessName;
        public IntPtr pUserSid;
        public uint   NumberOfThreads;
        public uint   HandleCount;
        public uint   PagefileUsage;
        public uint   PeakPagefileUsage;
        public uint   WorkingSetSize;
        public uint   PeakWorkingSetSize;
        public long   UserTime;
        public long   KernelTime;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSEnumerateProcessesExW")]
    private static extern bool WTSEnumerateProcessesEx(
        IntPtr hServer, ref int pLevel, int sessionId, out IntPtr ppProcessInfo, out int pCount);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WTSFreeMemoryExW")]
    private static extern bool WTSFreeMemoryEx(int wtsTypeClass, IntPtr pMemory, int numberOfEntries);
}
