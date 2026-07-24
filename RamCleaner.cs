// RamCleaner — libera RAM: vacía working sets y purga la standby list.
// Compilar: csc /nologo /optimize /target:winexe /out:RamCleaner.exe RamCleaner.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

static class RamCleaner
{
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("ntdll.dll")]
    static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TokPriv1Luid { public int Count; public long Luid; public int Attr; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokPriv1Luid newState, int len, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

    const int SystemMemoryListInformation = 80;      // 0x50
    const int MemoryPurgeStandbyList = 4;
    const int MemoryPurgeLowPriorityStandbyList = 5;

    static ulong AvailMB()
    {
        var m = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(m);
        return m.ullAvailPhys / (1024 * 1024);
    }

    static bool EnablePrivilege(string name)
    {
        IntPtr tok;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out tok)) return false; // ADJUST_PRIVILEGES | QUERY
        TokPriv1Luid tp;
        tp.Count = 1;
        tp.Attr = 2; // SE_PRIVILEGE_ENABLED
        if (!LookupPrivilegeValue(null, name, out tp.Luid)) return false;
        return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
            && Marshal.GetLastWin32Error() == 0;
    }

    static void Main()
    {
        ulong before = AvailMB();

        // No tocar el proceso en primer plano (un juego se congelaria unos segundos al re-paginar)
        uint fgPid = 0;
        GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);

        int ok = 0, skipped = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if ((uint)p.Id == fgPid) { skipped++; continue; }
                if (EmptyWorkingSet(p.Handle)) ok++; else skipped++;
            }
            catch { skipped++; }
            finally { p.Dispose(); }
        }

        string standby;
        if (EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            int cmd = MemoryPurgeStandbyList;
            int st = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, 4);
            cmd = MemoryPurgeLowPriorityStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmd, 4);
            standby = st == 0 ? "OK" : "error 0x" + st.ToString("X8");
        }
        else standby = "sin privilegios de admin";

        ulong after = AvailMB();
        string line = string.Format("{0:yyyy-MM-dd HH:mm:ss}  libre {1} MB -> {2} MB ({3}{4} MB) | procesos {5} ok / {6} omitidos | standby: {7}",
            DateTime.Now, before, after, after >= before ? "+" : "", (long)after - (long)before, ok, skipped, standby);

        try
        {
            string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RamCleaner.log");
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(log, line + Environment.NewLine);
        }
        catch { }
    }
}
