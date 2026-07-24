// RamCleanerTray — limpiador de RAM residente estilo Mem Reduct.
// Icono en bandeja con % de uso, limpieza manual/automática (umbral e intervalo),
// opciones de qué limpiar y arranque con Windows (tarea programada elevada).
// Compilar: csc /nologo /optimize /target:winexe /win32manifest:app.manifest /out:RamCleanerTray.exe RamCleanerTray.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ChangeWindowMessageFilter(uint message, uint flag);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("ntdll.dll")]
    public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TokPriv1Luid { public int Count; public long Luid; public int Attr; }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokPriv1Luid newState, int len, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);
}

class Config
{
    public int Threshold = 90;      // % de uso que dispara auto-limpieza (0 = desactivada)
    public int IntervalMin = 30;    // minutos entre limpiezas (0 = desactivado)
    public bool CleanWS = true;
    public bool CleanStandby = true;
    public bool CleanStandbyLow = true;
    public bool CleanModified = false;
    public bool CleanSysCache = false;
    public int Notif = 1; // 0=siempre, 1=no durante juegos, 2=solo manuales, 3=nunca

    static string CfgPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"); } }

    public static Config Load()
    {
        var c = new Config();
        try
        {
            if (File.Exists(CfgPath))
            {
                foreach (var line in File.ReadAllLines(CfgPath))
                {
                    var kv = line.Split(new char[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    int n;
                    if (k == "umbral" && int.TryParse(v, out n)) c.Threshold = n;
                    else if (k == "intervalo" && int.TryParse(v, out n)) c.IntervalMin = n;
                    else if (k == "ws") c.CleanWS = v == "1";
                    else if (k == "standby") c.CleanStandby = v == "1";
                    else if (k == "standby_low") c.CleanStandbyLow = v == "1";
                    else if (k == "modified") c.CleanModified = v == "1";
                    else if (k == "syscache") c.CleanSysCache = v == "1";
                    else if (k == "notif" && int.TryParse(v, out n) && n >= 0 && n <= 3) c.Notif = n;
                }
            }
        }
        catch { }
        return c;
    }

    public void Save()
    {
        try
        {
            File.WriteAllLines(CfgPath, new string[] {
                "umbral=" + Threshold,
                "intervalo=" + IntervalMin,
                "ws=" + (CleanWS ? 1 : 0),
                "standby=" + (CleanStandby ? 1 : 0),
                "standby_low=" + (CleanStandbyLow ? 1 : 0),
                "modified=" + (CleanModified ? 1 : 0),
                "syscache=" + (CleanSysCache ? 1 : 0),
                "notif=" + Notif
            });
        }
        catch { }
    }
}

class CleanResult
{
    public long FreedMB;
    public int PctBefore, PctAfter;
}

class TrayApp : ApplicationContext
{
    const string TaskName = "RamCleanerTray";

    NotifyIcon notify;
    System.Windows.Forms.Timer timer;
    Config cfg;
    SynchronizationContext ui;
    ToolStripMenuItem miUsage;
    IntPtr prevHIcon = IntPtr.Zero;
    Icon prevIcon;
    volatile bool cleaning;
    DateTime lastClean = DateTime.UtcNow;
    Font fontDigits;
    StringFormat sfCenter;
    int iconSize;

    public TrayApp()
    {
        cfg = Config.Load();
        ui = SynchronizationContext.Current;

        EnablePrivilege("SeProfileSingleProcessPrivilege"); // purga standby/modified
        EnablePrivilege("SeIncreaseQuotaPrivilege");        // caché del sistema
        EnablePrivilege("SeDebugPrivilege");                // abrir más procesos

        iconSize = SystemInformation.SmallIconSize.Width;
        if (iconSize < 16) iconSize = 16;
        sfCenter = new StringFormat();
        sfCenter.Alignment = StringAlignment.Center;
        sfCenter.LineAlignment = StringAlignment.Center;
        fontDigits = new Font("Segoe UI", iconSize * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);

        notify = new NotifyIcon();
        notify.ContextMenuStrip = BuildMenu();
        notify.BalloonTipIcon = ToolTipIcon.Info;
        notify.DoubleClick += delegate { StartClean("manual"); };
        notify.Visible = true;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1200;
        timer.Tick += Tick;
        timer.Start();
        Tick(null, null);
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        miUsage = new ToolStripMenuItem("RAM: ...");
        miUsage.Enabled = false;
        menu.Items.Add(miUsage);
        menu.Items.Add(new ToolStripSeparator());

        var miClean = new ToolStripMenuItem("Limpiar memoria ahora", null, delegate { StartClean("manual"); });
        miClean.Font = new Font(miClean.Font, FontStyle.Bold);
        menu.Items.Add(miClean);
        menu.Items.Add(new ToolStripSeparator());

        var miWhat = new ToolStripMenuItem("Qué limpiar");
        miWhat.DropDownItems.Add(CheckItem("Working sets de procesos", cfg.CleanWS, delegate(bool v) { cfg.CleanWS = v; }));
        miWhat.DropDownItems.Add(CheckItem("Standby list", cfg.CleanStandby, delegate(bool v) { cfg.CleanStandby = v; }));
        miWhat.DropDownItems.Add(CheckItem("Standby list (baja prioridad)", cfg.CleanStandbyLow, delegate(bool v) { cfg.CleanStandbyLow = v; }));
        miWhat.DropDownItems.Add(CheckItem("Modified page list (escribe al pagefile)", cfg.CleanModified, delegate(bool v) { cfg.CleanModified = v; }));
        miWhat.DropDownItems.Add(CheckItem("Caché de archivos del sistema", cfg.CleanSysCache, delegate(bool v) { cfg.CleanSysCache = v; }));
        menu.Items.Add(miWhat);

        var miAuto = new ToolStripMenuItem("Limpieza automática");
        miAuto.DropDownItems.Add(RadioGroup("Al superar % de uso",
            new int[] { 0, 80, 85, 90, 95 },
            new string[] { "Desactivada", "80%", "85%", "90%", "95%" },
            cfg.Threshold, delegate(int v) { cfg.Threshold = v; }));
        miAuto.DropDownItems.Add(RadioGroup("Cada cierto tiempo",
            new int[] { 0, 15, 30, 60 },
            new string[] { "Desactivado", "Cada 15 min", "Cada 30 min", "Cada 60 min" },
            cfg.IntervalMin, delegate(int v) { cfg.IntervalMin = v; }));
        menu.Items.Add(miAuto);

        menu.Items.Add(RadioGroup("Notificaciones",
            new int[] { 0, 1, 2, 3 },
            new string[] { "Siempre", "No durante juegos", "Solo limpiezas manuales", "Nunca" },
            cfg.Notif, delegate(int v) { cfg.Notif = v; }));

        var miStart = new ToolStripMenuItem("Iniciar con Windows");
        miStart.Checked = TaskExists();
        miStart.Click += delegate
        {
            if (miStart.Checked)
            {
                RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
                miStart.Checked = TaskExists();
            }
            else
            {
                CreateStartupTask();
                miStart.Checked = TaskExists();
            }
        };
        menu.Items.Add(miStart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Abrir log", null, delegate
        {
            try
            {
                string log = LogPath();
                if (!File.Exists(log)) File.WriteAllText(log, "");
                Process.Start(log);
            }
            catch { }
        }));
        menu.Items.Add(new ToolStripMenuItem("Salir", null, delegate
        {
            timer.Stop();
            notify.Visible = false;
            notify.Dispose();
            if (prevHIcon != IntPtr.Zero) Native.DestroyIcon(prevHIcon);
            ExitThread();
        }));
        return menu;
    }

    ToolStripMenuItem CheckItem(string text, bool val, Action<bool> set)
    {
        var mi = new ToolStripMenuItem(text);
        mi.Checked = val;
        mi.CheckOnClick = true;
        mi.CheckedChanged += delegate { set(mi.Checked); cfg.Save(); };
        return mi;
    }

    ToolStripMenuItem RadioGroup(string title, int[] values, string[] labels, int current, Action<int> set)
    {
        var parent = new ToolStripMenuItem(title);
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            var mi = new ToolStripMenuItem(labels[i]);
            mi.Checked = v == current;
            mi.Click += delegate
            {
                foreach (ToolStripMenuItem sib in parent.DropDownItems) sib.Checked = false;
                mi.Checked = true;
                set(v);
                cfg.Save();
            };
            parent.DropDownItems.Add(mi);
        }
        return parent;
    }

    void Tick(object s, EventArgs e)
    {
        var m = new Native.MEMORYSTATUSEX();
        if (!Native.GlobalMemoryStatusEx(m)) return;
        int pct = (int)m.dwMemoryLoad;

        UpdateIcon(pct);
        string txt = string.Format("RAM {0}% — {1:F1}/{2:F1} GB", pct,
            (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0, m.ullTotalPhys / 1073741824.0);
        miUsage.Text = txt;
        if (txt.Length > 63) txt = txt.Substring(0, 63);
        notify.Text = txt;

        if (cleaning) return;
        if (cfg.Threshold > 0 && pct >= cfg.Threshold && (DateTime.UtcNow - lastClean).TotalMinutes >= 5)
            StartClean(string.Format("auto {0}%", pct));
        else if (cfg.IntervalMin > 0 && (DateTime.UtcNow - lastClean).TotalMinutes >= cfg.IntervalMin)
            StartClean("intervalo");
    }

    void UpdateIcon(int pct)
    {
        if (pct > 99) pct = 99;
        bool danger = cfg.Threshold > 0 && pct >= cfg.Threshold;
        using (var bmp = new Bitmap(iconSize, iconSize))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var bg = new SolidBrush(danger ? Color.FromArgb(196, 43, 28) : Color.FromArgb(43, 43, 43)))
                    g.FillRectangle(bg, 0, 0, iconSize, iconSize);
                g.DrawString(pct.ToString(), fontDigits, Brushes.White,
                    new RectangleF(0, 1f, iconSize, iconSize), sfCenter);
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = Icon.FromHandle(h);
            notify.Icon = ic;
            if (prevHIcon != IntPtr.Zero) Native.DestroyIcon(prevHIcon);
            if (prevIcon != null) prevIcon.Dispose();
            prevHIcon = h;
            prevIcon = ic;
        }
    }

    void StartClean(string reason)
    {
        if (cleaning) return;
        cleaning = true;
        lastClean = DateTime.UtcNow;
        bool manual = reason == "manual";
        ThreadPool.QueueUserWorkItem(delegate(object state)
        {
            CleanResult r = DoClean(reason);
            ui.Post(delegate(object o)
            {
                bool show =
                    cfg.Notif == 0 ||
                    (cfg.Notif == 1 && (manual || !NotifBlocked())) ||
                    (cfg.Notif == 2 && manual);
                if (show)
                {
                    notify.BalloonTipTitle = "RamCleaner";
                    notify.BalloonTipText = r.FreedMB > 0
                        ? string.Format("Liberados {0} MB (uso {1}% → {2}%)", r.FreedMB, r.PctBefore, r.PctAfter)
                        : string.Format("Sin cambio apreciable (uso {0}% → {1}%)", r.PctBefore, r.PctAfter);
                    notify.ShowBalloonTip(3000);
                }
                cleaning = false;
            }, null);
        });
    }

    CleanResult DoClean(string reason)
    {
        var res = new CleanResult();
        var m = new Native.MEMORYSTATUSEX();
        Native.GlobalMemoryStatusEx(m);
        res.PctBefore = (int)m.dwMemoryLoad;
        ulong beforeMB = m.ullAvailPhys / 1048576UL;
        var parts = new List<string>();

        if (cfg.CleanWS)
        {
            // No tocar el proceso en primer plano (un juego se congelaría al re-paginar)
            uint fgPid = 0;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
            int ok = 0, skip = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if ((uint)p.Id == fgPid) { skip++; continue; }
                    if (Native.EmptyWorkingSet(p.Handle)) ok++; else skip++;
                }
                catch { skip++; }
                finally { p.Dispose(); }
            }
            parts.Add(string.Format("ws {0}/{1}", ok, skip));
        }
        if (cfg.CleanModified) parts.Add("modified " + Purge(3));       // MemoryFlushModifiedList
        if (cfg.CleanStandbyLow) parts.Add("standby-low " + Purge(5));  // MemoryPurgeLowPriorityStandbyList
        if (cfg.CleanStandby) parts.Add("standby " + Purge(4));         // MemoryPurgeStandbyList
        if (cfg.CleanSysCache)
            parts.Add("syscache " + (Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0) ? "OK" : "error"));

        Native.GlobalMemoryStatusEx(m);
        res.PctAfter = (int)m.dwMemoryLoad;
        ulong afterMB = m.ullAvailPhys / 1048576UL;
        res.FreedMB = (long)afterMB - (long)beforeMB;

        Log(string.Format("[{0}] libre {1} -> {2} MB ({3}{4} MB) | uso {5}% -> {6}% | {7}",
            reason, beforeMB, afterMB, res.FreedMB >= 0 ? "+" : "", res.FreedMB,
            res.PctBefore, res.PctAfter, string.Join(" | ", parts.ToArray())));
        return res;
    }

    static string Purge(int cmd)
    {
        int c = cmd;
        int st = Native.NtSetSystemInformation(80, ref c, 4); // SystemMemoryListInformation
        return st == 0 ? "OK" : "error 0x" + st.ToString("X8");
    }

    static string LogPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RamCleaner.log");
    }

    static void Log(string msg)
    {
        try
        {
            string log = LogPath();
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(log, string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}{2}", DateTime.Now, msg, Environment.NewLine));
        }
        catch { }
    }

    static bool EnablePrivilege(string name)
    {
        IntPtr tok;
        if (!Native.OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out tok)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            Native.TokPriv1Luid tp;
            tp.Count = 1;
            tp.Attr = 2; // SE_PRIVILEGE_ENABLED
            if (!Native.LookupPrivilegeValue(null, name, out tp.Luid)) return false;
            return Native.AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally { Native.CloseHandle(tok); }
    }

    // Registra la tarea de arranque vía XML: schtasks /Create a secas hereda un
    // ExecutionTimeLimit de 72 h (mataría el tray al tercer día) y almacena la ruta
    // sin comillas (rompe autostart si hay espacios). El XML controla ambas cosas.
    static bool CreateStartupTask()
    {
        try
        {
            string user = System.Security.SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
            string cmd = System.Security.SecurityElement.Escape("\"" + Application.ExecutablePath + "\"");
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <UserId>" + user + "</UserId>\r\n" +
                "      <Delay>PT10S</Delay>\r\n" +
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <UserId>" + user + "</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>" + cmd + "</Command>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "RamCleanerTray-task.xml");
            File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            bool ok = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
            return ok;
        }
        catch { return false; }
    }

    static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(15000);
                return p.HasExited && p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    // true si el sistema está en juego/pantalla completa/presentación (Windows no aceptaría notificaciones)
    static bool NotifBlocked()
    {
        int s;
        if (Native.SHQueryUserNotificationState(out s) != 0) return false; // ante la duda, mostrar
        return s != 5; // QUNS_ACCEPTS_NOTIFICATIONS
    }

    static bool TaskExists()
    {
        return RunSchtasks("/Query /TN \"" + TaskName + "\"");
    }
}

static class Program
{
    static Mutex mtx;

    [STAThread]
    static void Main()
    {
        bool created;
        mtx = new Mutex(true, "RamCleanerTray_single_instance", out created);
        if (!created) return; // ya hay una instancia corriendo
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // UIPI bloquea TaskbarCreated hacia procesos elevados: sin este filtro el icono
        // no reaparece si explorer se reinicia, ni cuando el logon arranca antes que la taskbar
        Native.ChangeWindowMessageFilter(Native.RegisterWindowMessage("TaskbarCreated"), 1); // MSGFLT_ADD
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
        GC.KeepAlive(mtx);
    }
}
