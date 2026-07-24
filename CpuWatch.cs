// CpuWatch — vigilante de picos de CPU: registra qué procesos consumían cuando
// la CPU total supera el umbral de forma sostenida (config en CpuWatch.ini).
// Compilar: csc /nologo /optimize /codepage:65001 /target:winexe /out:CpuWatch.exe CpuWatch.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class CpuWatch
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    class ProcUse { public int Pid; public string Name; public double Pct; }

    // config (CpuWatch.ini junto al exe; se crea con defaults si no existe)
    static int umbral = 80;                // % de CPU total que se considera pico
    static int segundos = 10;              // debe sostenerse este tiempo
    static int cooldownMin = 5;            // minutos minimos entre registros
    static bool ignorarPrimerPlano = true; // no registrar si la app en primer plano causa el pico

    const int SampleMs = 2000;
    static Mutex mtx; // campo estatico: si fuera local el GC podria liberarlo y permitir 2a instancia

    static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

    static void Main()
    {
        bool created;
        mtx = new Mutex(true, "CpuWatch_single_instance", out created);
        if (!created) return;

        LoadOrCreateConfig();
        int needed = Math.Max(1, (int)Math.Ceiling(segundos * 1000.0 / SampleMs));
        Log(string.Format("CpuWatch iniciado (umbral {0}%, {1} s sostenidos, cooldown {2} min, ignorar primer plano: {3})",
            umbral, segundos, cooldownMin, ignorarPrimerPlano ? "si" : "no"));

        long pIdle, pKernel, pUser;
        GetSystemTimes(out pIdle, out pKernel, out pUser);
        var prev = Snapshot();
        var sw = Stopwatch.StartNew();
        int hot = 0;
        DateTime lastEvent = DateTime.MinValue;

        while (true)
        {
            Thread.Sleep(SampleMs);

            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) continue;
            long busyD = (kernel - pKernel) + (user - pUser) - (idle - pIdle);
            long totalD = (kernel - pKernel) + (user - pUser);
            pIdle = idle; pKernel = kernel; pUser = user;
            int total = totalD > 0 ? (int)(busyD * 100 / totalD) : 0;
            if (total < 0) total = 0; else if (total > 100) total = 100;

            long elapsed = sw.ElapsedMilliseconds;
            sw.Restart();
            var cur = Snapshot();
            var uses = Diff(prev, cur, elapsed);
            prev = cur;

            if (total >= umbral) hot++; else hot = 0;
            if (hot < needed || (DateTime.UtcNow - lastEvent).TotalMinutes < cooldownMin) continue;
            hot = 0;
            lastEvent = DateTime.UtcNow; // cooldown tambien si se decide no registrar (gaming sostenido)

            uint fgPid = 0;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            string fgName = null;
            double fgPct = 0;
            foreach (var u in uses)
                if (u.Pid == (int)fgPid) { fgName = u.Name; fgPct = u.Pct; break; }

            // si la app que estas usando causa la mayor parte del consumo, no es "raro": no registrar
            if (ignorarPrimerPlano && fgName != null && fgPct >= total * 0.6) continue;

            var top = new List<string>();
            foreach (var u in uses)
            {
                if (top.Count >= 5 || u.Pct < 1) break;
                top.Add(string.Format("{0} {1:F0}%", u.Name, u.Pct));
            }
            Log(string.Format("PICO CPU {0}% sostenido {1} s | primer plano: {2} | top: {3}",
                total, segundos,
                fgName == null ? "(ninguno)" : string.Format("{0} ({1:F0}%)", fgName, fgPct),
                top.Count > 0 ? string.Join(", ", top.ToArray()) : "sin datos"));
        }
    }

    static Dictionary<int, KeyValuePair<string, double>> Snapshot()
    {
        var d = new Dictionary<int, KeyValuePair<string, double>>();
        foreach (var p in Process.GetProcesses())
        {
            try { d[p.Id] = new KeyValuePair<string, double>(p.ProcessName, p.TotalProcessorTime.TotalMilliseconds); }
            catch { } // procesos protegidos o ya muertos
            finally { p.Dispose(); }
        }
        return d;
    }

    static List<ProcUse> Diff(Dictionary<int, KeyValuePair<string, double>> prev, Dictionary<int, KeyValuePair<string, double>> cur, long elapsedMs)
    {
        var list = new List<ProcUse>();
        if (elapsedMs <= 0) return list;
        int cores = Environment.ProcessorCount;
        foreach (var kv in cur)
        {
            KeyValuePair<string, double> old;
            if (!prev.TryGetValue(kv.Key, out old)) continue;
            if (old.Key != kv.Value.Key) continue; // PID reutilizado por otro proceso
            double d = kv.Value.Value - old.Value;
            if (d <= 0) continue;
            var u = new ProcUse();
            u.Pid = kv.Key;
            u.Name = kv.Value.Key;
            u.Pct = d / elapsedMs / cores * 100.0;
            list.Add(u);
        }
        list.Sort(delegate(ProcUse a, ProcUse b) { return b.Pct.CompareTo(a.Pct); });
        return list;
    }

    static void LoadOrCreateConfig()
    {
        string path = Path.Combine(BaseDir, "CpuWatch.ini");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllLines(path, new string[] {
                    "umbral=" + umbral,
                    "segundos=" + segundos,
                    "cooldown_min=" + cooldownMin,
                    "ignorar_primer_plano=" + (ignorarPrimerPlano ? 1 : 0)
                });
                return;
            }
            foreach (var line in File.ReadAllLines(path))
            {
                var kv = line.Split(new char[] { '=' }, 2);
                if (kv.Length != 2) continue;
                string k = kv[0].Trim(), v = kv[1].Trim();
                int n;
                if (k == "umbral" && int.TryParse(v, out n) && n > 0 && n <= 100) umbral = n;
                else if (k == "segundos" && int.TryParse(v, out n) && n > 0) segundos = n;
                else if (k == "cooldown_min" && int.TryParse(v, out n) && n >= 0) cooldownMin = n;
                else if (k == "ignorar_primer_plano") ignorarPrimerPlano = v == "1";
            }
        }
        catch { }
    }

    static void Log(string msg)
    {
        try
        {
            string log = Path.Combine(BaseDir, "CpuWatch.log");
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(log, string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}{2}", DateTime.Now, msg, Environment.NewLine));
        }
        catch { }
    }
}
