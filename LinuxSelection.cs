// =====================================================================
//  Linux Selection - the X11 select-copy-paste workflow for Windows:
//  "select to copy" and "middle-click to paste".
//
//  Copyright (c) 2026 Aleksandr Igumenov
//  SPDX-License-Identifier: MIT (see LICENSE)
//  No third-party software required: Windows + .NET Framework 4.x only.
//
//  One .exe, several modes:
//    LinuxSelectionSetup.exe                  - installer (double-click)
//    LinuxSelectionSetup.exe --install /quiet - silent install (for automation)
//    ...\Program Files\LinuxSelection\LinuxSelection.exe
//        (no arguments)         - tray agent (mouse, clipboard, menu)
//        --uninstall [/quiet]   - uninstall (called by Settings > Apps)
//        --uninstall /confirm   - uninstall with confirmation (Start menu shortcut)
//        --service              - started by Windows (SCM), do not run manually
//
//  Why two processes: a Windows service runs in isolated session 0 and
//  cannot see the user's mouse, clipboard or notification area. The service
//  therefore launches the agent in every logged-on user session and keeps
//  it running (restarts it if it crashes).
// =====================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Linux Selection")]
[assembly: AssemblyProduct("Linux Selection")]
[assembly: AssemblyDescription("X11 select-copy-paste workflow for Windows")]
[assembly: AssemblyCompany("Aleksandr Igumenov")]
[assembly: AssemblyCopyright("(c) 2026 Aleksandr Igumenov")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

static class Program
{
    public const string ServiceName = "LinuxSelection";
    public const string DisplayName = "Linux Selection";
    public const string ProcName = "LinuxSelection";   // installed .exe name without extension
    public const string Version = "1.0.1";
    public const string Publisher = "Aleksandr Igumenov";
    public const string ShowEventName = @"Local\LinuxSelection_Show";

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        bool quiet = HasArg(args, "/quiet") || HasArg(args, "/silent") || HasArg(args, "/s");
        bool confirm = HasArg(args, "/confirm");
        int parentPid = 0;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--parent") int.TryParse(args[i + 1], out parentPid);

        if (mode == "--service") { ServiceBase.Run(new LauncherService()); return 0; }
        if (mode == "--install") return Setup.Install(quiet, parentPid);
        if (mode == "--uninstall") return Setup.Uninstall(quiet, confirm, parentPid);
        if (mode == "--make-icon" && args.Length > 1) { Logo.WriteIco(args[1]); return 0; }

        // Not started from the install folder (e.g. a downloaded LinuxSelectionSetup.exe) - act as installer
        if (!Setup.RunningFromInstallDir) return Setup.Interactive();

        bool created;
        using (var mtx = new Mutex(true, @"Local\LinuxSelection_Agent", out created))
        {
            if (!created)
            {
                // Agent already running in this session (e.g. started again from the Start menu):
                // ask it to show itself and make sure the service is running.
                try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch { }
                return 0;
            }
            Application.EnableVisualStyles();
            Application.Run(new TrayAgent());
        }
        return 0;
    }

    public static string ExePath
    {
        get { return Process.GetCurrentProcess().MainModule.FileName; }
    }

    static bool HasArg(string[] args, string a)
    {
        foreach (var s in args) if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinuxSelection");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "service.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
}

// =====================================================================
//  Service: makes sure an agent is running in every active session
// =====================================================================
class LauncherService : ServiceBase
{
    System.Threading.Timer timer;
    readonly Dictionary<int, DateTime> lastLaunch = new Dictionary<int, DateTime>();
    int busy;

    public LauncherService()
    {
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Program.Log("Service started");
        timer = new System.Threading.Timer(Tick, null, 1000, 3000);
    }

    protected override void OnStop()
    {
        if (timer != null) timer.Dispose();
        Program.Log("Service stopped");
    }

    void Tick(object state)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            foreach (int sid in Wts.ActiveSessions())
            {
                if (sid == 0 || AgentRunning(sid)) continue;
                DateTime t;
                if (lastLaunch.TryGetValue(sid, out t) && (DateTime.UtcNow - t).TotalSeconds < 10) continue;
                lastLaunch[sid] = DateTime.UtcNow;
                if (Wts.LaunchInSession(sid, Program.ExePath))
                    Program.Log("Agent started in session " + sid);
            }
        }
        catch (Exception ex) { Program.Log("Error: " + ex.Message); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    static bool AgentRunning(int sid)
    {
        bool found = false;
        foreach (var p in Process.GetProcessesByName(Program.ProcName))
        {
            try { if (p.SessionId == sid) found = true; } catch { }
            p.Dispose();
        }
        return found;
    }
}

// =====================================================================
//  Service status and control (used by the agent)
// =====================================================================
static class Svc
{
    public const int NotInstalled = 0, Running = 1, Stopped = 2, Pending = 3;

    public static int State()
    {
        try
        {
            using (var sc = new ServiceController(Program.ServiceName))
            {
                var s = sc.Status;
                if (s == ServiceControllerStatus.Running) return Running;
                if (s == ServiceControllerStatus.Stopped) return Stopped;
                return Pending;
            }
        }
        catch { return NotInstalled; }
    }

    // Blocking. Tries without admin rights first (setup grants them to interactive
    // users); if that fails, falls back to an elevated sc.exe via UAC.
    public static bool SetRunning(bool run)
    {
        var target = run ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
        try
        {
            using (var sc = new ServiceController(Program.ServiceName))
            {
                if (run && sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                else if (!run && sc.Status == ServiceControllerStatus.Running) sc.Stop();
                sc.WaitForStatus(target, TimeSpan.FromSeconds(15));
                return true;
            }
        }
        catch { }
        try
        {
            var psi = new ProcessStartInfo("sc.exe", (run ? "start " : "stop ") + Program.ServiceName)
            {
                Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var p = Process.Start(psi)) { if (p != null) p.WaitForExit(20000); }
            using (var sc = new ServiceController(Program.ServiceName))
            {
                sc.WaitForStatus(target, TimeSpan.FromSeconds(15));
                return true;
            }
        }
        catch { return false; }
    }
}

// =====================================================================
//  Install / uninstall
// =====================================================================
static class Setup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LinuxSelection";
    const string SettingsKey = @"Software\LinuxSelection";

    static string StartMenuDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Program.DisplayName); }
    }

    // Creates a .lnk via the built-in WScript.Shell COM object (no third-party tools)
    static void CreateShortcut(string lnk, string target, string args, string description)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = null;
        try
        {
            sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        finally
        {
            if (sc != null) Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    static void CreateStartMenu(string exe)
    {
        try
        {
            Directory.CreateDirectory(StartMenuDir);
            CreateShortcut(Path.Combine(StartMenuDir, Program.DisplayName + ".lnk"), exe, "",
                "Start Linux Selection");
            CreateShortcut(Path.Combine(StartMenuDir, "Uninstall " + Program.DisplayName + ".lnk"), exe,
                "--uninstall /confirm", "Uninstall Linux Selection");
        }
        catch (Exception ex) { Program.Log("Start menu shortcuts could not be created: " + ex.Message); }
    }

    public static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LinuxSelection"); }
    }

    static string InstalledExe { get { return Path.Combine(InstallDir, Program.ProcName + ".exe"); } }

    public static bool RunningFromInstallDir
    {
        get
        {
            return string.Equals(Path.GetFullPath(Program.ExePath), Path.GetFullPath(InstalledExe),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Relaunches itself with admin rights and waits for it to finish
    public static int Elevate(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Program.ExePath,
                args + " --parent " + Process.GetCurrentProcess().Id) { Verb = "runas", UseShellExecute = true };
            using (var p = Process.Start(psi))
            {
                if (p == null) return 1;
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        catch (Win32Exception) { return 1223; }   // UAC declined (ERROR_CANCELLED)
    }

    static RegistryKey Hklm()
    {
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
    }

    public static string InstalledVersion()
    {
        try
        {
            using (var root = Hklm())
            using (var k = root.OpenSubKey(UninstallKey))
                return k == null ? null : k.GetValue("DisplayVersion") as string;
        }
        catch { return null; }
    }

    static int Sc(string args)
    {
        var psi = new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true };
        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
    }

    static void WaitStopped()
    {
        try
        {
            using (var sc = new ServiceController(Program.ServiceName))
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
        catch { }
    }

    // Closes all agents (except this process and the one that launched it via UAC)
    static void KillAgents(int parentPid)
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName(Program.ProcName))
        {
            try { if (p.Id != me && p.Id != parentPid) { p.Kill(); p.WaitForExit(3000); } } catch { }
            p.Dispose();
        }
    }

    static void Info(bool quiet, string text, MessageBoxIcon icon)
    {
        Program.Log(text.Replace("\n", " "));
        if (!quiet) MessageBox.Show(text, Program.DisplayName, MessageBoxButtons.OK, icon);
    }

    // ---------------------------- Interactive installer ----------------------------
    public static int Interactive()
    {
        string inst = InstalledVersion();
        string q = inst == null
            ? "Install " + Program.DisplayName + " " + Program.Version + "?\n\n" +
              "- Selected text is copied to the clipboard automatically.\n" +
              "- The middle mouse button pastes text.\n\n" +
              "It will be installed as a Windows service and start automatically.\n" +
              "You can uninstall it from Settings > Apps."
            : Program.DisplayName + " " + inst + " is already installed.\n\n" +
              "Reinstall version " + Program.Version + "?";
        if (MessageBox.Show(q, Program.DisplayName + " Setup", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return 1602;   // ERROR_INSTALL_USEREXIT
        return Elevate("--install");
    }

    // ---------------------------------- Install ----------------------------------
    public static int Install(bool quiet, int parentPid)
    {
        if (!IsAdmin()) return Elevate("--install" + (quiet ? " /quiet" : ""));
        const string N = Program.ServiceName;
        try
        {
            string dst = InstalledExe;
            bool exists = Svc.State() != Svc.NotInstalled;
            if (exists) { Sc("stop " + N); WaitStopped(); }
            KillAgents(parentPid);

            // 1. Files. The service runs as SYSTEM, so the .exe must live in a protected folder
            Directory.CreateDirectory(InstallDir);
            if (!RunningFromInstallDir)
            {
                Exception last = null;
                for (int i = 0; i < 10; i++)   // the old .exe may still be locked for a moment
                {
                    try { File.Copy(Program.ExePath, dst, true); last = null; break; }
                    catch (IOException ex) { last = ex; Thread.Sleep(500); }
                }
                if (last != null) throw last;
            }

            // 2. Service
            string bin = "binPath= \"\\\"" + dst + "\\\" --service\" start= auto";
            int rc = exists
                ? Sc("config " + N + " " + bin)
                : Sc("create " + N + " " + bin + " DisplayName= \"" + Program.DisplayName + "\"");
            if (rc != 0) throw new Exception("sc.exe returned error " + rc);

            Sc("description " + N + " \"Linux-style select-to-copy and middle-click paste\"");
            // Interactive users (IU) may start/stop the service without UAC
            Sc("sdset " + N + " D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)");
            Sc("failure " + N + " reset= 86400 actions= restart/5000/restart/5000/restart/30000");

            // 3. Entry in Settings > Apps (Add/Remove Programs)
            using (var root = Hklm())
            using (var k = root.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Program.DisplayName);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", Program.Publisher);
                k.SetValue("DisplayIcon", dst + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + dst + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + dst + "\" --uninstall /quiet");
                k.SetValue("EstimatedSize", (int)(new FileInfo(dst).Length / 1024) + 1, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            // 4. Start menu: launcher and uninstaller
            CreateStartMenu(dst);

            // 5. Autostart entries from older versions are no longer needed
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("LinuxSelection", false); }
            catch { }

            // 6. Start - the service will launch the agent in the user session within seconds
            Sc("start " + N);

            Info(quiet, Program.DisplayName + " " + Program.Version + " has been installed.\n\n" +
                "The tray icon will appear in a few seconds.\n" +
                "Start menu: " + Program.DisplayName + " (launch and uninstall).\n" +
                "To uninstall you can also use Settings > Apps.", MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            Info(quiet, "Installation failed: " + ex.Message, MessageBoxIcon.Error);
            return 1603;   // ERROR_INSTALL_FAILURE
        }
    }

    // --------------------------------- Uninstall ---------------------------------
    public static int Uninstall(bool quiet, bool confirm, int parentPid)
    {
        if (confirm && !quiet &&
            MessageBox.Show("Uninstall " + Program.DisplayName + "?\n\nThe service, tray agent, Start menu shortcuts and settings will be removed.",
                Program.DisplayName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1602;
        if (!IsAdmin()) return Elevate("--uninstall" + (quiet ? " /quiet" : ""));
        const string N = Program.ServiceName;
        try
        {
            if (Svc.State() != Svc.NotInstalled)
            {
                Sc("stop " + N);
                WaitStopped();
                Sc("delete " + N);
            }
            KillAgents(parentPid);

            using (var root = Hklm()) root.DeleteSubKeyTree(UninstallKey, false);
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("LinuxSelection", false); }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(SettingsKey, false); } catch { }

            Info(quiet, Program.DisplayName + " has been uninstalled.", MessageBoxIcon.Information);

            // Delete files. A running .exe cannot delete itself, so cmd.exe does it
            // after this process (and the process that launched it) has exited.
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinuxSelection");
            if (Program.ExePath.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                string rm = "rmdir /s /q \"" + InstallDir + "\" 2>nul";
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & " + rm + " & ping 127.0.0.1 -n 4 >nul & " + rm +
                    " & rmdir /s /q \"" + data + "\" 2>nul")
                    { UseShellExecute = false, CreateNoWindow = true });
            }
            else
            {
                try { Directory.Delete(InstallDir, true); } catch { }
                try { Directory.Delete(data, true); } catch { }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Info(quiet, "Uninstall failed: " + ex.Message, MessageBoxIcon.Error);
            return 1603;
        }
    }
}

// =====================================================================
//  Logo: a colored rounded square with a text-cursor (I-beam) symbol
// =====================================================================
static class Logo
{
    public static void Draw(Graphics g, int size, Color bg)
    {
        float k = size / 32f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(new RectangleF(1 * k, 1 * k, 30 * k, 30 * k), 12 * k))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);
        using (var p = new Pen(Color.White, 3.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(p, 16 * k, 8 * k, 16 * k, 24 * k);
            g.DrawLine(p, 11 * k, 7 * k, 21 * k, 7 * k);
            g.DrawLine(p, 11 * k, 25 * k, 21 * k, 25 * k);
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float d)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Icon MakeIcon(Color bg)
    {
        using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, 32, bg);
            }
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { Native.DestroyIcon(h); }
        }
    }

    // Writes an .ico file for the application icon (used by build.bat so the .exe has its own icon)
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Draw(g, s, Color.FromArgb(46, 160, 67));
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }
        }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }
    }
}

// =====================================================================
//  Tray agent: mouse hook, clipboard, menu
// =====================================================================
class TrayAgent : ApplicationContext
{
    // ------------------------------- Settings -------------------------------
    // Applications where nothing is done (terminals, remote desktops, VMs)
    static readonly string[] Excluded = {
        "conhost.exe", "WindowsTerminal.exe", "mstsc.exe", "VirtualBoxVM.exe", "vmconnect.exe"
    };
    const int DragThreshold = 6;    // px
    // ------------------------------------------------------------------------

    const string SettingsKey = @"Software\LinuxSelection";

    bool enabled = true;            // "Enabled" menu checkbox
    bool separate = false;          // true = separate selection buffer (true Linux style)
    int svcState = Svc.NotInstalled;
    volatile bool active;           // enabled && service running (or not installed - standalone mode)
    bool opBusy;                    // service start/stop in progress
    string primary = "";

    Native.POINT downPt;
    bool downIBeam;
    uint lastUpTime;
    bool swallowMiddleUp;
    bool busy;
    bool trayClickPending;          // first click of a possible tray-icon double-click
    int lastTrayClick;

    readonly IntPtr ibeamCursor;
    readonly Native.LowLevelMouseProc proc;   // keep a reference so the GC doesn't collect it
    IntPtr hook;

    readonly System.Windows.Forms.Timer copyTimer, pasteTimer, statusTimer;
    readonly NotifyIcon tray;
    readonly Icon icoActive, icoDisabled, icoStopped;
    readonly ToolStripMenuItem miStatus, miEnabled, miSeparate, miStart, miStop, miInstall, miExit;
    readonly MessageWindow msgWindow;

    public TrayAgent()
    {
        ibeamCursor = Native.LoadCursor(IntPtr.Zero, 32513); // IDC_IBEAM
        LoadSettings();

        icoActive = Logo.MakeIcon(Color.FromArgb(46, 160, 67));     // green - active
        icoDisabled = Logo.MakeIcon(Color.FromArgb(128, 128, 128)); // grey - disabled via checkbox
        icoStopped = Logo.MakeIcon(Color.FromArgb(200, 70, 60));    // red - service stopped

        copyTimer = new System.Windows.Forms.Timer { Interval = 60 };
        copyTimer.Tick += delegate { copyTimer.Stop(); DoCopy(); };
        pasteTimer = new System.Windows.Forms.Timer { Interval = 10 };
        pasteTimer.Tick += delegate { pasteTimer.Stop(); DoPaste(); };

        proc = HookProc;
        hook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show("Failed to install the mouse hook.", Program.DisplayName);
            Environment.Exit(1);
        }

        miStatus = new ToolStripMenuItem("Service: ...") { Enabled = false };
        miEnabled = new ToolStripMenuItem("Enabled", null, (s, e) => ToggleEnabled(false)) { ShortcutKeyDisplayString = "Ctrl+Alt+P" };
        miSeparate = new ToolStripMenuItem("Separate selection buffer (Linux style)", null, (s, e) =>
        {
            separate = !separate; SaveSettings(); RefreshState();
        });
        miStart = new ToolStripMenuItem("Start service", null, (s, e) => ChangeService(true));
        miStop = new ToolStripMenuItem("Stop service", null, (s, e) => ChangeService(false));
        miInstall = new ToolStripMenuItem("Install as service...", null, (s, e) => Task.Run(() => Setup.Elevate("--install")));
        miExit = new ToolStripMenuItem("Stop and exit", null, (s, e) => StopAndExit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(miStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miEnabled);
        menu.Items.Add(miSeparate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miStart);
        menu.Items.Add(miStop);
        menu.Items.Add(miInstall);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);
        menu.Opening += (s, e) => RefreshState();

        tray = new NotifyIcon { Icon = icoActive, Text = Program.DisplayName, ContextMenuStrip = menu, Visible = true };
        // Double-click toggles "Enabled". Detected from two left-button releases instead of the
        // DoubleClick event, because the shell does not always deliver WM_LBUTTONDBLCLK to tray icons.
        tray.MouseUp += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            int now = Environment.TickCount;
            if (trayClickPending && unchecked(now - lastTrayClick) <= SystemInformation.DoubleClickTime)
            {
                trayClickPending = false;
                ToggleEnabled(false);
            }
            else
            {
                trayClickPending = true;
                lastTrayClick = now;
            }
        };

        statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        statusTimer.Tick += (s, e) => RefreshState();
        statusTimer.Start();

        msgWindow = new MessageWindow(() => ToggleEnabled(true), OnShowRequested);

        // Signal from a second launch (e.g. Start menu) -> show ourselves
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var waiter = new Thread(() => { while (showEvent.WaitOne()) msgWindow.PostShow(); }) { IsBackground = true };
        waiter.Start();

        // When started manually while the service is stopped (e.g. after "Stop and exit"), start it
        var startup = new System.Windows.Forms.Timer { Interval = 800 };
        startup.Tick += (s, e) =>
        {
            startup.Stop();
            startup.Dispose();
            if (Svc.State() == Svc.Stopped) ChangeService(true);
        };
        startup.Start();

        RefreshState();
    }

    void OnShowRequested()
    {
        RefreshState();
        if (svcState == Svc.Stopped) ChangeService(true);
        tray.ShowBalloonTip(3000, Program.DisplayName,
            "Linux Selection is running. Right-click the tray icon for options.", ToolTipIcon.Info);
    }

    // ----------------------------- State and menu -----------------------------
    void RefreshState()
    {
        if (!opBusy) svcState = Svc.State();
        bool svcOk = svcState == Svc.Running || svcState == Svc.NotInstalled;
        active = enabled && svcOk;

        string st = svcState == Svc.Running ? "running"
                  : svcState == Svc.Stopped ? "stopped"
                  : svcState == Svc.Pending ? "changing state..."
                  : "not installed (standalone mode)";
        if (opBusy) st = "changing state...";
        miStatus.Text = "Service: " + st;

        miEnabled.Checked = enabled;
        miSeparate.Checked = separate;

        bool installed = svcState != Svc.NotInstalled;
        miStart.Visible = installed;
        miStop.Visible = installed;
        miInstall.Visible = !installed;
        miStart.Enabled = !opBusy && svcState == Svc.Stopped;
        miStop.Enabled = !opBusy && svcState == Svc.Running;
        miExit.Enabled = !opBusy;
        miExit.Text = installed ? "Stop and exit" : "Exit";

        Icon want = active ? icoActive : (!svcOk ? icoStopped : icoDisabled);
        if (tray.Icon != want) tray.Icon = want;
        tray.Text = "Linux Selection - " + (active ? "active" : !svcOk ? "service stopped" : "disabled");
    }

    void ToggleEnabled(bool notify)
    {
        enabled = !enabled;
        SaveSettings();
        RefreshState();
        if (notify)
            tray.ShowBalloonTip(1000, Program.DisplayName, enabled ? "Enabled" : "Disabled", ToolTipIcon.Info);
    }

    void ChangeService(bool run)
    {
        if (opBusy) return;
        opBusy = true;
        RefreshState();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => Svc.SetRunning(run)).ContinueWith(t =>
        {
            opBusy = false;
            RefreshState();
            if (!t.Result)
                tray.ShowBalloonTip(3000, Program.DisplayName,
                    run ? "Failed to start the service." : "Failed to stop the service.", ToolTipIcon.Warning);
        }, ui);
    }

    void StopAndExit()
    {
        if (opBusy) return;
        int s = Svc.State();
        if (s == Svc.NotInstalled || s == Svc.Stopped) { ExitThread(); return; }

        opBusy = true;
        RefreshState();
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        Task.Run(() => Svc.SetRunning(false)).ContinueWith(t =>
        {
            opBusy = false;
            if (t.Result) { ExitThread(); return; }
            RefreshState();
            // If the service kept running it would relaunch the agent within seconds
            tray.ShowBalloonTip(3000, Program.DisplayName,
                "Failed to stop the service, so the application was not closed.", ToolTipIcon.Warning);
        }, ui);
    }

    // ------------------------------- Mouse hook -------------------------------
    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            bool injected = (m.flags & 1) != 0;   // ignore events we inject ourselves
            int msg = (int)wParam;

            if (!injected && msg == 0x0208 /*WM_MBUTTONUP*/ && swallowMiddleUp)
            {
                swallowMiddleUp = false;
                if (active) pasteTimer.Start();
                return (IntPtr)1;
            }

            if (!injected && active)
            {
                switch (msg)
                {
                    case 0x0201: // WM_LBUTTONDOWN
                        downPt = m.pt;
                        downIBeam = IsIBeam();
                        break;

                    case 0x0202: // WM_LBUTTONUP
                    {
                        bool dragged = Math.Abs(m.pt.x - downPt.x) > DragThreshold ||
                                       Math.Abs(m.pt.y - downPt.y) > DragThreshold;
                        bool dbl = unchecked(m.time - lastUpTime) < Native.GetDoubleClickTime();
                        lastUpTime = m.time;
                        if (downIBeam && (dragged || dbl))
                        {
                            copyTimer.Stop();
                            copyTimer.Start();
                        }
                        break;
                    }

                    case 0x0207: // WM_MBUTTONDOWN
                        if (IsIBeam() && !IsExcluded())
                        {
                            swallowMiddleUp = true;
                            return (IntPtr)1;            // swallow it - the application never sees it
                        }
                        break;
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // ---------------------------------- Copy ----------------------------------
    async void DoCopy()
    {
        if (busy || !active || IsExcluded()) return;
        if (KeyDown(0x12) || KeyDown(0x5B) || KeyDown(0x5C)) return;  // Alt / Win held down
        busy = true;
        try
        {
            DataObject saved = separate ? SnapshotClipboard() : null;
            uint seq = Native.GetClipboardSequenceNumber();

            if (KeyDown(0x10)) SendKey(0x10, true);   // release Shift to avoid Ctrl+Shift+C
            SendCtrl(0x43);                           // Ctrl+C

            bool changed = false;
            for (int i = 0; i < 20 && !changed; i++)
            {
                await Task.Delay(20);
                changed = Native.GetClipboardSequenceNumber() != seq;
            }
            if (!changed) return;                     // nothing selected - clipboard unchanged

            await Task.Delay(30);
            string text = TryGetText();
            if (text != null) primary = text;

            if (separate && saved != null) TrySetData(saved);
        }
        finally { busy = false; }
    }

    // ---------------------------------- Paste ----------------------------------
    async void DoPaste()
    {
        if (busy) return;
        busy = true;
        try
        {
            ClickLeft();                              // move the text caret to the mouse position
            await Task.Delay(40);

            if (separate)
            {
                if (string.IsNullOrEmpty(primary)) return;
                DataObject saved = SnapshotClipboard();
                if (!TrySetData(primary)) return;
                SendCtrl(0x56);                       // Ctrl+V
                await Task.Delay(250);
                if (saved != null) TrySetData(saved);
            }
            else
            {
                SendCtrl(0x56);
            }
        }
        finally { busy = false; }
    }

    // --------------------------------- Helpers ---------------------------------
    bool IsIBeam()
    {
        var ci = new Native.CURSORINFO();
        ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
        return Native.GetCursorInfo(ref ci) && ci.hCursor == ibeamCursor;
    }

    static bool IsExcluded()
    {
        string exe = ForegroundExe();
        foreach (var e in Excluded)
            if (string.Equals(exe, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string ForegroundExe()
    {
        uint pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        IntPtr h = Native.OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : "";
        }
        finally { Native.CloseHandle(h); }
    }

    static bool KeyDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

    static void SendCtrl(ushort vk)
    {
        var inputs = new[] {
            KeyInput(0x11, false), KeyInput(vk, false),
            KeyInput(vk, true),    KeyInput(0x11, true)
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    static void SendKey(ushort vk, bool up)
    {
        var inputs = new[] { KeyInput(vk, up) };
        Native.SendInput(1, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    static Native.INPUT KeyInput(ushort vk, bool up)
    {
        var i = new Native.INPUT { type = 1 };
        i.u.ki.wVk = vk;
        i.u.ki.dwFlags = up ? 2u : 0u;
        return i;
    }

    static void ClickLeft()
    {
        var down = new Native.INPUT { type = 0 }; down.u.mi.dwFlags = 0x0002; // LEFTDOWN
        var up   = new Native.INPUT { type = 0 }; up.u.mi.dwFlags   = 0x0004; // LEFTUP
        Native.SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    static DataObject SnapshotClipboard()
    {
        try
        {
            IDataObject src = Clipboard.GetDataObject();
            if (src == null) return null;
            var copy = new DataObject();
            foreach (string f in src.GetFormats(false))
            {
                try { object o = src.GetData(f, false); if (o != null) copy.SetData(f, false, o); }
                catch { }
            }
            return copy;
        }
        catch { return null; }
    }

    static string TryGetText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { Thread.Sleep(10); }
        }
        return null;
    }

    static bool TrySetData(object data)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (data is string) Clipboard.SetText((string)data);
                else Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { Thread.Sleep(10); }
        }
        return false;
    }

    // -------------------------------- Settings --------------------------------
    void LoadSettings()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (k == null) return;
                separate = Convert.ToInt32(k.GetValue("Separate", 0)) == 1;
                enabled = Convert.ToInt32(k.GetValue("Enabled", 1)) == 1;
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                k.SetValue("Separate", separate ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        statusTimer.Stop();
        copyTimer.Stop();
        pasteTimer.Stop();
        msgWindow.Dispose();
        tray.Visible = false;
        tray.Dispose();
        icoActive.Dispose();
        icoDisabled.Dispose();
        icoStopped.Dispose();
        base.ExitThreadCore();
    }
}

// Hidden window: Ctrl+Alt+P hotkey and "show" requests from other threads
class MessageWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312, WM_SHOW = 0x8001 /*WM_APP+1*/;
    readonly Action onHotkey, onShow;

    public MessageWindow(Action onHotkey, Action onShow)
    {
        this.onHotkey = onHotkey;
        this.onShow = onShow;
        CreateHandle(new CreateParams());
        Native.RegisterHotKey(Handle, 1, 0x0002 | 0x0001 | 0x4000 /*CTRL|ALT|NOREPEAT*/, 0x50 /*P*/);
    }

    public void PostShow() { Native.PostMessage(Handle, WM_SHOW, IntPtr.Zero, IntPtr.Zero); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) onHotkey();
        else if (m.Msg == WM_SHOW) onShow();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

// =====================================================================
//  Sessions and launching processes in a user session (used by the service)
// =====================================================================
static class Wts
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WTS_SESSION_INFO { public int SessionId; public string pWinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
    static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static List<int> ActiveSessions()
    {
        var list = new List<int>();
        IntPtr p; int n;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out p, out n)) return list;
        try
        {
            int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
            for (int i = 0; i < n; i++)
            {
                var s = (WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + (long)i * size), typeof(WTS_SESSION_INFO));
                if (s.State == 0 /*WTSActive*/) list.Add(s.SessionId);
            }
        }
        finally { WTSFreeMemory(p); }
        return list;
    }

    public static bool LaunchInSession(int sid, string exe)
    {
        IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sid, out tok))
            {
                Program.Log("WTSQueryUserToken(" + sid + ") error " + Marshal.GetLastWin32Error());
                return false;
            }
            if (!DuplicateTokenEx(tok, 0x02000000 /*MAXIMUM_ALLOWED*/, IntPtr.Zero, 2 /*Impersonation*/, 1 /*Primary*/, out dup))
                return false;
            CreateEnvironmentBlock(out env, dup, false);

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = @"winsta0\default";
            PROCESS_INFORMATION pi;
            var cmd = new StringBuilder("\"" + exe + "\"");
            bool ok = CreateProcessAsUser(dup, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                0x00000400 /*CREATE_UNICODE_ENVIRONMENT*/, env, Path.GetDirectoryName(exe), ref si, out pi);
            if (ok) { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
            else Program.Log("CreateProcessAsUser error " + Marshal.GetLastWin32Error());
            return ok;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (tok != IntPtr.Zero) CloseHandle(tok);
        }
    }
}

// =====================================================================
//  Win32 API
// =====================================================================
static class Native
{
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
