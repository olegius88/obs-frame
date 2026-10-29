// ObsFrame: Bandicam-style frame + control bar for OBS Studio.
// Draws a click-through border around the area OBS records (monitor capture crop),
// shows REC state/timer/size, and controls recording via the built-in obs-websocket (v5).
// The region can be moved (drag the bar) and resized (corner grip) while not recording.
// All overlay windows are excluded from screen capture (WDA_EXCLUDEFROMCAPTURE).
// C# 5 (built with .NET Framework csc), see build.cmd.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplayDevicesA(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    // Same id OBS stores in monitor_capture "monitor_id" (EnumDisplayDevicesA + EDD_GET_DEVICE_INTERFACE_NAME)
    public static string MonitorDeviceId(Screen s)
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(dd);
        return EnumDisplayDevicesA(s.DeviceName, 0, ref dd, 1) ? dd.DeviceID : "";
    }
}

// UI language: Russian when Windows UI is Russian, English otherwise; OBSFRAME_LANG=en|ru overrides
static class Loc
{
    public static readonly bool Ru = (Environment.GetEnvironmentVariable("OBSFRAME_LANG")
        ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "ru";
    public static string L(string en, string ru) { return Ru ? ru : en; }
}

class ObsError : Exception { public ObsError(string m) : base(m) { } }

// Minimal synchronous obs-websocket v5 client (https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md)
class ObsClient : IDisposable
{
    ClientWebSocket ws;
    readonly JavaScriptSerializer js = new JavaScriptSerializer();
    int nextId;

    public bool Connected { get { return ws != null && ws.State == WebSocketState.Open; } }

    public void Connect(int port, string password)
    {
        Dispose();
        ws = new ClientWebSocket();
        ws.Options.Proxy = null; // localhost: never go through a system/VPN proxy
        using (var cts = new CancellationTokenSource(3000))
            ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port), cts.Token).Wait();
        var hello = Recv();
        var d = (Dictionary<string, object>)hello["d"];
        var ident = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 0 } };
        if (d.ContainsKey("authentication"))
        {
            var a = (Dictionary<string, object>)d["authentication"];
            string secret = Hash(password + (string)a["salt"]);
            ident["authentication"] = Hash(secret + (string)a["challenge"]);
        }
        Send(1, ident);
        var r = Recv();
        if (Convert.ToInt32(r["op"]) != 2) throw new ObsError("identify failed");
    }

    static string Hash(string s)
    {
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
    }

    public Dictionary<string, object> Req(string type, Dictionary<string, object> data = null)
    {
        string id = (++nextId).ToString();
        var d = new Dictionary<string, object> { { "requestType", type }, { "requestId", id } };
        if (data != null) d["requestData"] = data;
        Send(6, d);
        while (true)
        {
            var m = Recv();
            if (Convert.ToInt32(m["op"]) != 7) continue;
            var rd = (Dictionary<string, object>)m["d"];
            if ((string)rd["requestId"] != id) continue;
            var st = (Dictionary<string, object>)rd["requestStatus"];
            if (!(bool)st["result"])
                throw new ObsError(type + ": " + (st.ContainsKey("comment") ? st["comment"] : st["code"]));
            return rd.ContainsKey("responseData") && rd["responseData"] != null
                ? (Dictionary<string, object>)rd["responseData"] : new Dictionary<string, object>();
        }
    }

    void Send(int op, object d)
    {
        var bytes = Encoding.UTF8.GetBytes(js.Serialize(new Dictionary<string, object> { { "op", op }, { "d", d } }));
        using (var cts = new CancellationTokenSource(5000))
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).Wait();
    }

    Dictionary<string, object> Recv()
    {
        var buf = new byte[65536];
        using (var ms = new MemoryStream())
        using (var cts = new CancellationTokenSource(5000))
        {
            WebSocketReceiveResult res;
            do
            {
                res = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result;
                if (res.MessageType == WebSocketMessageType.Close) throw new ObsError("closed by OBS");
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);
            return js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    public void Dispose()
    {
        if (ws != null) { try { ws.Abort(); ws.Dispose(); } catch { } ws = null; }
    }
}

// Where OBS records: monitor rectangle + crop of the monitor_capture item in the program scene
class RegionInfo
{
    public string Scene;
    public int ItemId;
    public Rectangle Monitor;
    public int SrcW, SrcH, L, T, R, B;
    public Rectangle ScreenRect
    {
        get { return new Rectangle(Monitor.X + L, Monitor.Y + T, Math.Max(1, SrcW - L - R), Math.Max(1, SrcH - T - B)); }
    }
}

class Status
{
    public bool ObsRunning, Connected, Active, Paused;
    public string Timecode = "00:00:00";
    public long Bytes;
    public string Error;
    public RegionInfo Region;
}

// Border window: transparent, click-through, topmost, excluded from capture
class FrameForm : Form
{
    public const int T = 3;
    public Color Color = Color.DodgerBlue;

    public FrameForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }
    public void SetRegion(Rectangle r)
    {
        var b = Rectangle.Inflate(r, T, T);
        if (Bounds != b) { Bounds = b; Invalidate(); }
    }
    public void SetColor(Color c)
    {
        if (Color != c) { Color = c; Invalidate(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using (var b = new SolidBrush(Color))
        {
            var g = e.Graphics;
            g.FillRectangle(b, 0, 0, Width, T);
            g.FillRectangle(b, 0, Height - T, Width, T);
            g.FillRectangle(b, 0, 0, T, Height);
            g.FillRectangle(b, Width - T, 0, T, Height);
        }
    }
}

// Small non-activating, capture-excluded tool window (bar and resize grip)
class ToolForm : Form
{
    public ToolForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8000000; // TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }
}

// Bandicam-style list of recordings in the OBS record folder
class ListForm : Form
{
    static readonly string[] Exts = { ".mp4", ".mkv", ".mov", ".flv", ".ts", ".m4v" };
    readonly ListView list = new ListView();
    readonly Label pathLbl = new Label();
    readonly FileSystemWatcher watcher = new FileSystemWatcher();
    readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 800 };
    string dir;

    public ListForm()
    {
        Text = Loc.L("OBS recordings", "Записи OBS");
        Size = new Size(820, 520);
        MinimumSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.LabelEdit = true;
        list.HideSelection = false;
        list.Columns.Add(Loc.L("File name", "Имя файла"), 330);
        list.Columns.Add(Loc.L("Date", "Дата"), 140);
        list.Columns.Add(Loc.L("Duration", "Длительность"), 100, HorizontalAlignment.Right);
        list.Columns.Add(Loc.L("Size", "Размер"), 100, HorizontalAlignment.Right);
        list.DoubleClick += delegate { Play(); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) Play();
            else if (e.KeyCode == Keys.Delete) Delete();
            else if (e.KeyCode == Keys.F2 && list.SelectedItems.Count == 1) list.SelectedItems[0].BeginEdit();
            else if (e.KeyCode == Keys.F5) Reload();
        };
        list.AfterLabelEdit += OnRename;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6, 6, 6, 0) };
        buttons.Controls.Add(Btn(Loc.L("▶ Play", "▶ Воспроизвести"), delegate { Play(); }));
        buttons.Controls.Add(Btn(Loc.L("📁 Show in folder", "📁 Показать в папке"), delegate { ShowInFolder(); }));
        buttons.Controls.Add(Btn(Loc.L("✎ Rename", "✎ Переименовать"), delegate { if (list.SelectedItems.Count == 1) list.SelectedItems[0].BeginEdit(); }));
        buttons.Controls.Add(Btn(Loc.L("🗑 Delete", "🗑 Удалить"), delegate { Delete(); }));
        buttons.Controls.Add(Btn(Loc.L("⟳ Refresh", "⟳ Обновить"), delegate { Reload(); }));

        pathLbl.Dock = DockStyle.Top;
        pathLbl.Height = 24;
        pathLbl.Padding = new Padding(6, 4, 6, 0);
        pathLbl.ForeColor = Color.DimGray;

        Controls.Add(list);
        Controls.Add(pathLbl);
        Controls.Add(buttons);

        debounce.Tick += delegate { debounce.Stop(); Reload(); };
        watcher.Created += OnFsChange;
        watcher.Deleted += OnFsChange;
        watcher.Renamed += (s, e) => OnFsChange(s, e);
        watcher.Changed += OnFsChange;
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
    }

    static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28 };
        b.Click += onClick;
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // keep the window object; just hide it
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    void OnFsChange(object s, FileSystemEventArgs e)
    {
        if (IsHandleCreated) BeginInvoke((Action)delegate { debounce.Stop(); debounce.Start(); });
    }

    public void ShowFor(string recordDir)
    {
        if (!string.IsNullOrEmpty(recordDir) && recordDir != dir && Directory.Exists(recordDir))
        {
            dir = recordDir;
            watcher.EnableRaisingEvents = false;
            watcher.Path = dir;
            watcher.EnableRaisingEvents = true;
        }
        pathLbl.Text = dir ?? Loc.L("OBS recording folder not found", "Папка записей OBS не найдена");
        Reload();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void Reload()
    {
        if (dir == null || !Directory.Exists(dir)) return;
        string selected = list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;
        var files = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(dir).GetFiles())
            if (Array.IndexOf(Exts, f.Extension.ToLowerInvariant()) >= 0) files.Add(f);
        files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var f in files)
        {
            var it = new ListViewItem(f.Name) { Tag = f.FullName };
            it.SubItems.Add(f.LastWriteTime.ToString("dd.MM.yyyy HH:mm"));
            it.SubItems.Add("…");
            it.SubItems.Add(FormatSize(f.Length));
            if (f.FullName == selected) it.Selected = true;
            list.Items.Add(it);
        }
        list.EndUpdate();
        Text = Loc.L("OBS recordings — ", "Записи OBS — ") + files.Count;

        // durations via the Windows shell property System.Media.Duration (100 ns units), off the UI thread
        var paths = files.ConvertAll(f => f.FullName);
        var t = new Thread(() =>
        {
            var res = new Dictionary<string, string>();
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic folder = shell.NameSpace(dir);
                foreach (var p in paths)
                {
                    try
                    {
                        dynamic item = folder.ParseName(Path.GetFileName(p));
                        object v = item == null ? null : item.ExtendedProperty("System.Media.Duration");
                        res[p] = v == null ? "—" : TimeSpan.FromTicks(Convert.ToInt64(v)).ToString(@"hh\:mm\:ss");
                    }
                    catch { res[p] = "—"; }
                }
            }
            catch { }
            if (IsHandleCreated)
                BeginInvoke((Action)delegate
                {
                    foreach (ListViewItem it in list.Items)
                    {
                        string d;
                        if (res.TryGetValue((string)it.Tag, out d)) it.SubItems[2].Text = d;
                    }
                });
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static string FormatSize(long b)
    {
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + Loc.L(" GB", " ГБ");
        return (b / (double)(1L << 20)).ToString("0.0") + Loc.L(" MB", " МБ");
    }

    string Selected()
    {
        return list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;
    }

    void Play()
    {
        var p = Selected();
        if (p != null) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    void ShowInFolder()
    {
        var p = Selected();
        if (p != null) Process.Start("explorer.exe", "/select,\"" + p + "\"");
        else if (dir != null) Process.Start("explorer.exe", "\"" + dir + "\"");
    }

    void Delete()
    {
        var paths = new List<string>();
        foreach (ListViewItem it in list.SelectedItems) paths.Add((string)it.Tag);
        if (paths.Count == 0) return;
        var msg = paths.Count == 1 ? Loc.L("Move to Recycle Bin: \"", "Переместить в корзину «") + Path.GetFileName(paths[0]) + Loc.L("\"?", "»?")
                                   : Loc.L("Move files to Recycle Bin: ", "Переместить в корзину файлов: ") + paths.Count + "?";
        if (MessageBox.Show(this, msg, Loc.L("Delete", "Удаление"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var p in paths)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Loc.L("Could not delete", "Не удалось удалить"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        Reload();
    }

    void OnRename(object s, LabelEditEventArgs e)
    {
        if (e.Label == null) return;
        var it = list.Items[e.Item];
        var oldPath = (string)it.Tag;
        var name = e.Label.Trim();
        if (Path.GetExtension(name) == "") name += Path.GetExtension(oldPath);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "")
        {
            e.CancelEdit = true;
            MessageBox.Show(this, Loc.L("Invalid file name.", "Недопустимое имя файла."), Loc.L("Rename", "Переименование"));
            return;
        }
        try
        {
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath), name);
            File.Move(oldPath, newPath);
            it.Tag = newPath;
            e.CancelEdit = true;   // set text ourselves (extension may have been appended)
            it.Text = name;
        }
        catch (Exception ex)
        {
            e.CancelEdit = true;
            MessageBox.Show(this, ex.Message, Loc.L("Could not rename", "Не удалось переименовать"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

class App : ApplicationContext
{
    static readonly Color Idle = Color.FromArgb(0, 140, 255), Rec = Color.FromArgb(235, 30, 30), Pause = Color.FromArgb(255, 190, 0);
    const int BarH = 34, BarW = 640, Grip = 18;

    readonly FrameForm frame = new FrameForm();
    readonly ToolForm bar = new ToolForm(), grip = new ToolForm();
    readonly Label dot = new Label(), info = new Label(), sizeLbl = new Label();
    readonly Button btnRec = new Button(), btnPause = new Button(), btnFolder = new Button(), btnList = new Button(), btnClose = new Button();
    readonly ListForm listForm = new ListForm();
    volatile string recordDir;
    readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
    readonly ConcurrentQueue<Action<ObsClient>> commands = new ConcurrentQueue<Action<ObsClient>>();

    Status status = new Status();
    readonly object statusLock = new object();
    volatile bool editing;           // user is dragging/resizing; worker must not overwrite region
    Rectangle editRect;              // region being edited (screen coords)
    Point dragStart; Rectangle dragOrig;
    volatile bool regionDirty = true;
    bool blink;

    public App()
    {
        BuildBar();
        BuildGrip();
        uiTimer.Interval = 250;
        uiTimer.Tick += delegate { RefreshUi(); };
        uiTimer.Start();
        var t = new Thread(Worker) { IsBackground = true, Name = "obs-worker" };
        t.Start();
    }

    Button MakeButton(Button b, string text, int w)
    {
        b.Text = text;
        b.Width = w;
        b.Height = BarH - 8;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        b.BackColor = Color.FromArgb(55, 55, 55);
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        b.Margin = new Padding(3, 4, 3, 4);
        b.TabStop = false;
        return b;
    }

    void BuildBar()
    {
        bar.BackColor = Color.FromArgb(32, 32, 32);
        bar.Height = BarH;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 0, 4, 0), BackColor = bar.BackColor };
        dot.Text = "●"; dot.AutoSize = true; dot.Font = new Font("Segoe UI", 13f); dot.Margin = new Padding(2, 3, 0, 0);
        info.AutoSize = true; info.ForeColor = Color.White; info.Font = new Font("Consolas", 11f, FontStyle.Bold); info.Margin = new Padding(0, 8, 6, 0);
        sizeLbl.AutoSize = true; sizeLbl.ForeColor = Color.Silver; sizeLbl.Font = new Font("Segoe UI", 8.5f); sizeLbl.Margin = new Padding(4, 10, 4, 0);
        MakeButton(btnRec, Loc.L("● Record", "● Запись"), 96);
        MakeButton(btnPause, Loc.L("❚❚ Pause", "❚❚ Пауза"), 110);
        MakeButton(btnFolder, "📁", 36);
        MakeButton(btnList, Loc.L("☰ Videos", "☰ Видео"), 84);
        MakeButton(btnClose, "✕", 30);
        btnList.Click += delegate { listForm.ShowFor(recordDir); };
        btnClose.Click += delegate { ExitThread(); };
        btnRec.Click += delegate
        {
            bool active; lock (statusLock) active = status.Active;
            commands.Enqueue(c => c.Req(active ? "StopRecord" : "StartRecord"));
        };
        btnPause.Click += delegate
        {
            bool paused; lock (statusLock) paused = status.Paused;
            commands.Enqueue(c => c.Req(paused ? "ResumeRecord" : "PauseRecord"));
        };
        btnFolder.Click += delegate
        {
            commands.Enqueue(c =>
            {
                var dir = (string)c.Req("GetRecordDirectory")["recordDirectory"];
                Process.Start("explorer.exe", "\"" + dir + "\"");
            });
        };
        var tip = new ToolTip();
        tip.SetToolTip(btnFolder, Loc.L("Open recordings folder", "Открыть папку с записями"));
        tip.SetToolTip(btnList, Loc.L("List of recorded videos", "Список сделанных видео"));
        tip.SetToolTip(btnClose, Loc.L("Close the frame (start again with ObsFrame.exe)", "Закрыть рамку (снова запустить: Пуск → «Рамка OBS»)"));
        tip.SetToolTip(info, Loc.L("Drag the bar to move the area (while not recording)", "Перетащите панель, чтобы сдвинуть область (когда запись не идёт)"));
        flow.Controls.AddRange(new Control[] { dot, info, btnRec, btnPause, btnList, btnFolder, sizeLbl, btnClose });
        bar.Controls.Add(flow);

        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.L("Exit frame", "Выход из рамки"), null, delegate { ExitThread(); });
        bar.ContextMenuStrip = menu;

        foreach (Control c in new Control[] { flow, dot, info, sizeLbl })
        {
            c.ContextMenuStrip = menu;
            c.MouseDown += BarMouseDown;
            c.MouseMove += BarMouseMove;
            c.MouseUp += EditMouseUp;
        }
    }

    void BuildGrip()
    {
        grip.Size = new Size(Grip, Grip);
        grip.BackColor = Idle;
        grip.Cursor = Cursors.SizeNWSE;
        grip.MouseDown += (s, e) => BeginEdit(e);
        grip.MouseMove += (s, e) =>
        {
            if (!editing) return;
            var p = Cursor.Position;
            editRect = new Rectangle(dragOrig.X, dragOrig.Y,
                Math.Max(64, dragOrig.Width + p.X - dragStart.X), Math.Max(64, dragOrig.Height + p.Y - dragStart.Y));
            Layout(editRect);
        };
        grip.MouseUp += EditMouseUp;
    }

    bool CanEdit()
    {
        lock (statusLock) return status.Connected && !status.Active && status.Region != null;
    }

    void BeginEdit(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !CanEdit()) return;
        lock (statusLock) editRect = status.Region.ScreenRect;
        dragOrig = editRect;
        dragStart = Cursor.Position;
        editing = true;
    }

    void BarMouseDown(object s, MouseEventArgs e) { BeginEdit(e); }

    void BarMouseMove(object s, MouseEventArgs e)
    {
        if (!editing) return;
        var p = Cursor.Position;
        editRect = new Rectangle(dragOrig.X + p.X - dragStart.X, dragOrig.Y + p.Y - dragStart.Y, dragOrig.Width, dragOrig.Height);
        Layout(editRect);
    }

    void EditMouseUp(object s, MouseEventArgs e)
    {
        if (!editing) return;
        var r = editRect;
        commands.Enqueue(c => { ApplyRegion(c, r); });
    }

    // Writes the new area to OBS: canvas/output size + crop of the monitor capture item
    void ApplyRegion(ObsClient c, Rectangle want)
    {
        try
        {
            RegionInfo ri; lock (statusLock) ri = status.Region;
            if (ri == null) return;
            var m = ri.Monitor;
            var r = Rectangle.Intersect(want, m);
            int w = r.Width - r.Width % 4, h = r.Height - r.Height % 2; // OBS wants even sizes; width kept a multiple of 4 like OBS did
            if (w < 64 || h < 64) return;
            int L = r.X - m.X, T = r.Y - m.Y, R = ri.SrcW - L - w, B = ri.SrcH - T - h;
            // obs-websocket limits canvas to 4096; scale larger areas down
            double k = Math.Min(1.0, Math.Min(4096.0 / w, 4096.0 / h));
            int cw = (int)(w * k) & ~3, ch = (int)(h * k) & ~1;
            c.Req("SetVideoSettings", new Dictionary<string, object> {
                { "baseWidth", cw }, { "baseHeight", ch }, { "outputWidth", cw }, { "outputHeight", ch } });
            c.Req("SetSceneItemTransform", new Dictionary<string, object> {
                { "sceneName", ri.Scene }, { "sceneItemId", ri.ItemId },
                { "sceneItemTransform", new Dictionary<string, object> {
                    { "cropLeft", L }, { "cropTop", T }, { "cropRight", R }, { "cropBottom", B },
                    { "positionX", 0 }, { "positionY", 0 }, { "alignment", 5 },
                    { "boundsType", "OBS_BOUNDS_SCALE_INNER" }, { "boundsAlignment", 0 },
                    { "boundsWidth", cw }, { "boundsHeight", ch } } } });
        }
        finally
        {
            regionDirty = true;
            editing = false;
        }
    }

    // Places frame, bar and grip around region r (screen coords)
    void Layout(Rectangle r)
    {
        frame.SetRegion(r);
        var scr = Screen.FromRectangle(r).Bounds;
        const int barW = BarW;
        int x = Math.Min(Math.Max(r.X - FrameForm.T, scr.X), scr.Right - barW);
        int y = r.Y - FrameForm.T - BarH;
        if (y < scr.Y) y = r.Bottom + FrameForm.T;              // no room above: below the frame
        if (y + BarH > scr.Bottom) y = r.Y + FrameForm.T;       // no room below either: inside (not recorded anyway)
        bar.Bounds = new Rectangle(x, y, barW, BarH);
        int gx = Math.Min(r.Right + FrameForm.T, scr.Right - Grip), gy = Math.Min(r.Bottom + FrameForm.T, scr.Bottom - Grip);
        grip.Location = new Point(gx, gy);
    }

    void RefreshUi()
    {
        Status s; lock (statusLock) s = status;
        if (!s.ObsRunning)
        {
            frame.Hide(); bar.Hide(); grip.Hide();
            return;
        }
        blink = !blink;
        Color col = !s.Connected ? Color.Gray : s.Paused ? Pause : s.Active ? Rec : Idle;
        frame.SetColor(col);
        if (!editing && s.Region != null) Layout(s.Region.ScreenRect);

        if (!s.Connected)
        {
            dot.ForeColor = Color.Gray;
            info.Text = s.Error ?? Loc.L("Connecting to OBS…", "Подключение к OBS…");
            btnRec.Enabled = btnPause.Enabled = btnFolder.Enabled = false;
            btnList.Enabled = recordDir != null;
        }
        else
        {
            dot.ForeColor = s.Active ? (s.Paused ? Pause : (blink ? Rec : Color.FromArgb(90, 20, 20))) : Color.Gray;
            string state = s.Active ? (s.Paused ? Loc.L("PAUSED ", "ПАУЗА ") : "REC ") : Loc.L("STOPPED ", "СТОП ");
            info.Text = state + s.Timecode + (s.Active ? "  " + FormatBytes(s.Bytes) : "");
            btnRec.Text = s.Active ? Loc.L("■ Stop", "■ Стоп") : Loc.L("● Record", "● Запись");
            btnRec.ForeColor = s.Active ? Color.White : Color.FromArgb(255, 90, 90);
            btnPause.Text = s.Paused ? Loc.L("▶ Resume", "▶ Продолжить") : Loc.L("❚❚ Pause", "❚❚ Пауза");
            btnRec.Enabled = btnFolder.Enabled = btnList.Enabled = true;
            btnPause.Enabled = s.Active;
        }
        var rr = editing ? editRect : (s.Region != null ? s.Region.ScreenRect : Rectangle.Empty);
        sizeLbl.Text = rr.IsEmpty ? "" : rr.Width + "×" + rr.Height;
        grip.BackColor = col;

        if (s.Region != null || editing)
        {
            if (!frame.Visible) frame.Show();
            if (!bar.Visible) bar.Show();
            bool g = s.Connected && !s.Active;
            if (g && !grip.Visible) grip.Show();
            if (!g && grip.Visible && !editing) grip.Hide();
        }
        else if (!bar.Visible)
        {
            var scr = Screen.PrimaryScreen.Bounds;
            bar.Bounds = new Rectangle(scr.X + 20, scr.Y + 20, BarW, BarH);
            bar.Show();
        }
        foreach (Form f in new Form[] { frame, bar, grip })
            if (f.Visible)
                Native.SetWindowPos(f.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    static string FormatBytes(long b)
    {
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + Loc.L(" GB", " ГБ");
        return (b / (double)(1L << 20)).ToString("0.0") + Loc.L(" MB", " МБ");
    }

    // ---------------- worker thread: talks to OBS ----------------

    void Worker()
    {
        var obs = new ObsClient();
        DateTime lastRegion = DateTime.MinValue;
        while (true)
        {
            var st = new Status();
            Status prev; lock (statusLock) prev = status;
            st.Region = prev.Region;
            st.ObsRunning = Process.GetProcessesByName("obs64").Length > 0;
            if (!st.ObsRunning)
            {
                obs.Dispose();
                st.Region = null;
                Publish(st);
                Thread.Sleep(1000);
                continue;
            }
            try
            {
                if (!obs.Connected)
                {
                    int port; string pass;
                    ReadWsConfig(out port, out pass);
                    obs.Connect(port, pass);
                    regionDirty = true;
                    recordDir = (string)obs.Req("GetRecordDirectory")["recordDirectory"];
                }
                Action<ObsClient> cmd;
                while (commands.TryDequeue(out cmd))
                {
                    try { cmd(obs); }
                    catch (ObsError e) { st.Error = e.Message; }
                }
                var rs = obs.Req("GetRecordStatus");
                st.Connected = true;
                st.Active = (bool)rs["outputActive"];
                st.Paused = rs.ContainsKey("outputPaused") && (bool)rs["outputPaused"];
                st.Timecode = ((string)rs["outputTimecode"] ?? "00:00:00").Split('.')[0];
                st.Bytes = Convert.ToInt64(rs["outputBytes"]);
                if (!editing && (regionDirty || (DateTime.Now - lastRegion).TotalSeconds > 2))
                {
                    regionDirty = false;
                    lastRegion = DateTime.Now;
                    st.Region = FetchRegion(obs);
                }
            }
            catch (Exception e)
            {
                obs.Dispose();
                st.Connected = false;
                var inner = e is AggregateException ? e.InnerException : e;
                st.Error = inner is ObsError ? "OBS: " + inner.Message : Loc.L("No connection to OBS, retrying…", "Нет связи с OBS, повтор…");
                Log(e.ToString());
                Publish(st);
                Thread.Sleep(1000);
                continue;
            }
            Publish(st);
            Thread.Sleep(250);
        }
    }

    void Publish(Status s) { lock (statusLock) status = s; }

    // Connection errors go to %LOCALAPPDATA%\ObsFrame\ObsFrame.log (kept small)
    static string lastLogged;
    static void Log(string text)
    {
        if (text == lastLogged) return;
        lastLogged = text;
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ObsFrame.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + Environment.NewLine);
        }
        catch { }
    }

    static void ReadWsConfig(out int port, out string pass)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"obs-studio\plugin_config\obs-websocket\config.json");
        var cfg = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        if (!cfg.ContainsKey("server_enabled") || !(bool)cfg["server_enabled"])
            throw new ObsError(Loc.L("enable Tools → WebSocket Server Settings", "включите Сервис → Настройки WebSocket-сервера"));
        port = cfg.ContainsKey("server_port") ? Convert.ToInt32(cfg["server_port"]) : 4455;
        pass = cfg.ContainsKey("server_password") ? (string)cfg["server_password"] : "";
    }

    static RegionInfo FetchRegion(ObsClient c)
    {
        var scene = (string)c.Req("GetCurrentProgramScene")["currentProgramSceneName"];
        var items = (System.Collections.ArrayList)c.Req("GetSceneItemList",
            new Dictionary<string, object> { { "sceneName", scene } })["sceneItems"];
        foreach (Dictionary<string, object> it in items)
        {
            if ((it["inputKind"] as string) != "monitor_capture") continue;
            var ri = new RegionInfo { Scene = scene, ItemId = Convert.ToInt32(it["sceneItemId"]) };
            var settings = (Dictionary<string, object>)c.Req("GetInputSettings",
                new Dictionary<string, object> { { "inputName", (string)it["sourceName"] } })["inputSettings"];
            string monId = settings.ContainsKey("monitor_id") ? (string)settings["monitor_id"] : "";
            ri.Monitor = Screen.PrimaryScreen.Bounds;
            foreach (var s in Screen.AllScreens)
                if (string.Equals(Native.MonitorDeviceId(s), monId, StringComparison.OrdinalIgnoreCase)) ri.Monitor = s.Bounds;
            var tr = (Dictionary<string, object>)c.Req("GetSceneItemTransform", new Dictionary<string, object> {
                { "sceneName", scene }, { "sceneItemId", ri.ItemId } })["sceneItemTransform"];
            ri.SrcW = Convert.ToInt32(tr["sourceWidth"]);
            ri.SrcH = Convert.ToInt32(tr["sourceHeight"]);
            if (ri.SrcW == 0 || ri.SrcH == 0) { ri.SrcW = ri.Monitor.Width; ri.SrcH = ri.Monitor.Height; }
            ri.L = Convert.ToInt32(tr["cropLeft"]); ri.T = Convert.ToInt32(tr["cropTop"]);
            ri.R = Convert.ToInt32(tr["cropRight"]); ri.B = Convert.ToInt32(tr["cropBottom"]);
            return ri;
        }
        return null;
    }

    [STAThread]
    static void Main(string[] args)
    {
        // --with-obs (desktop shortcut): also start OBS if it is not running yet.
        // Without the flag (autostart at login) only the frame runs and waits for OBS.
        if (Array.IndexOf(args, "--with-obs") >= 0 && Process.GetProcessesByName("obs64").Length == 0)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"obs-studio\bin\64bit");
            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, "obs64.exe")) { WorkingDirectory = dir }); }
            catch (Exception e) { MessageBox.Show(Loc.L("Could not start OBS: ", "Не удалось запустить OBS: ") + e.Message, "ObsFrame"); }
        }
        bool created;
        using (new Mutex(true, "ObsFrameOverlaySingleInstance", out created))
        {
            if (!created) return;
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2: physical pixels, same as OBS
            Application.EnableVisualStyles();
            Application.Run(new App());
        }
    }
}
