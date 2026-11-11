// 寶石小缸 桌布宿主（原型）
// 把 wall.html 掛到 Windows 桌面圖示層的下方，並把「桌面圖示矩形」「游標位置」「是否該暫停」傳給頁面。
// 用 Windows 內建的 C# 5 編譯器建置（見 build.ps1），不需要安裝 Visual Studio。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool wantExit = Array.IndexOf(args, "--exit") >= 0;
        string snap = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--snap") snap = args[i + 1];

        bool first;
        using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, "GemTankWall.Quit", out first))
        {
            if (!first) { if (wantExit) quit.Set(); return; }   // 已經有一份在跑：--exit 叫它結束，否則什麼都不做
            if (wantExit) return;

            Application.EnableVisualStyles();
            var form = new WallForm(snap);
            var waiter = new Thread(delegate()
            {
                quit.WaitOne();
                try { form.BeginInvoke((Action)form.Quit); } catch { }
            });
            waiter.IsBackground = true;
            waiter.Start();
            Application.Run(form);
        }
    }
}

static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GemTankWallpaper");
    public static string FilePath { get { return Path.Combine(Dir, "host.log"); } }
    public static void Write(string s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}

class WallForm : Form
{
    readonly WebView2 web = new WebView2();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer slow = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer fast = new System.Windows.Forms.Timer();
    readonly IconProbe probe = new IconProbe();
    readonly string snapPath;
    Rectangle scr;
    IntPtr parent, anchor;
    bool layered, closing, userPause, pageReady;
    string lastIcons = "", lastCur = "", lastArea = "";
    int lastPause = -1;
    ToolStripMenuItem pauseItem;

    public WallForm(string snapPath)
    {
        this.snapPath = snapPath;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(31, 98, 118);
        Bounds = Screen.PrimaryScreen.Bounds;
        Text = "GemTankWall";

        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        var menu = new ContextMenuStrip();
        pauseItem = new ToolStripMenuItem("暫停");
        pauseItem.Click += delegate
        {
            userPause = !userPause;
            pauseItem.Text = userPause ? "繼續" : "暫停";
            PollPause();
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add("重新載入頁面", null, delegate { try { if (web.CoreWebView2 != null) web.CoreWebView2.Reload(); } catch { } });
        menu.Items.Add("開啟記錄檔", null, delegate { try { System.Diagnostics.Process.Start(Log.FilePath); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束（還原桌布）", null, delegate { Quit(); });
        tray.Icon = SystemIcons.Application;
        tray.Text = "寶石小缸 桌布原型";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;

        slow.Interval = 400;
        slow.Tick += delegate { KeepOrder(); PollIcons(); PollArea(); PollPause(); };
        fast.Interval = 40;
        fast.Tick += delegate { PollCursor(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= W.WS_EX_NOACTIVATE | W.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    static string FindPageDir()
    {
        string d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 5 && d != null; i++)
        {
            if (File.Exists(Path.Combine(d, "wall.html"))) return d;
            d = Path.GetDirectoryName(d.TrimEnd('\\', '/'));
        }
        return null;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Log.Write("---- 啟動 ----");
            string pageDir = FindPageDir();
            if (pageDir == null) { Fail("找不到 wall.html"); return; }
            if (!Attach()) { Fail("找不到桌面視窗，無法掛成桌布"); return; }

            // 拍照驗證時視窗多半被其他程式蓋住，關掉「被蓋住就不畫」的最佳化才拍得到
            var opt = new CoreWebView2EnvironmentOptions(snapPath != null ? "--disable-features=CalculateNativeWinOcclusion" : null);
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Dir, "WebView2"), opt);
            await web.EnsureCoreWebView2Async(env);
            if (closing || IsDisposed) return;
            var core = web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("gemtank.local", pageDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += delegate(object s, CoreWebView2WebMessageReceivedEventArgs a)
            {
                string msg = null;
                try { msg = a.TryGetWebMessageAsString(); } catch { }
                if (msg == null) return;
                if (msg.StartsWith("stat|")) { Log.Write("頁面：" + msg.Substring(5)); return; }
                if (msg != "ready") return;
                pageReady = true;
                lastIcons = lastCur = lastArea = ""; lastPause = -1;      // 頁面（重新）載入：全部重送
                if (snapPath != null) Post("{\"t\":\"debug\",\"v\":true}");
                PollArea(); PollIcons(); PollPause();
            };
            core.Navigate("https://gemtank.local/wall.html");
            slow.Start();
            fast.Start();
            Log.Write("WebView2 " + core.Environment.BrowserVersionString + "，頁面資料夾 " + pageDir);

            if (snapPath != null)
            {
                var t = new System.Windows.Forms.Timer();
                t.Interval = 9000;
                t.Tick += async delegate
                {
                    t.Stop();
                    try
                    {
                        using (var fs = File.Create(snapPath))
                            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                        Log.Write("已拍照：" + snapPath);
                    }
                    catch (Exception ex) { Log.Write("拍照失敗：" + ex.Message); }
                };
                t.Start();
            }
        }
        catch (Exception ex)
        {
            if (closing || IsDisposed) return;
            Fail("啟動失敗：" + ex.Message);
            Log.Write(ex.ToString());
        }
    }

    void Fail(string msg)
    {
        Log.Write(msg);
        MessageBox.Show(msg + "\n\n記錄檔：" + Log.FilePath, "寶石小缸 桌布原型");
        Quit();
    }

    void Post(string json)
    {
        if (closing || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsString(json); }
        catch (Exception ex) { Log.Write("傳訊失敗：" + ex.Message); }
    }

    // ------------------------------------------------------------ 掛到桌面
    // 新版 Windows 11（24H2 起）：Progman 底下直接有圖示層 SHELLDLL_DefView 和系統桌布層 WorkerW，
    //   我們當 Progman 的子視窗，排在圖示層的正下方。Progman 的子視窗要是分層視窗才畫得出來。
    // 舊版：圖示層住在某個頂層 WorkerW 裡，它後面那個頂層 WorkerW 負責畫桌布，我們掛進後者。
    static IntPtr ClassicWorker()
    {
        IntPtr iconHost = IntPtr.Zero;
        W.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (W.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            iconHost = h;
            return false;
        }, IntPtr.Zero);
        return iconHost == IntPtr.Zero ? IntPtr.Zero : W.FindWindowEx(IntPtr.Zero, iconHost, "WorkerW", null);
    }

    static void AskForWallpaperLayer(IntPtr progman)
    {
        IntPtr r;
        W.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out r);
    }

    bool Attach()
    {
        scr = Screen.PrimaryScreen.Bounds;
        IntPtr progman = W.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;
        IntPtr defView = W.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        string how;

        if (defView != IntPtr.Zero)
        {
            if (W.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) == IntPtr.Zero)
            {
                AskForWallpaperLayer(progman);
                for (int i = 0; i < 20 && W.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) == IntPtr.Zero; i++) Thread.Sleep(100);
            }
            parent = progman; anchor = defView; layered = true;
            W.SetWindowLong(Handle, W.GWL_EXSTYLE, W.GetWindowLong(Handle, W.GWL_EXSTYLE) | W.WS_EX_LAYERED);
            W.SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */);
            how = "新版：Progman 的分層子視窗，圖示層正下方";
        }
        else
        {
            IntPtr worker = ClassicWorker();
            if (worker == IntPtr.Zero)
            {
                AskForWallpaperLayer(progman);
                for (int i = 0; i < 20 && (worker = ClassicWorker()) == IntPtr.Zero; i++) Thread.Sleep(100);
            }
            if (worker == IntPtr.Zero) return false;
            parent = worker; anchor = IntPtr.Zero; layered = false;
            how = "舊版：WorkerW 的子視窗";
        }

        int style = W.GetWindowLong(Handle, W.GWL_STYLE);
        style = (style & ~(W.WS_POPUP | W.WS_CAPTION | W.WS_THICKFRAME)) | W.WS_CHILD | W.WS_VISIBLE;
        W.SetWindowLong(Handle, W.GWL_STYLE, style);
        W.SetParent(Handle, parent);
        var p = new W.POINT { X = scr.X, Y = scr.Y };
        W.ScreenToClient(parent, ref p);
        W.SetWindowPos(Handle, anchor, p.X, p.Y, scr.Width, scr.Height, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW | W.SWP_FRAMECHANGED);
        Log.Write("已掛載（" + how + "），主螢幕 " + scr + "，視窗層級正確=" + OrderOk());
        return true;
    }

    bool OrderOk()
    {
        return !layered || W.GetWindow(anchor, 2 /* GW_HWNDNEXT */) == Handle;
    }

    // 系統換桌布時會把新的桌布層插到我們上面，發現被擠開就排回圖示層正下方
    int orderFixes;
    void KeepOrder()
    {
        if (closing || !layered || !W.IsWindow(anchor) || OrderOk()) return;
        W.SetWindowPos(Handle, anchor, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE);
        if (++orderFixes <= 10) Log.Write("視窗層級被打亂，已修正");
    }

    // ------------------------------------------------------------ 圖示、工作區、游標、暫停
    void PollIcons()
    {
        if (!pageReady) return;
        bool shown;
        List<W.RECT> rects = probe.Read(out shown);
        if (rects == null) return;
        var sb = new StringBuilder("{\"t\":\"icons\",\"r\":[");
        for (int i = 0; i < rects.Count; i++)
        {
            W.RECT r = rects[i];
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(r.Left - scr.X).Append(',').Append(r.Top - scr.Y).Append(',')
              .Append(r.Right - r.Left).Append(',').Append(r.Bottom - r.Top).Append(']');
        }
        string json = sb.Append("]}").ToString();
        if (json == lastIcons) return;
        lastIcons = json;
        Post(json);
        Log.Write("圖示有變：" + rects.Count + " 個" + (shown ? "" : "（使用者把桌面圖示隱藏了）"));
    }

    // 工作列佔掉的底部高度，頁面把沙地畫在它上面
    void PollArea()
    {
        if (!pageReady) return;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        string json = "{\"t\":\"area\",\"b\":" + Math.Max(0, scr.Bottom - wa.Bottom) + "}";
        if (json == lastArea) return;
        lastArea = json;
        Post(json);
    }

    static string RootClass(IntPtr w)
    {
        if (w == IntPtr.Zero) return "";
        var sb = new StringBuilder(64);
        W.GetClassName(W.GetAncestor(w, 2 /* GA_ROOT */), sb, 64);
        return sb.ToString();
    }

    // 桌布層收不到滑鼠事件（圖示層蓋在上面），所以改成每 40ms 看一次游標：
    // 游標正下方是桌面（不是其他程式的視窗）時，把位置和左鍵狀態傳給頁面。
    void PollCursor()
    {
        if (!pageReady || lastPause == 1) return;
        W.POINT p;
        if (!W.GetCursorPos(out p)) return;
        string cls = RootClass(W.WindowFromPoint(p));
        bool onDesktop = (cls == "Progman" || cls == "WorkerW") && scr.Contains(p.X, p.Y);
        string json = onDesktop
            ? "{\"t\":\"cur\",\"x\":" + (p.X - scr.X) + ",\"y\":" + (p.Y - scr.Y) + ",\"d\":" + ((W.GetAsyncKeyState(1) & 0x8000) != 0 ? 1 : 0) + "}"
            : "{\"t\":\"cur\",\"x\":-1,\"y\":-1,\"d\":0}";
        if (json == lastCur) return;
        lastCur = json;
        Post(json);
    }

    // 桌面完全被其他視窗蓋住時，桌布反正看不到，叫頁面停下來省電。
    // 不看「前景視窗是否最大化」：按 Win+D 顯示桌面時，前景視窗仍是那個最大化的程式，會誤判。
    // 改成在主螢幕上取 9 個點，只要有一個點的最上層是桌面，就當作看得到。
    bool DesktopVisible()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        for (int iy = 1; iy <= 3; iy++)
            for (int ix = 1; ix <= 3; ix++)
            {
                var p = new W.POINT { X = wa.Left + wa.Width * ix / 4, Y = wa.Top + wa.Height * iy / 4 };
                string cls = RootClass(W.WindowFromPoint(p));
                if (cls == "Progman" || cls == "WorkerW") return true;
            }
        return false;
    }

    void PollPause()
    {
        if (!pageReady) return;
        bool pause = userPause || (snapPath == null && !DesktopVisible());
        int v = pause ? 1 : 0;
        if (v == lastPause) return;
        lastPause = v;
        Post("{\"t\":\"pause\",\"v\":" + (pause ? "true" : "false") + "}");
        Log.Write(pause ? (userPause ? "暫停（手動）" : "暫停（桌面被蓋住）") : "繼續");
    }

    // ------------------------------------------------------------ 結束
    public void Quit()
    {
        if (closing) return;
        closing = true;
        slow.Stop(); fast.Stop();
        probe.Dispose();
        tray.Visible = false;
        try { web.Dispose(); } catch { }
        if (IsHandleCreated) { W.SetParent(Handle, IntPtr.Zero); Hide(); }
        // 不動系統的桌布設定，只請檔案總管把原本的桌布重畫出來
        if (parent != IntPtr.Zero && W.IsWindow(parent))
            W.RedrawWindow(parent, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100);
        Log.Write("已結束");
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false;
        probe.Dispose();
        base.OnFormClosed(e);
    }
}

// 讀桌面圖示矩形。桌面圖示是檔案總管行程裡的一個清單控制項（SysListView32），
// 對它送 LVM_GETITEMRECT 就能問到每個圖示（含文字）的範圍；因為回傳的矩形要寫在
// 「對方行程」的記憶體裡，所以先在檔案總管那邊借 16 位元組，問完再讀回來。
// 只問矩形，不問名稱、不碰檔案。
class IconProbe : IDisposable
{
    IntPtr list, proc, buf;

    static IntPtr FindList()
    {
        IntPtr view = IntPtr.Zero;
        IntPtr progman = W.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) view = W.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view == IntPtr.Zero)
        {
            W.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                view = W.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                return view == IntPtr.Zero;
            }, IntPtr.Zero);
        }
        return view == IntPtr.Zero ? IntPtr.Zero : W.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
    }

    bool Open()
    {
        Release();
        list = FindList();
        if (list == IntPtr.Zero) return false;
        uint pid;
        W.GetWindowThreadProcessId(list, out pid);
        proc = W.OpenProcess(0x0008 | 0x0010 | 0x0020 /* VM_OPERATION | VM_READ | VM_WRITE */, false, pid);
        if (proc == IntPtr.Zero) return false;
        buf = W.VirtualAllocEx(proc, IntPtr.Zero, new IntPtr(64), 0x3000 /* COMMIT | RESERVE */, 0x04 /* READWRITE */);
        return buf != IntPtr.Zero;
    }

    // 檔案總管卡住時不要跟著卡：每次詢問最多等 200ms
    static bool Ask(IntPtr h, uint msg, IntPtr w, IntPtr l, out IntPtr result)
    {
        return W.SendMessageTimeout(h, msg, w, l, 0x0002 /* SMTO_ABORTIFHUNG */, 200, out result) != IntPtr.Zero;
    }

    public List<W.RECT> Read(out bool shown)
    {
        shown = false;
        if ((list == IntPtr.Zero || buf == IntPtr.Zero || !W.IsWindow(list)) && !Open()) return null;
        var result = new List<W.RECT>();
        shown = W.IsWindowVisible(list);
        if (!shown) return result;

        IntPtr n;
        if (!Ask(list, 0x1004 /* LVM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero, out n)) return null;
        var origin = new W.POINT();
        W.ClientToScreen(list, ref origin);
        int count = n.ToInt32();
        for (int i = 0; i < count; i++)
        {
            var r = new W.RECT();           // Left 先填 0 = LVIR_BOUNDS：圖示加文字的整體範圍
            IntPtr io, ok;
            if (!W.WriteProcessMemory(proc, buf, ref r, 16, out io)) { Release(); return null; }
            if (!Ask(list, 0x100E /* LVM_GETITEMRECT */, new IntPtr(i), buf, out ok)) return null;
            if (ok == IntPtr.Zero) continue;
            if (!W.ReadProcessMemory(proc, buf, out r, 16, out io)) { Release(); return null; }
            r.Left += origin.X; r.Right += origin.X; r.Top += origin.Y; r.Bottom += origin.Y;
            result.Add(r);
        }
        return result;
    }

    void Release()
    {
        if (buf != IntPtr.Zero && proc != IntPtr.Zero) W.VirtualFreeEx(proc, buf, IntPtr.Zero, 0x8000 /* MEM_RELEASE */);
        if (proc != IntPtr.Zero) W.CloseHandle(proc);
        buf = proc = list = IntPtr.Zero;
    }

    public void Dispose() { Release(); }
}

static class W
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, ref RECT data, int size, out IntPtr written);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, out RECT data, int size, out IntPtr read);
}
