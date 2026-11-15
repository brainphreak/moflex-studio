// Moflex Studio -- the all-in-one SUPER MOFLEX builder (Windows GUI front end for app\moflex_studio.py)
// C# 5 / .NET Framework 4.5+ so it builds with the csc.exe every Windows install has (see build.bat).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MoflexStudio
{
    using Dict = Dictionary<string, object>;

    static class J
    {
        public static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        static JavaScriptSerializer _s;
        public static JavaScriptSerializer Ser
        {
            get { if (_s == null) { _s = new JavaScriptSerializer(); _s.MaxJsonLength = int.MaxValue; _s.RecursionLimit = 256; } return _s; }
        }
        public static object Parse(string s) { return Ser.DeserializeObject(s); }
        public static string Dump(object o) { return Ser.Serialize(o); }
        public static object G(object d, string k)
        {
            var dd = d as IDictionary<string, object>; object v;
            return dd != null && dd.TryGetValue(k, out v) ? v : null;
        }
        public static Dict D(object o) { return o as Dict; }
        public static string S(object o) { return o == null ? "" : Convert.ToString(o, CI); }
        public static int I(object o) { try { return o == null ? 0 : Convert.ToInt32(o, CI); } catch { return 0; } }
        public static double F(object o) { try { return o == null ? 0 : Convert.ToDouble(o, CI); } catch { return 0; } }
        public static bool B(object o) { return o is bool && (bool)o; }
        public static object[] A(object o)
        {
            var a = o as object[]; if (a != null) return a;
            var l = o as System.Collections.IEnumerable;
            if (l != null && !(o is string) && !(o is IDictionary<string, object>)) return l.Cast<object>().ToArray();
            return new object[0];
        }
        public static bool Has(object d, string k) { var dd = d as IDictionary<string, object>; return dd != null && dd.ContainsKey(k); }
        public static Dict Clone(Dict d) { return d == null ? null : (Dict)Parse(Dump(d)); }
    }

    class Item
    {
        public string Text; public object Tag;
        public Item(string t, object tag) { Text = t; Tag = tag; }
        public override string ToString() { return Text; }
    }

    class QItem
    {
        public Dict Job, Probe;
        public string Status = "Pending", Output = "", Error = "";
        public ListViewItem Lvi;
    }

    // ------------------------------------------------------------------ backend calls
    static class Backend
    {
        public static string AppDir, Python, Script;

        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2); sb.Append('"');
            return sb.ToString();
        }

        public static ProcessStartInfo Psi(params string[] args)
        {
            var psi = new ProcessStartInfo(Python, "-u " + Quote(Script) + " " + string.Join(" ", args.Select(Quote)));
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = AppDir;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            return psi;
        }

        // run a command that prints one JSON document; throws with the backend's message on failure
        public static Task<object> Call(params string[] args)
        {
            return Task.Run(() =>
            {
                using (var p = Process.Start(Psi(args)))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    string e = errTask.Result;
                    if (p.ExitCode != 0)
                    {
                        string msg = null;
                        foreach (var line in o.Split('\n').Reverse())
                        {
                            try { var d = J.Parse(line.Trim()); if (J.S(J.G(d, "type")) == "error") { msg = J.S(J.G(d, "message")); break; } }
                            catch { }
                        }
                        throw new Exception(msg ?? (e.Trim().Length > 0 ? e.Trim().Split('\n').Last() : "backend failed"));
                    }
                    return J.Parse(o);
                }
            });
        }
    }

    // ------------------------------------------------------------------ Windows helpers
    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        { public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        static IntPtr job = IntPtr.Zero;

        // every process the backend starts (ffmpeg, RunAsDate, the encoder) inherits the job; Windows kills
        // them all when this app exits for any reason, including a crash
        public static void KillWithApp(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        // the Mobiclip encoder pops up its own window even when started hidden; keep it out of sight
        public static void HideEncoderWindows()
        {
            var pids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("MobiclipMulticoreEncoder")) { pids.Add(p.Id); p.Dispose(); }
            if (pids.Count == 0) return;
            EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid) && IsWindowVisible(h)) ShowWindow(h, 0); return true; }, IntPtr.Zero);
        }
    }

    // ------------------------------------------------------------------ one audio track picker
    class AudioPicker
    {
        public GroupBox Box;
        ComboBox cmbMode, cmbStream;
        TextBox txtFile, txtLang;
        Button btnFile;
        NumericUpDown numOffset;
        Label lblLang;
        bool second;
        Dict otherProbe;
        public Func<string, Task<Dict>> ProbeFile;
        public Action<string> Status;
        public Dict SourceProbe;

        public AudioPicker(string title, bool isSecond, int x, int y)
        {
            second = isSecond;
            Box = new GroupBox { Text = title, Left = x, Top = y, Width = 576, Height = 118 };
            Box.Controls.Add(new Label { Text = "Source", Left = 10, Top = 24, Width = 58 });
            cmbMode = new ComboBox { Left = 70, Top = 20, Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
            lblLang = new Label { Text = second ? "Label" : "Language", Left = 412, Top = 24, Width = 60 };
            txtLang = new TextBox { Left = 474, Top = 20, Width = 50, CharacterCasing = CharacterCasing.Upper, MaxLength = 3 };
            Box.Controls.Add(new Label { Text = "File", Left = 10, Top = 55, Width = 58 });
            txtFile = new TextBox { Left = 70, Top = 51, Width = 420, ReadOnly = true };
            btnFile = new Button { Text = "...", Left = 494, Top = 50, Width = 30, Height = 23 };
            Box.Controls.Add(new Label { Text = "Stream", Left = 10, Top = 86, Width = 58 });
            cmbStream = new ComboBox { Left = 70, Top = 82, Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
            Box.Controls.Add(new Label { Text = "Offset s", Left = 412, Top = 86, Width = 60 });
            numOffset = new NumericUpDown { Left = 474, Top = 82, Width = 70, DecimalPlaces = 3, Minimum = -3600, Maximum = 3600, Increment = 0.1m };
            Box.Controls.AddRange(new Control[] { cmbMode, lblLang, txtLang, txtFile, btnFile, cmbStream, numOffset });
            cmbMode.SelectedIndexChanged += delegate { ModeChanged(); };
            cmbStream.SelectedIndexChanged += delegate { StreamChanged(); };
            btnFile.Click += async delegate { await PickFile(null); };
        }

        string Mode { get { var it = cmbMode.SelectedItem as Item; return it == null ? "" : (string)it.Tag; } }

        static string StreamText(object a)
        {
            string t = J.S(J.G(a, "title"));
            return string.Format("#{0}  {1}  {2} {3}ch{4}{5}", J.I(J.G(a, "index")), J.S(J.G(a, "lang")).ToUpper(),
                J.S(J.G(a, "codec")), J.I(J.G(a, "channels")), t.Length > 0 ? "  " + t : "", J.B(J.G(a, "default")) ? "  (default)" : "");
        }

        public void Setup(Dict probe)
        {
            SourceProbe = probe; otherProbe = null;
            cmbMode.Items.Clear(); txtFile.Text = ""; cmbStream.Items.Clear(); numOffset.Value = 0; txtLang.Text = "";
            string kind = J.S(J.G(probe, "kind"));
            if (probe == null) { ModeChanged(); return; }
            if (!second)
            {
                if (kind != "video") { cmbMode.Items.Add(new Item("Keep as it is", "keep")); cmbMode.Items.Add(new Item("Keep, but fix the loudness (normalize)", "normalize")); }
                else foreach (var a in J.A(J.G(probe, "audio"))) cmbMode.Items.Add(new Item("Source " + StreamText(a), "src:" + J.I(J.G(a, "index"))));
                cmbMode.Items.Add(new Item("Other file...", "file"));
                if (kind == "video") cmbMode.Items.Add(new Item("Silent (no audio)", "silent"));
            }
            else
            {
                cmbMode.Items.Add(new Item("None", "none"));
                var a2 = J.G(J.G(probe, "moflex"), "audio2");
                if (a2 != null)
                    cmbMode.Items.Add(new Item(string.Format("Keep the current track 2  ({0}, {1} Hz, {2}:{3:00})", J.S(J.G(a2, "lang")), J.I(J.G(a2, "rate")),
                        (int)J.F(J.G(a2, "seconds")) / 60, (int)J.F(J.G(a2, "seconds")) % 60), "keep"));
                if (kind == "video") foreach (var a in J.A(J.G(probe, "audio"))) cmbMode.Items.Add(new Item("Source " + StreamText(a), "src:" + J.I(J.G(a, "index"))));
                cmbMode.Items.Add(new Item("Other file...", "file"));
            }
            cmbMode.SelectedIndex = cmbMode.Items.Count > 0 ? 0 : -1;
            ModeChanged();
        }

        void SelectMode(string tag)
        {
            for (int i = 0; i < cmbMode.Items.Count; i++)
                if ((string)((Item)cmbMode.Items[i]).Tag == tag) { cmbMode.SelectedIndex = i; return; }
        }

        object FindStream(Dict probe, int idx)
        {
            return J.A(J.G(probe, "audio")).FirstOrDefault(a => J.I(J.G(a, "index")) == idx);
        }

        static string Lang3(string l) { l = (l ?? "").Trim(); return Regex.IsMatch(l, "^[A-Za-z][A-Za-z0-9]{1,2}$") ? l.ToUpper() : "UND"; }

        void ModeChanged()
        {
            string m = Mode;
            bool file = m == "file";
            txtFile.Enabled = btnFile.Enabled = cmbStream.Enabled = file;
            numOffset.Enabled = file || m.StartsWith("src:");
            txtLang.Enabled = m != "none" && m != "silent" && m != "normalize" && !(m == "keep" && !second);
            if (m.StartsWith("src:"))
            {
                var a = FindStream(SourceProbe, int.Parse(m.Substring(4)));
                txtLang.Text = Lang3(J.S(J.G(a, "lang")));
            }
            else if (m == "keep" && second)
                txtLang.Text = J.S(J.G(J.G(J.G(SourceProbe, "moflex"), "audio2"), "lang"));
            else if (m == "keep" || m == "normalize")
                txtLang.Text = J.S(J.G(J.G(SourceProbe, "moflex"), "lang_main"));
            else if (m == "none" || m == "silent") txtLang.Text = "";
            if (file) StreamChanged();
        }

        void StreamChanged()
        {
            var it = cmbStream.SelectedItem as Item;
            if (it != null) txtLang.Text = Lang3(J.S(J.G(it.Tag, "lang")));
        }

        public async Task PickFile(string path)
        {
            if (path == null)
            {
                using (var d = new OpenFileDialog { Title = "Audio source (any video or audio file)", Filter = "Media files|*.mkv;*.mp4;*.m4v;*.avi;*.mov;*.ts;*.m2ts;*.webm;*.wmv;*.mpg;*.mpeg;*.moflex;*.mka;*.m4a;*.aac;*.ac3;*.eac3;*.dts;*.flac;*.wav;*.mp3;*.ogg;*.opus|All files|*.*" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    path = d.FileName;
                }
            }
            txtFile.Text = path; cmbStream.Items.Clear();
            if (Status != null) Status("Reading " + Path.GetFileName(path) + "...");
            try
            {
                otherProbe = await ProbeFile(path);
                foreach (var a in J.A(J.G(otherProbe, "audio"))) cmbStream.Items.Add(new Item(StreamText(a), a));
                if (cmbStream.Items.Count > 0) cmbStream.SelectedIndex = 0;
                if (Status != null) Status(cmbStream.Items.Count == 0 ? "No audio streams in " + Path.GetFileName(path) : "");
            }
            catch (Exception ex) { if (Status != null) Status("Could not read " + Path.GetFileName(path) + ": " + ex.Message); }
        }

        public string Validate()
        {
            string m = Mode;
            if (m == "file" && (txtFile.Text == "" || cmbStream.SelectedItem == null)) return Box.Text + ": choose a file with an audio stream";
            if (txtLang.Enabled && txtLang.Text != "" && !Regex.IsMatch(txtLang.Text, "^[A-Za-z][A-Za-z0-9]{1,2}$"))
                return Box.Text + ": " + lblLang.Text.ToLower() + " must be 2-3 letters";
            return null;
        }

        public object ToJson()
        {
            string m = Mode; var d = new Dict();
            string lang = txtLang.Text.Trim().ToLower();
            if (m == "none" || m == "") return null;
            if (m == "silent") return d;
            if (m == "keep") { d["keep"] = true; if (second && lang != "") d["label"] = lang; return d; }
            if (m == "normalize") { d["normalize"] = true; return d; }
            if (m.StartsWith("src:")) { d["file"] = null; d["stream"] = int.Parse(m.Substring(4)); }
            else { d["file"] = txtFile.Text; d["stream"] = J.I(J.G(((Item)cmbStream.SelectedItem).Tag, "index")); }
            d["offset"] = (double)numOffset.Value;
            if (lang == "") lang = second ? "alt" : "eng";
            d["lang"] = lang;
            if (second) d["label"] = lang;
            return d;
        }

        public async Task FromJson(object o)
        {
            if (o == null) { SelectMode(second ? "none" : "silent"); return; }
            if (J.B(J.G(o, "keep"))) { SelectMode("keep"); if (J.Has(o, "label")) txtLang.Text = J.S(J.G(o, "label")).ToUpper(); return; }
            if (J.B(J.G(o, "normalize"))) { SelectMode("normalize"); return; }
            if (!J.Has(o, "stream")) { SelectMode("silent"); return; }
            string f = J.S(J.G(o, "file"));
            if (f == "") SelectMode("src:" + J.I(J.G(o, "stream")));
            else
            {
                SelectMode("file"); await PickFile(f);
                for (int i = 0; i < cmbStream.Items.Count; i++)
                    if (J.I(J.G(((Item)cmbStream.Items[i]).Tag, "index")) == J.I(J.G(o, "stream"))) cmbStream.SelectedIndex = i;
            }
            numOffset.Value = Math.Max(numOffset.Minimum, Math.Min(numOffset.Maximum, (decimal)J.F(J.G(o, "offset"))));
            string l = J.S(J.G(o, second ? "label" : "lang"));
            if (l != "") txtLang.Text = l.ToUpper();
        }

        public void Select(string tag, string lang) { SelectMode(tag); if (lang != null) txtLang.Text = lang; }
    }

    // ------------------------------------------------------------------ main window
    class MainForm : Form
    {
        static readonly string[] VideoExt = { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".m2ts", ".mts", ".webm", ".wmv", ".mpg", ".mpeg", ".flv", ".moflex" };
        const string PyUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";
        const string FfUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        string appDir, settingsPath, queuePath;
        Dict settings = new Dict();
        Dict probe;                         // the source loaded in the editor
        QItem editing;                      // queue item being edited (null = new)
        List<QItem> queue = new List<QItem>();
        QItem running; Process proc; bool stopRequested, queueActive;
        double jobDuration; DateTime stageStart;
        string currentArtMode = "none", artUrl = "", artFile = "";
        string origShowDesc = "";
        bool toolsReady;

        // editor controls
        TextBox txtSource; Label lblInfo; Button btnOpen;
        TabControl tabs;
        ComboBox cmbLayout; RadioButton rad3D, rad2D; NumericUpDown numBitrate; Label lblVideoNote;
        AudioPicker audio1, audio2;
        ListView lvSubs; ComboBox cmbSubFrom; TextBox txtSubLabel; NumericUpDown numSubOffset; Label lblSubCount;
        ComboBox cmbKind, cmbResults, cmbCategory, cmbFormat; TextBox txtQuery, txtTitle, txtEpTitle, txtGenres, txtYear, txtRuntime, txtDate, txtDesc;
        NumericUpDown numS, numE; PictureBox picArt; Label lblArt; Button btnArtKeep;
        TextBox txtOutDir, txtOutName; Label lblAutoName; CheckBox chkSuper, chkOverwrite;
        TextBox txtToken, txtDefOut; NumericUpDown numDefBitrate; Label lblTools; Button btnDownload;
        Button btnAdd, btnClear, btnCancelEdit; Label lblEditing;
        // queue + progress
        ListView lvQueue; Button btnStart, btnStop, btnEdit, btnRemove, btnUp, btnDown, btnClearDone;
        Label lblStage, lblDetail, lblStatus; ProgressBar pbStage, pbTools; TextBox txtLog;
        ToolTip tips = new ToolTip();

        public MainForm()
        {
            appDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "app");
            settingsPath = Path.Combine(appDir, "settings.json");
            queuePath = Path.Combine(appDir, "queue.json");
            Backend.AppDir = appDir;
            Backend.Script = Path.Combine(appDir, "moflex_studio.py");
            Text = "Moflex Studio";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(984, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            LoadSettings();
            ClearEditor();
            LoadQueue();
            Shown += async delegate
            {
                CleanStaleWork();
                await CheckTools(true);
                // files dropped on the exe: one -> editor, several -> queue; --queue forces queueing, --start runs the queue
                var args = Environment.GetCommandLineArgs().Skip(1).ToList();
                bool start = args.Remove("--start"), toQ = args.Remove("--queue");
                if (args.Count > 0 && toolsReady) await OnDrop(args.ToArray(), toQ);
                if (start && toolsReady) StartQueue();
            };
            FormClosing += OnClosing;
            AllowDrop = true;
            HookDrop(this);
        }

        // ============================================================ UI construction
        Label L(Control parent, string text, int x, int y, int w = 80)
        {
            var l = new Label { Text = text, Left = x, Top = y + 3, Width = w, AutoEllipsis = true };
            parent.Controls.Add(l); return l;
        }
        T Add<T>(Control parent, T c) where T : Control { parent.Controls.Add(c); return c; }
        Label Note(Control parent, string text, int x, int y, int w, int h)
        {
            var l = new Label { Text = text, Left = x, Top = y, Width = w, Height = h, ForeColor = SystemColors.GrayText };
            parent.Controls.Add(l); return l;
        }

        void BuildUi()
        {
            // ---- source row
            L(this, "Source", 8, 9, 50);
            txtSource = Add(this, new TextBox { Left = 60, Top = 9, Width = 486, ReadOnly = true, BackColor = SystemColors.Window });
            btnOpen = Add(this, new Button { Text = "Open...", Left = 550, Top = 8, Width = 66, Height = 25 });
            btnOpen.Click += async delegate
            {
                using (var d = new OpenFileDialog { Title = "Video, moflex or SUPER MOFLEX", Filter = "Video / moflex|*" + string.Join(";*", VideoExt) + "|All files|*.*" })
                    if (d.ShowDialog() == DialogResult.OK) await LoadSource(d.FileName, null);
            };
            lblInfo = Note(this, "Open or drop a video file, moflex or SUPER MOFLEX (drop several to queue them all).", 60, 36, 556, 17);

            tabs = Add(this, new TabControl { Left = 8, Top = 56, Width = 608, Height = 444 });
            var tVideo = new TabPage("Video"); var tAudio = new TabPage("Audio"); var tSubs = new TabPage("Subtitles");
            var tInfo = new TabPage("Info + poster"); var tOut = new TabPage("Output"); var tSet = new TabPage("Settings");
            tabs.TabPages.AddRange(new[] { tVideo, tAudio, tSubs, tInfo, tOut, tSet });

            // ---- Video
            L(tVideo, "Source layout", 10, 14, 100);
            cmbLayout = Add(tVideo, new ComboBox { Left = 112, Top = 14, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList });
            L(tVideo, "Output", 10, 46, 100);
            rad3D = Add(tVideo, new RadioButton { Text = "3D", Left = 112, Top = 46, Width = 50, Checked = true });
            rad2D = Add(tVideo, new RadioButton { Text = "2D", Left = 166, Top = 46, Width = 50 });
            L(tVideo, "Bitrate", 10, 78, 100);
            numBitrate = Add(tVideo, new NumericUpDown { Left = 112, Top = 78, Width = 80, Minimum = 200, Maximum = 8000, Increment = 100, Value = 1400 });
            L(tVideo, "kbps", 196, 78, 40);
            lblVideoNote = Note(tVideo, "", 10, 116, 576, 280);
            rad3D.CheckedChanged += delegate { UpdateVideoNote(); UpdateAutoName(); };
            cmbLayout.SelectedIndexChanged += delegate { UpdateVideoNote(); };

            // ---- Audio
            audio1 = new AudioPicker("Track 1  (in the video stream, plays everywhere)", false, 8, 8);
            audio2 = new AudioPicker("Track 2  (SUPER MOFLEX trailer, CLOWNSEC player only)", true, 8, 134);
            foreach (var ap in new[] { audio1, audio2 })
            {
                ap.ProbeFile = ProbeDict; ap.Status = SetStatus;
                tAudio.Controls.Add(ap.Box);
            }
            Note(tAudio, "Track 1 is always loudness-normalised (EBU R128, -16 LUFS) when it comes from a file or stream, and so is track 2. " +
                "Use the offset to shift an audio file that starts earlier (negative) or later (positive) than the video. " +
                "The label is what the player shows for track 2 (e.g. JPN, COM, DUB).", 10, 262, 576, 60);

            // ---- Subtitles
            lvSubs = Add(tSubs, new ListView { Left = 8, Top = 8, Width = 496, Height = 268, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false });
            lvSubs.Columns.Add("#", 28); lvSubs.Columns.Add("Label", 50); lvSubs.Columns.Add("From", 340); lvSubs.Columns.Add("Offset", 60);
            var bUp = Add(tSubs, new Button { Text = "Up", Left = 510, Top = 8, Width = 78 });
            var bDn = Add(tSubs, new Button { Text = "Down", Left = 510, Top = 38, Width = 78 });
            var bRm = Add(tSubs, new Button { Text = "Remove", Left = 510, Top = 68, Width = 78 });
            lblSubCount = Note(tSubs, "0 / 16", 512, 100, 76, 18);
            Note(tSubs, "The first track is the default.", 512, 122, 76, 60);
            L(tSubs, "Add from", 8, 286, 60);
            cmbSubFrom = Add(tSubs, new ComboBox { Left = 70, Top = 286, Width = 434, DropDownStyle = ComboBoxStyle.DropDownList });
            L(tSubs, "Label", 8, 318, 60);
            txtSubLabel = Add(tSubs, new TextBox { Left = 70, Top = 318, Width = 50, CharacterCasing = CharacterCasing.Upper, MaxLength = 3 });
            L(tSubs, "Offset s", 130, 318, 56);
            numSubOffset = Add(tSubs, new NumericUpDown { Left = 188, Top = 318, Width = 76, DecimalPlaces = 3, Minimum = -3600, Maximum = 3600, Increment = 0.1m });
            var bAddSub = Add(tSubs, new Button { Text = "Add", Left = 274, Top = 317, Width = 76, Height = 25 });
            var bUpdSub = Add(tSubs, new Button { Text = "Update selected", Left = 356, Top = 317, Width = 148, Height = 25 });
            Note(tSubs, "Text subtitles only (SRT, ASS/SSA; ASS styling is kept). Image subtitles (PGS/VobSub) need OCR first. " +
                "Each track must be under 1 MiB. A label like ENG, SPA, SDH or FRC (forced) is what the player shows.", 8, 352, 576, 50);
            bUp.Click += delegate { MoveSub(-1); }; bDn.Click += delegate { MoveSub(1); };
            bRm.Click += delegate { if (lvSubs.SelectedItems.Count > 0) { lvSubs.Items.Remove(lvSubs.SelectedItems[0]); RenumberSubs(); } };
            bAddSub.Click += delegate { AddSubFromUi(); };
            bUpdSub.Click += delegate { UpdateSelectedSub(); };
            cmbSubFrom.SelectedIndexChanged += delegate
            {
                var it = cmbSubFrom.SelectedItem as Item; var t = it == null ? null : it.Tag as Dict;
                if (t != null && J.Has(t, "label")) txtSubLabel.Text = J.S(t["label"]).ToUpper();
            };
            lvSubs.SelectedIndexChanged += delegate
            {
                if (lvSubs.SelectedItems.Count == 0) return;
                var d = (Dict)lvSubs.SelectedItems[0].Tag;
                txtSubLabel.Text = J.S(J.G(d, "label")).ToUpper();
                numSubOffset.Value = (decimal)J.F(J.G(d, "offset"));
            };

            // ---- Info + poster
            cmbKind = Add(tInfo, new ComboBox { Left = 8, Top = 8, Width = 92, DropDownStyle = ComboBoxStyle.DropDownList });
            cmbKind.Items.AddRange(new object[] { "Movie", "TV episode" }); cmbKind.SelectedIndex = 0;
            txtQuery = Add(tInfo, new TextBox { Left = 104, Top = 8, Width = 236 });
            L(tInfo, "S", 346, 8, 14);
            numS = Add(tInfo, new NumericUpDown { Left = 362, Top = 8, Width = 44, Minimum = 0, Maximum = 99 });
            L(tInfo, "E", 410, 8, 14);
            numE = Add(tInfo, new NumericUpDown { Left = 426, Top = 8, Width = 50, Minimum = 0, Maximum = 999 });
            var bSearch = Add(tInfo, new Button { Text = "Search TMDB", Left = 482, Top = 7, Width = 106, Height = 25 });
            cmbResults = Add(tInfo, new ComboBox { Left = 8, Top = 38, Width = 580, DropDownStyle = ComboBoxStyle.DropDownList });
            int ly = 70;
            L(tInfo, "Title", 8, ly, 62); txtTitle = Add(tInfo, new TextBox { Left = 72, Top = ly, Width = 362 });
            L(tInfo, "Episode", 8, ly + 28, 62); txtEpTitle = Add(tInfo, new TextBox { Left = 72, Top = ly + 28, Width = 362 });
            L(tInfo, "Genres", 8, ly + 56, 62); txtGenres = Add(tInfo, new TextBox { Left = 72, Top = ly + 56, Width = 362 });
            L(tInfo, "Year", 8, ly + 84, 62); txtYear = Add(tInfo, new TextBox { Left = 72, Top = ly + 84, Width = 50 });
            L(tInfo, "Minutes", 130, ly + 84, 52); txtRuntime = Add(tInfo, new TextBox { Left = 184, Top = ly + 84, Width = 44 });
            L(tInfo, "Date", 236, ly + 84, 34); txtDate = Add(tInfo, new TextBox { Left = 272, Top = ly + 84, Width = 162 });
            L(tInfo, "Category", 8, ly + 112, 62);
            cmbCategory = Add(tInfo, new ComboBox { Left = 72, Top = ly + 112, Width = 150 });
            cmbCategory.Items.AddRange(new object[] { "Movies", "TV Shows", "Music Videos", "Concerts", "Shorts" });
            L(tInfo, "Format", 236, ly + 112, 46);
            cmbFormat = Add(tInfo, new ComboBox { Left = 284, Top = ly + 112, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList });
            cmbFormat.Items.AddRange(new object[] { "converted", "native" }); cmbFormat.SelectedIndex = 0;
            L(tInfo, "Description", 8, ly + 140, 80);
            txtDesc = Add(tInfo, new TextBox { Left = 8, Top = ly + 160, Width = 426, Height = 128, Multiline = true, ScrollBars = ScrollBars.Vertical });
            Note(tInfo, "Leave the title empty to keep the file's current info.", 8, ly + 292, 426, 18);
            picArt = Add(tInfo, new PictureBox { Left = 448, Top = ly, Width = 132, Height = 188, BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.StretchImage, BackColor = Color.FromArgb(40, 40, 40) });
            var bImg = Add(tInfo, new Button { Text = "Image...", Left = 448, Top = ly + 194, Width = 64 });
            var bNone = Add(tInfo, new Button { Text = "None", Left = 516, Top = ly + 194, Width = 64 });
            btnArtKeep = Add(tInfo, new Button { Text = "Keep original", Left = 448, Top = ly + 222, Width = 132 });
            lblArt = Note(tInfo, "", 448, ly + 252, 140, 58);
            bSearch.Click += async delegate { await TmdbSearch(); };
            txtQuery.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await TmdbSearch(); } };
            cmbResults.SelectedIndexChanged += async delegate { await TmdbPick(); };
            cmbKind.SelectedIndexChanged += delegate { bool tv = cmbKind.SelectedIndex == 1; numS.Enabled = numE.Enabled = txtEpTitle.Enabled = tv; UpdateAutoName(); };
            bImg.Click += delegate
            {
                using (var d = new OpenFileDialog { Title = "Poster image", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|All files|*.*" })
                    if (d.ShowDialog() == DialogResult.OK) SetArt("file", d.FileName);
            };
            bNone.Click += delegate { SetArt("none", null); };
            btnArtKeep.Click += async delegate { await ShowOriginalArt(); };
            foreach (var c in new Control[] { txtTitle, txtEpTitle, txtYear }) c.TextChanged += delegate { UpdateAutoName(); };
            numS.ValueChanged += delegate { UpdateAutoName(); }; numE.ValueChanged += delegate { UpdateAutoName(); };

            // ---- Output
            L(tOut, "Folder", 10, 14, 62);
            txtOutDir = Add(tOut, new TextBox { Left = 74, Top = 14, Width = 470 });
            var bOut = Add(tOut, new Button { Text = "...", Left = 548, Top = 13, Width = 34, Height = 25 });
            Note(tOut, "Empty = next to the source file.", 74, 40, 500, 18);
            L(tOut, "File name", 10, 64, 62);
            txtOutName = Add(tOut, new TextBox { Left = 74, Top = 64, Width = 508 });
            lblAutoName = Note(tOut, "", 74, 90, 508, 32);
            chkSuper = Add(tOut, new CheckBox { Text = "SUPER MOFLEX: add the trailer (info, poster, language, subtitles, track 2)", Left = 10, Top = 130, Width = 572, Checked = true });
            Note(tOut, "Unticked = a plain moflex with only the in-band audio track (any player).", 28, 152, 554, 18);
            chkOverwrite = Add(tOut, new CheckBox { Text = "When editing a moflex in place, replace the original file", Left = 10, Top = 180, Width = 572 });
            Note(tOut, "Unticked = it is saved next to it as \"... (edited).moflex\" if the name would be the same.", 28, 202, 554, 18);
            bOut.Click += delegate { var p = PickFolder(txtOutDir.Text); if (p != null) txtOutDir.Text = p; };
            txtOutName.TextChanged += delegate { UpdateAutoName(); };
            chkSuper.CheckedChanged += delegate { UpdateAutoName(); };

            // ---- Settings
            L(tSet, "TMDB token", 10, 14, 96);
            txtToken = Add(tSet, new TextBox { Left = 108, Top = 14, Width = 400, UseSystemPasswordChar = true });
            var chkShow = Add(tSet, new CheckBox { Text = "Show", Left = 516, Top = 15, Width = 70 });
            chkShow.CheckedChanged += delegate { txtToken.UseSystemPasswordChar = !chkShow.Checked; };
            Note(tSet, "The API Read Access Token from themoviedb.org (Settings > API). Only needed for the Search TMDB button.", 108, 40, 478, 32);
            L(tSet, "Bitrate", 10, 80, 96);
            numDefBitrate = Add(tSet, new NumericUpDown { Left = 108, Top = 80, Width = 80, Minimum = 200, Maximum = 8000, Increment = 100, Value = 1400 });
            L(tSet, "kbps default", 192, 80, 100);
            L(tSet, "Output folder", 10, 112, 96);
            txtDefOut = Add(tSet, new TextBox { Left = 108, Top = 112, Width = 436 });
            var bDefOut = Add(tSet, new Button { Text = "...", Left = 548, Top = 111, Width = 34, Height = 25 });
            bDefOut.Click += delegate { var p = PickFolder(txtDefOut.Text); if (p != null) txtDefOut.Text = p; };
            var bSave = Add(tSet, new Button { Text = "Save settings", Left = 108, Top = 146, Width = 120, Height = 27 });
            bSave.Click += delegate { SaveSettings(); SetStatus("Settings saved."); };
            var grp = Add(tSet, new GroupBox { Text = "Tools", Left = 8, Top = 190, Width = 578, Height = 210 });
            lblTools = new Label { Left = 12, Top = 22, Width = 556, Height = 120, Font = new Font("Consolas", 9f) };
            grp.Controls.Add(lblTools);
            btnDownload = new Button { Text = "Download missing tools", Left = 12, Top = 150, Width = 180, Height = 27 };
            var bAppDir = new Button { Text = "Open app folder", Left = 198, Top = 150, Width = 130, Height = 27 };
            grp.Controls.Add(btnDownload); grp.Controls.Add(bAppDir);
            btnDownload.Click += async delegate { await CheckTools(true); };
            bAppDir.Click += delegate { Process.Start("explorer.exe", Backend.Quote(appDir)); };

            // ---- editor buttons
            btnAdd = Add(this, new Button { Text = "Add to queue", Left = 8, Top = 506, Width = 140, Height = 30, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
            btnClear = Add(this, new Button { Text = "Clear", Left = 154, Top = 506, Width = 80, Height = 30 });
            btnCancelEdit = Add(this, new Button { Text = "Cancel edit", Left = 240, Top = 506, Width = 96, Height = 30, Visible = false });
            lblEditing = Note(this, "", 344, 513, 272, 18);
            btnAdd.Click += delegate { AddToQueue(); };
            btnClear.Click += delegate { ClearEditor(); };
            btnCancelEdit.Click += delegate { ClearEditor(); };

            // ---- queue
            lvQueue = Add(this, new ListView { Left = 628, Top = 8, Width = 348, Height = 290, View = View.Details, FullRowSelect = true, HideSelection = false, AllowDrop = true });
            lvQueue.Columns.Add("Queue", 210); lvQueue.Columns.Add("Status", 116);
            lvQueue.ShowItemToolTips = true;
            btnEdit = Add(this, new Button { Text = "Edit", Left = 628, Top = 304, Width = 62 });
            btnRemove = Add(this, new Button { Text = "Remove", Left = 694, Top = 304, Width = 66 });
            btnUp = Add(this, new Button { Text = "Up", Left = 764, Top = 304, Width = 48 });
            btnDown = Add(this, new Button { Text = "Down", Left = 816, Top = 304, Width = 52 });
            btnClearDone = Add(this, new Button { Text = "Clear finished", Left = 872, Top = 304, Width = 104 });
            btnStart = Add(this, new Button { Text = "Start queue", Left = 628, Top = 336, Width = 170, Height = 32, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
            btnStop = Add(this, new Button { Text = "Stop", Left = 804, Top = 336, Width = 172, Height = 32, Enabled = false });
            lblStage = Add(this, new Label { Left = 628, Top = 376, Width = 348, Height = 18, Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoEllipsis = true, Text = "Idle" });
            pbStage = Add(this, new ProgressBar { Left = 628, Top = 396, Width = 348, Height = 18 });
            lblDetail = Note(this, "", 628, 418, 348, 18); lblDetail.AutoEllipsis = true;
            txtLog = Add(this, new TextBox { Left = 628, Top = 440, Width = 348, Height = 130, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8f), BackColor = SystemColors.Window, WordWrap = true });
            btnEdit.Click += async delegate { await EditSelected(); };
            lvQueue.DoubleClick += async delegate
            {
                var q = SelectedQ(); if (q == null) return;
                if (q.Status.StartsWith("Done") && File.Exists(q.Output)) Process.Start("explorer.exe", "/select," + Backend.Quote(q.Output));
                else await EditSelected();
            };
            btnRemove.Click += delegate { RemoveSelected(); };
            lvQueue.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            btnUp.Click += delegate { MoveQ(-1); }; btnDown.Click += delegate { MoveQ(1); };
            btnClearDone.Click += delegate
            {
                foreach (var q in queue.Where(x => x.Status.StartsWith("Done")).ToList()) { queue.Remove(q); lvQueue.Items.Remove(q.Lvi); }
                SaveQueue();
            };
            btnStart.Click += delegate { StartQueue(); };
            btnStop.Click += delegate { StopQueue(); };

            // ---- status bar
            lblStatus = Add(this, new Label { Left = 8, Top = 578, Width = 780, Height = 18, AutoEllipsis = true, ForeColor = SystemColors.GrayText });
            pbTools = Add(this, new ProgressBar { Left = 796, Top = 578, Width = 180, Height = 16, Visible = false });
        }

        void SetStatus(string s) { lblStatus.Text = s ?? ""; }

        // work folders left behind by a crash or power cut (only when no other copy of the app is running)
        void CleanStaleWork()
        {
            try
            {
                if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1) return;
                long freed = 0;
                foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "moflex_studio_*"))
                {
                    try
                    {
                        freed += new DirectoryInfo(d).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                        Directory.Delete(d, true);
                    }
                    catch { }
                }
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "moflex_studio_*")) try { File.Delete(f); } catch { }
                if (freed > 0) Log(string.Format("Removed {0:0.0} GB of leftover work files from an earlier run.", freed / 1073741824.0));
            }
            catch { }
        }

        void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += async (s, e) =>
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                await OnDrop(files, c == lvQueue);
            };
            foreach (Control k in c.Controls)
                if (!(k is TextBox && ((TextBox)k).Multiline)) HookDrop(k);
        }

        string PickFolder(string start)
        {
            using (var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(start) ? start : "" })
                return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
        }

        // ============================================================ settings + tools
        void LoadSettings()
        {
            try { if (File.Exists(settingsPath)) settings = (Dict)J.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)); }
            catch (Exception ex) { SetStatus("settings.json could not be read: " + ex.Message); }
            txtToken.Text = J.S(J.G(settings, "tmdb_token"));
            int br = J.I(J.G(settings, "default_bitrate")); if (br >= 200) numDefBitrate.Value = Math.Min(8000, br);
            txtDefOut.Text = J.S(J.G(settings, "default_output"));
        }

        void SaveSettings()
        {
            settings["tmdb_token"] = txtToken.Text.Trim();
            settings["default_bitrate"] = (int)numDefBitrate.Value;
            settings["default_output"] = txtDefOut.Text.Trim();
            File.WriteAllText(settingsPath, J.Dump(settings), new UTF8Encoding(false));
        }

        string ToolPath(string key, string def)
        {
            string v = J.S(J.G(settings, key));
            return v != "" ? v : def;
        }

        void FindPython()
        {
            string emb = Path.Combine(appDir, "python", "python.exe");
            string cfg = J.S(J.G(settings, "python"));
            Backend.Python = cfg != "" ? cfg : emb;
        }

        async Task CheckTools(bool download)
        {
            FindPython();
            string bin = ToolPath("ffmpeg_dir", Path.Combine(appDir, "bin"));
            bool py = File.Exists(Backend.Python);
            bool ff = File.Exists(Path.Combine(bin, "ffmpeg.exe")) && File.Exists(Path.Combine(bin, "ffprobe.exe"));
            if (download && (!py || !ff))
            {
                btnDownload.Enabled = false;
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
                    catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                    if (!py) { await Fetch(PyUrl, "Python 3.11 (embeddable)", Path.Combine(appDir, "python"), null); }
                    if (!ff) { await Fetch(FfUrl, "ffmpeg (gyan.dev release essentials)", Path.Combine(appDir, "bin"), new[] { "ffmpeg.exe", "ffprobe.exe" }); }
                    SetStatus("Tools downloaded.");
                }
                catch (Exception ex) { SetStatus("Download failed: " + ex.Message + "  (Settings > Download missing tools to retry)"); }
                finally { btnDownload.Enabled = true; pbTools.Visible = false; }
                await CheckTools(false);
                return;
            }
            string enc = Path.Combine(ToolPath("encoder_dir", Path.Combine(appDir, "encoder")), "MobiclipMulticoreEncoder.exe");
            string rad = ToolPath("runasdate", Path.Combine(appDir, "RunAsDate.exe"));
            string lic = ToolPath("license", Path.Combine(appDir, "User_CTR.lic"));
            Func<bool, string> ok = b => b ? "OK     " : "MISSING";
            Func<string, string> rel = x => x.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) ? "app" + x.Substring(appDir.Length) : x;
            lblTools.Text = string.Format("{0}  Python      {1}\n{2}  ffmpeg      {3}\n{4}  Mobiclip    {5}\n{6}  RunAsDate   {7}\n{8}  license     {9}",
                ok(py), rel(Backend.Python), ok(ff), rel(bin), ok(File.Exists(enc)), rel(enc), ok(File.Exists(rad)), rel(rad), ok(File.Exists(lic)), rel(lic)) +
                (File.Exists(enc) ? "" : "\n\nWithout the encoder, video files can't be converted (moflex editing still works).");
            toolsReady = py && ff;
            btnAdd.Enabled = btnStart.Enabled = btnOpen.Enabled = toolsReady && !queueActive;
            if (!toolsReady) { SetStatus("Python / ffmpeg missing: Settings > Download missing tools."); tabs.SelectedIndex = 5; }
            else if (lblStatus.Text == "" || lblStatus.Text.StartsWith("Tools")) SetStatus("Ready. " + (File.Exists(enc) ? "" : "(Mobiclip encoder missing: only moflex editing works.)"));
        }

        async Task Fetch(string url, string what, string destDir, string[] only)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "moflex_studio_dl_" + Guid.NewGuid().ToString("N") + ".zip");
            pbTools.Visible = true; pbTools.Value = 0;
            SetStatus("Downloading " + what + "...");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MoflexStudio/1.0";
                wc.DownloadProgressChanged += (s, e) =>
                {
                    pbTools.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                    SetStatus(string.Format("Downloading {0}... {1:0.0} / {2:0.0} MB", what, e.BytesReceived / 1048576.0, e.TotalBytesToReceive / 1048576.0));
                };
                await wc.DownloadFileTaskAsync(new Uri(url), tmp);
            }
            SetStatus("Unpacking " + what + "...");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                using (var z = ZipFile.OpenRead(tmp))
                    foreach (var e in z.Entries)
                    {
                        if (e.Name == "") continue;
                        if (only != null) { if (only.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) e.ExtractToFile(Path.Combine(destDir, e.Name), true); }
                        else
                        {
                            string p = Path.Combine(destDir, e.FullName.Replace('/', '\\'));
                            Directory.CreateDirectory(Path.GetDirectoryName(p));
                            e.ExtractToFile(p, true);
                        }
                    }
                File.Delete(tmp);
            });
        }

        // ============================================================ loading a source
        async Task<Dict> ProbeDict(string path)
        {
            var r = await Backend.Call("probe", path);
            return (Dict)r;
        }

        static bool IsMedia(string p) { return VideoExt.Contains(Path.GetExtension(p).ToLowerInvariant()); }

        static IEnumerable<string> Expand(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.GetFiles(p, "*", SearchOption.AllDirectories).Where(IsMedia).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return f;
                }
                else if (File.Exists(p)) yield return p;
            }
        }

        async Task OnDrop(string[] paths, bool toQueue)
        {
            if (!toolsReady) { SetStatus("Wait for the tools to finish downloading."); return; }
            var files = Expand(paths).ToList();
            if (files.Count == 0) return;
            if (files.Count == 1 && !toQueue) { await LoadSource(files[0], null); return; }
            int n = 0;
            foreach (var f in files)
            {
                SetStatus(string.Format("Queueing {0} of {1}: {2}", ++n, files.Count, Path.GetFileName(f)));
                try
                {
                    var pr = await ProbeDict(f);
                    var job = DefaultJob(pr);
                    AddQueueItem(new QItem { Job = job, Probe = pr });
                }
                catch (Exception ex) { Log("! " + Path.GetFileName(f) + ": " + ex.Message); }
            }
            SaveQueue();
            SetStatus(string.Format("Queued {0} file(s) with the current Video/Output settings.", n));
        }

        // the job a freshly dropped file gets (also what the editor starts from)
        Dict DefaultJob(Dict pr)
        {
            var job = new Dict();
            string kind = J.S(J.G(pr, "kind"));
            string src = J.S(J.G(pr, "path"));
            job["source"] = src;
            job["source_type"] = "auto";
            job["output"] = kind == "video" ? (rad2D.Checked ? "2d" : "3d") : (J.B(J.G(J.G(pr, "moflex"), "is3d")) ? "3d" : "2d");
            if (kind == "video" && J.S(J.G(J.G(pr, "video"), "detected")) == "2d") job["output"] = "2d";
            job["bitrate"] = (int)numBitrate.Value;
            var aud = new Dict(); job["audio"] = aud;
            var parsed = J.G(pr, "parsed");
            if (kind == "video")
            {
                var streams = J.A(J.G(pr, "audio"));
                var main = streams.FirstOrDefault(a => J.S(J.G(a, "lang")).StartsWith("en")) ?? streams.FirstOrDefault(a => J.B(J.G(a, "default"))) ?? streams.FirstOrDefault();
                if (main != null)
                {
                    string ml = J.S(J.G(main, "lang")); if (ml == "und" || ml == "") ml = "eng";
                    aud["main"] = new Dict { { "file", null }, { "stream", J.I(J.G(main, "index")) }, { "offset", 0.0 }, { "lang", ml.Substring(0, Math.Min(3, ml.Length)) } };
                    string mlang = J.S(J.G(main, "lang"));
                    var sec = streams.FirstOrDefault(a => a != main && J.S(J.G(a, "lang")) != mlang && J.S(J.G(a, "lang")) != "und" && J.S(J.G(a, "lang")) != "");
                    if (sec != null)
                    {
                        string sl = J.S(J.G(sec, "lang")); sl = sl.Substring(0, Math.Min(3, sl.Length));
                        aud["second"] = new Dict { { "file", null }, { "stream", J.I(J.G(sec, "index")) }, { "offset", 0.0 }, { "lang", sl }, { "label", sl } };
                    }
                    else aud["second"] = null;
                }
                else aud["main"] = new Dict();
                var subs = new List<object>();
                foreach (var s in J.A(J.G(pr, "subtitles")))
                {
                    if (J.B(J.G(s, "image")) || subs.Count >= 16) continue;
                    string l = SubLabel(J.S(J.G(s, "lang")), J.S(J.G(s, "title")), J.B(J.G(s, "forced")));
                    subs.Add(new Dict { { "file", null }, { "stream", J.I(J.G(s, "index")) }, { "label", l }, { "offset", 0.0 },
                        { "desc", string.Format("Source stream #{0} {1} {2}", J.I(J.G(s, "index")), J.S(J.G(s, "codec")), J.S(J.G(s, "title"))).Trim() } });
                }
                foreach (var f in Sidecars(src))
                {
                    if (subs.Count >= 16) break;
                    subs.Add(new Dict { { "file", f }, { "label", LabelFromName(f) }, { "offset", 0.0 }, { "desc", "File " + Path.GetFileName(f) } });
                }
                job["subtitles"] = subs.ToArray();
                job["art"] = null;
                string title = J.S(J.G(parsed, "title"));
                var md = new Dict();
                md["title"] = title; md["year"] = J.I(J.G(parsed, "year"));
                var se = J.A(J.G(parsed, "season_episode"));
                md["season_episode"] = se.Length == 2 ? new object[] { J.I(se[0]), J.I(se[1]) } : null;
                md["category"] = se.Length == 2 ? "TV Shows" : "Movies";
                md["format"] = "converted";
                job["metadata"] = md;
            }
            else
            {
                var mf = J.G(pr, "moflex");
                aud["main"] = new Dict { { "keep", true } };
                aud["second"] = J.G(mf, "audio2") != null ? (object)new Dict { { "keep", true } } : null;
                var subs = new List<object>();
                foreach (var s in J.A(J.G(mf, "subs")))
                    subs.Add(new Dict { { "existing", J.I(J.G(s, "existing")) }, { "label", J.S(J.G(s, "label")) }, { "offset", 0.0 },
                        { "desc", string.Format("Current track {0} ({1}, {2} cues)", J.I(J.G(s, "existing")) + 1, J.S(J.G(s, "format")), J.I(J.G(s, "cues"))) } });
                job["subtitles"] = subs.ToArray();
                job["art"] = J.B(J.G(mf, "art")) ? "keep" : null;
                var nfo = J.G(mf, "nfo");
                if (J.S(J.G(nfo, "title")) != "")
                {
                    var md = new Dict();
                    foreach (var k in new[] { "title", "genres", "date", "desc", "showdesc", "category", "format" }) md[k] = J.S(J.G(nfo, k));
                    md["year"] = J.I(J.G(nfo, "year")); md["runtime"] = J.I(J.G(nfo, "runtime"));
                    var se = J.A(J.G(parsed, "season_episode"));
                    md["season_episode"] = se.Length == 2 ? new object[] { J.I(se[0]), J.I(se[1]) } : null;
                    var m = Regex.Match(Path.GetFileNameWithoutExtension(src), @"S\d+e\d+ - (.+)$", RegexOptions.IgnoreCase);
                    md["eptitle"] = m.Success ? m.Groups[1].Value : "";
                    md["_from_file"] = true;
                    job["metadata"] = md;
                }
                else
                {
                    var md = new Dict { { "title", "" } };
                    job["metadata"] = md;
                }
            }
            job["super"] = true;
            job["output_dir"] = txtDefOut.Text.Trim();
            job["output_name"] = "";
            job["overwrite"] = false;
            return job;
        }

        static string SubLabel(string lang, string title, bool forced)
        {
            string t = title.ToLowerInvariant();
            if (t.Contains("sdh") || t.Contains("hearing")) return "sdh";
            if (forced || t.Contains("forced")) return "frc";
            if (t.Contains("comment")) return "com";
            lang = lang.ToLowerInvariant();
            if (Regex.IsMatch(lang, "^[a-z][a-z0-9]{1,2}$") && lang != "und") return lang;
            return "eng";
        }

        static string LabelFromName(string f)
        {
            var m = Regex.Match(Path.GetFileName(f), @"\.([A-Za-z][A-Za-z0-9]{1,2})\.(srt|ass|ssa)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            if (Regex.IsMatch(f, "sdh", RegexOptions.IgnoreCase)) return "sdh";
            if (Regex.IsMatch(f, "forced", RegexOptions.IgnoreCase)) return "frc";
            return "eng";
        }

        static IEnumerable<string> Sidecars(string src)
        {
            string dir = Path.GetDirectoryName(src), b = Path.GetFileNameWithoutExtension(src);
            if (dir == null || !Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir).Where(f =>
            {
                string n = Path.GetFileName(f), e = Path.GetExtension(f).ToLowerInvariant();
                return (e == ".srt" || e == ".ass" || e == ".ssa") && n.StartsWith(b, StringComparison.OrdinalIgnoreCase);
            }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        async Task LoadSource(string path, Dict job)
        {
            SetStatus("Reading " + Path.GetFileName(path) + "...");
            Dict pr;
            try { pr = await ProbeDict(path); }
            catch (Exception ex) { SetStatus("Could not read " + Path.GetFileName(path) + ": " + ex.Message); return; }
            await LoadIntoEditor(pr, job ?? DefaultJob(pr));
            SetStatus("");
        }

        async Task LoadIntoEditor(Dict pr, Dict job)
        {
            probe = pr;
            string kind = J.S(J.G(pr, "kind"));
            txtSource.Text = J.S(J.G(pr, "path"));
            var v = J.G(pr, "video");
            double dur = J.F(J.G(pr, "duration"));
            string durs = dur > 0 ? string.Format("{0}:{1:00}:{2:00}", (int)dur / 3600, (int)dur / 60 % 60, (int)dur % 60) : "";
            if (kind == "video")
                lblInfo.Text = string.Format("Video  {0}x{1}  {2:0.###} fps  {3}  {4} audio  {5} subtitles  detected: {6}", J.I(J.G(v, "width")), J.I(J.G(v, "height")),
                    J.F(J.G(v, "fps")), durs, J.A(J.G(pr, "audio")).Length, J.A(J.G(pr, "subtitles")).Length, LayoutName(J.S(J.G(v, "detected"))));
            else
            {
                var mf = J.G(pr, "moflex");
                lblInfo.Text = kind == "super"
                    ? string.Format("SUPER MOFLEX  {0}  track 1 {1}{2}  {3} subtitles{4}", J.B(J.G(mf, "is3d")) ? "3D" : "2D", J.S(J.G(mf, "lang_main")) == "" ? "?" : J.S(J.G(mf, "lang_main")),
                        J.G(mf, "audio2") != null ? ", track 2 " + J.S(J.G(J.G(mf, "audio2"), "lang")) : "", J.A(J.G(mf, "subs")).Length, J.B(J.G(mf, "art")) ? "  poster" : "")
                    : string.Format("Plain moflex  {0}  (video kept as is: add info, poster, subtitles and audio)", J.B(J.G(mf, "is3d")) ? "3D" : "2D");
            }

            // video tab
            cmbLayout.Items.Clear();
            if (kind == "video")
            {
                cmbLayout.Items.Add(new Item("Auto (detected: " + LayoutName(J.S(J.G(v, "detected"))) + ")", "auto"));
                cmbLayout.Items.Add(new Item("2D", "2d"));
                cmbLayout.Items.Add(new Item("Half side-by-side (HSBS)", "hsbs"));
                cmbLayout.Items.Add(new Item("Full side-by-side (FSBS)", "fsbs"));
                SelectItem(cmbLayout, J.S(J.G(job, "source_type")));
            }
            else { cmbLayout.Items.Add(new Item("moflex (video is not re-encoded)", "auto")); cmbLayout.SelectedIndex = 0; }
            cmbLayout.Enabled = numBitrate.Enabled = kind == "video";
            rad3D.Checked = J.S(J.G(job, "output")) != "2d"; rad2D.Checked = !rad3D.Checked;
            int br = J.I(J.G(job, "bitrate")); numBitrate.Value = br >= 200 ? Math.Min(8000, br) : numDefBitrate.Value;

            // audio
            audio1.Setup(pr); audio2.Setup(pr);
            await audio1.FromJson(J.G(J.G(job, "audio"), "main"));
            await audio2.FromJson(J.G(J.G(job, "audio"), "second"));

            // subtitles
            cmbSubFrom.Items.Clear();
            if (kind == "super")
                foreach (var s in J.A(J.G(J.G(pr, "moflex"), "subs")))
                    cmbSubFrom.Items.Add(new Item(string.Format("Current track {0}: {1} ({2}, {3} cues)", J.I(J.G(s, "existing")) + 1, J.S(J.G(s, "label")).ToUpper(), J.S(J.G(s, "format")), J.I(J.G(s, "cues"))),
                        new Dict { { "existing", J.I(J.G(s, "existing")) }, { "label", J.S(J.G(s, "label")) }, { "desc", string.Format("Current track {0} ({1}, {2} cues)", J.I(J.G(s, "existing")) + 1, J.S(J.G(s, "format")), J.I(J.G(s, "cues"))) } }));
            if (kind == "video")
                foreach (var s in J.A(J.G(pr, "subtitles")))
                {
                    bool img = J.B(J.G(s, "image"));
                    string txt = string.Format("Source stream #{0}: {1} {2} {3}{4}", J.I(J.G(s, "index")), J.S(J.G(s, "lang")).ToUpper(), J.S(J.G(s, "codec")), J.S(J.G(s, "title")), img ? "  (image: needs OCR)" : "");
                    cmbSubFrom.Items.Add(new Item(txt, img ? null : new Dict { { "file", null }, { "stream", J.I(J.G(s, "index")) },
                        { "label", SubLabel(J.S(J.G(s, "lang")), J.S(J.G(s, "title")), J.B(J.G(s, "forced"))) },
                        { "desc", string.Format("Source stream #{0} {1} {2}", J.I(J.G(s, "index")), J.S(J.G(s, "codec")), J.S(J.G(s, "title"))).Trim() } }));
                }
            cmbSubFrom.Items.Add(new Item("Subtitle file (.srt / .ass / .ssa)...", "file"));
            cmbSubFrom.Items.Add(new Item("Text subtitle stream in another video file...", "otherfile"));
            cmbSubFrom.SelectedIndex = 0;
            lvSubs.Items.Clear();
            foreach (var s in J.A(J.G(job, "subtitles"))) AddSubRow((Dict)s);
            RenumberSubs();

            // info
            var md = J.D(J.G(job, "metadata")) ?? new Dict();
            txtTitle.Text = J.S(J.G(md, "title"));
            txtEpTitle.Text = J.S(J.G(md, "eptitle"));
            txtGenres.Text = J.S(J.G(md, "genres"));
            int y = J.I(J.G(md, "year")); txtYear.Text = y > 0 ? y.ToString() : "";
            int rt = J.I(J.G(md, "runtime")); txtRuntime.Text = rt > 0 ? rt.ToString() : "";
            txtDate.Text = J.S(J.G(md, "date"));
            txtDesc.Text = J.S(J.G(md, "desc")).Replace("\r", "").Replace("\n", "\r\n");
            origShowDesc = J.S(J.G(md, "showdesc"));
            cmbCategory.Text = J.S(J.G(md, "category")) == "" ? "Movies" : J.S(J.G(md, "category"));
            cmbFormat.SelectedIndex = J.S(J.G(md, "format")) == "native" ? 1 : 0;
            var se = J.A(J.G(md, "season_episode"));
            cmbKind.SelectedIndex = se.Length == 2 ? 1 : 0;
            numS.Value = se.Length == 2 ? Math.Min(99, J.I(se[0])) : 0; numE.Value = se.Length == 2 ? Math.Min(999, J.I(se[1])) : 0;
            txtQuery.Text = txtTitle.Text != "" ? txtTitle.Text : J.S(J.G(J.G(pr, "parsed"), "title"));
            cmbResults.Items.Clear();
            btnArtKeep.Enabled = J.B(J.G(J.G(pr, "moflex"), "art"));
            object art = J.G(job, "art");
            string a = J.S(art);
            if (a == "keep") await ShowOriginalArt();
            else if (a == "" || a == "none") SetArt("none", null);
            else if (a.StartsWith("http")) SetArt("url", a);
            else SetArt("file", a);

            // output
            txtOutDir.Text = J.S(J.G(job, "output_dir"));
            txtOutName.Text = J.S(J.G(job, "output_name"));
            chkSuper.Checked = !J.Has(job, "super") || J.B(J.G(job, "super"));
            chkOverwrite.Checked = J.B(J.G(job, "overwrite"));
            chkOverwrite.Enabled = kind != "video";
            UpdateVideoNote(); UpdateAutoName();
        }

        static string LayoutName(string d) { return d == "fsbs" ? "full SBS" : d == "hsbs" ? "half SBS" : d == "2d" ? "2D" : d; }

        static void SelectItem(ComboBox c, string tag)
        {
            for (int i = 0; i < c.Items.Count; i++) if (J.S(((Item)c.Items[i]).Tag) == tag) { c.SelectedIndex = i; return; }
            if (c.Items.Count > 0) c.SelectedIndex = 0;
        }

        void UpdateVideoNote()
        {
            string kind = J.S(J.G(probe, "kind"));
            if (probe == null) { lblVideoNote.Text = ""; return; }
            if (kind != "video")
            {
                lblVideoNote.Text = "This is already a moflex, so the picture is kept exactly as it is (no re-encode, takes seconds).\n\n" +
                    "Output 3D / 2D only sets the 3D flag the CLOWNSEC player reads. It doesn't change the video. " +
                    "It's pre-set from the file (" + (J.B(J.G(J.G(probe, "moflex"), "is3d")) ? "3D" : "2D") + ").";
                return;
            }
            var it = cmbLayout.SelectedItem as Item;
            string lay = it == null ? "auto" : (string)it.Tag;
            if (lay == "auto") lay = J.S(J.G(J.G(probe, "video"), "detected"));
            string s = "Each eye is scaled into 400x240 and letterboxed (black bars), never stretched; the aspect ratio is kept. " +
                "Sources above 30 fps are halved.\n\n";
            if (rad3D.Checked && lay == "2d") s += "3D output needs a side-by-side source. Set the layout to HSBS or FSBS, or choose 2D output.";
            else if (rad3D.Checked) s += "3D: the left and right halves become the two eyes (lossless 800x240 AVI, then the official Mobiclip encoder, 2 passes).";
            else if (lay != "2d") s += "2D from a side-by-side source: only the left eye is used.";
            else s += "2D: lossless 400x240 AVI, then the official Mobiclip encoder (2 passes).";
            lblVideoNote.Text = s;
        }

        // ---- subtitles tab
        void AddSubRow(Dict d)
        {
            var lvi = new ListViewItem(new[] { "", J.S(J.G(d, "label")).ToUpper(), J.S(J.G(d, "desc")), J.F(J.G(d, "offset")) == 0 ? "" : J.F(J.G(d, "offset")).ToString("0.###", J.CI) });
            lvi.Tag = d; lvSubs.Items.Add(lvi);
        }

        void RenumberSubs()
        {
            for (int i = 0; i < lvSubs.Items.Count; i++) lvSubs.Items[i].Text = (i + 1).ToString();
            lblSubCount.Text = lvSubs.Items.Count + " / 16";
            lblSubCount.ForeColor = lvSubs.Items.Count > 16 ? Color.Firebrick : SystemColors.GrayText;
        }

        void MoveSub(int dir)
        {
            if (lvSubs.SelectedItems.Count == 0) return;
            var it = lvSubs.SelectedItems[0]; int i = it.Index, j = i + dir;
            if (j < 0 || j >= lvSubs.Items.Count) return;
            lvSubs.Items.RemoveAt(i); lvSubs.Items.Insert(j, it); it.Selected = true; RenumberSubs();
        }

        string SubLabelInput(string fallback)
        {
            string l = txtSubLabel.Text.Trim().ToLower();
            if (l == "") l = fallback;
            if (!Regex.IsMatch(l, "^[a-z][a-z0-9]{1,2}$")) { SetStatus("Subtitle label must be 2-3 letters/digits starting with a letter."); return null; }
            return l;
        }

        async void AddSubFromUi()
        {
            var it = cmbSubFrom.SelectedItem as Item;
            if (it == null) return;
            if (it.Tag == null) { SetStatus("That subtitle stream is image-based (PGS/VobSub). OCR it to .srt first, then add the file."); return; }
            if (lvSubs.Items.Count >= 16) { SetStatus("The player shows at most 16 subtitle tracks."); return; }
            if (J.S(it.Tag) == "file")
            {
                using (var d = new OpenFileDialog { Title = "Subtitle files", Multiselect = true, Filter = "Subtitles|*.srt;*.ass;*.ssa|All files|*.*" })
                {
                    if (probe != null) try { d.InitialDirectory = Path.GetDirectoryName(J.S(J.G(probe, "path"))); } catch { }
                    if (d.ShowDialog() != DialogResult.OK) return;
                    foreach (var f in d.FileNames)
                    {
                        if (lvSubs.Items.Count >= 16) break;
                        string l = d.FileNames.Length == 1 ? SubLabelInput(LabelFromName(f)) : LabelFromName(f);
                        if (l == null) return;
                        AddSubRow(new Dict { { "file", f }, { "label", l }, { "offset", (double)numSubOffset.Value }, { "desc", "File " + Path.GetFileName(f) } });
                    }
                }
            }
            else if (J.S(it.Tag) == "otherfile")
            {
                string f;
                using (var d = new OpenFileDialog { Title = "Video file with a text subtitle stream", Filter = "Video|*.mkv;*.mp4;*.m4v;*.mov;*.ts;*.m2ts|All files|*.*" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    f = d.FileName;
                }
                try
                {
                    var pr = await ProbeDict(f);
                    int added = 0;
                    foreach (var s in J.A(J.G(pr, "subtitles")))
                    {
                        string txt = string.Format("{0} stream #{1}: {2} {3} {4}", Path.GetFileName(f), J.I(J.G(s, "index")), J.S(J.G(s, "lang")).ToUpper(), J.S(J.G(s, "codec")), J.S(J.G(s, "title")));
                        if (J.B(J.G(s, "image"))) continue;
                        cmbSubFrom.Items.Insert(cmbSubFrom.Items.Count - 2, new Item(txt, new Dict { { "file", f }, { "stream", J.I(J.G(s, "index")) },
                            { "label", SubLabel(J.S(J.G(s, "lang")), J.S(J.G(s, "title")), J.B(J.G(s, "forced"))) }, { "desc", txt } }));
                        added++;
                    }
                    SetStatus(added == 0 ? "No text subtitle streams in " + Path.GetFileName(f) : added + " stream(s) from " + Path.GetFileName(f) + " are now in the Add from list.");
                    if (added > 0) { cmbSubFrom.SelectedIndex = cmbSubFrom.Items.Count - 2 - added; cmbSubFrom.DroppedDown = true; }
                }
                catch (Exception ex) { SetStatus("Could not read " + Path.GetFileName(f) + ": " + ex.Message); }
                return;
            }
            else
            {
                var d = J.Clone((Dict)it.Tag);
                string l = SubLabelInput(J.S(J.G(d, "label")));
                if (l == null) return;
                d["label"] = l; d["offset"] = (double)numSubOffset.Value;
                AddSubRow(d);
            }
            RenumberSubs();
        }

        void UpdateSelectedSub()
        {
            if (lvSubs.SelectedItems.Count == 0) { SetStatus("Select a subtitle track first."); return; }
            var it = lvSubs.SelectedItems[0]; var d = (Dict)it.Tag;
            string l = SubLabelInput(J.S(J.G(d, "label"))); if (l == null) return;
            d["label"] = l; d["offset"] = (double)numSubOffset.Value;
            if (J.Has(d, "existing") && (double)numSubOffset.Value != 0) { d["offset"] = 0.0; SetStatus("Offsets can't be applied to a track the file already has; re-add it from a file to shift it."); }
            it.SubItems[1].Text = l.ToUpper(); it.SubItems[3].Text = J.F(d["offset"]) == 0 ? "" : J.F(d["offset"]).ToString("0.###", J.CI);
        }

        // ---- info tab
        async Task TmdbSearch()
        {
            if (txtToken.Text.Trim() == "") { SetStatus("Add your TMDB token in Settings first."); tabs.SelectedIndex = 5; return; }
            if (J.S(J.G(settings, "tmdb_token")) != txtToken.Text.Trim()) SaveSettings();
            string q = txtQuery.Text.Trim(); if (q == "") return;
            string year = "";
            var ym = Regex.Match(q, @"\s*\((\d{4})\)\s*$");
            if (ym.Success) { year = ym.Groups[1].Value; q = q.Substring(0, ym.Index); }
            SetStatus("Searching TMDB for " + q + "...");
            try
            {
                var r = J.A(await Backend.Call("tmdb-search", cmbKind.SelectedIndex == 1 ? "tv" : "movie", q, year == "" ? txtYear.Text.Trim() : year));
                if (r.Length == 0 && year == "" && txtYear.Text.Trim() != "")
                    r = J.A(await Backend.Call("tmdb-search", cmbKind.SelectedIndex == 1 ? "tv" : "movie", q));
                cmbResults.Items.Clear();
                foreach (var x in r)
                {
                    string ov = J.S(J.G(x, "overview")); if (ov.Length > 70) ov = ov.Substring(0, 70) + "...";
                    cmbResults.Items.Add(new Item(string.Format("{0} ({1})  {2}", J.S(J.G(x, "title")), J.S(J.G(x, "year")), ov), x));
                }
                SetStatus(r.Length == 0 ? "No TMDB results." : r.Length + " result(s): pick one.");
                if (r.Length > 0) cmbResults.DroppedDown = true;
            }
            catch (Exception ex) { SetStatus("TMDB: " + ex.Message); }
        }

        async Task TmdbPick()
        {
            var it = cmbResults.SelectedItem as Item; if (it == null) return;
            bool tv = cmbKind.SelectedIndex == 1;
            var args = new List<string> { "tmdb-details", tv ? "tv" : "movie", J.S(J.G(it.Tag, "id")) };
            if (tv && numS.Value > 0 && numE.Value > 0) args.Add(numS.Value + ":" + numE.Value);
            SetStatus("Getting TMDB details...");
            try
            {
                var d = await Backend.Call(args.ToArray());
                txtTitle.Text = J.S(J.G(d, "title"));
                int y = J.I(J.G(d, "year")); txtYear.Text = y > 0 ? y.ToString() : "";
                txtGenres.Text = J.S(J.G(d, "genres"));
                int rt = J.I(J.G(d, "runtime")); txtRuntime.Text = rt > 0 ? rt.ToString() : "";
                txtDate.Text = J.S(J.G(d, "date"));
                txtDesc.Text = J.S(J.G(d, "desc"));
                txtEpTitle.Text = J.S(J.G(d, "eptitle"));
                origShowDesc = J.S(J.G(d, "showdesc"));
                cmbCategory.Text = J.S(J.G(d, "category"));
                string pu = J.S(J.G(d, "poster_url"));
                if (pu != "" && currentArtMode == "none") SetArt("url", pu);
                else if (pu != "") { artUrl = pu; lblArt.Text = "TMDB poster available: press None, then pick the result again to use it."; }
                SetStatus("Filled from TMDB." + (tv && (numS.Value == 0 || numE.Value == 0) ? " Set S and E, then pick the show again for the episode title." : ""));
            }
            catch (Exception ex) { SetStatus("TMDB: " + ex.Message); }
        }

        void SetArt(string mode, string value)
        {
            currentArtMode = mode;
            var old = picArt.Image; picArt.Image = null; if (old != null) old.Dispose();
            if (mode == "none") { artFile = artUrl = ""; lblArt.Text = "No poster."; }
            else if (mode == "file")
            {
                artFile = value; lblArt.Text = "From file: " + Path.GetFileName(value);
                try { using (var fs = File.OpenRead(value)) using (var im = Image.FromStream(fs)) picArt.Image = new Bitmap(im); }
                catch { lblArt.Text += " (no preview)"; }
            }
            else if (mode == "url")
            {
                artUrl = value; lblArt.Text = "TMDB poster (downloaded at build time).";
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; } catch { }
                    picArt.LoadAsync(value);
                }
                catch { }
            }
            else if (mode == "keep") lblArt.Text = "The poster the file already has.";
        }

        async Task ShowOriginalArt()
        {
            if (probe == null || !J.B(J.G(J.G(probe, "moflex"), "art"))) { SetArt("none", null); return; }
            SetArt("keep", null);
            string png = Path.Combine(Path.GetTempPath(), "moflex_studio_art_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await Backend.Call("export-art", J.S(J.G(probe, "path")), png);
                using (var fs = File.OpenRead(png)) using (var im = Image.FromStream(fs)) picArt.Image = new Bitmap(im);
                File.Delete(png);
            }
            catch (Exception ex) { lblArt.Text += " (preview failed: " + ex.Message + ")"; }
        }

        // ---- output naming preview (mirrors library_name in moflex_studio.py)
        static string FatClean(string n)
        {
            n = n.Replace("/", " - ").Replace(":", " -").Replace("*", "").Replace("?", "").Replace("\"", "'").Replace("<", "").Replace(">", "").Replace("|", "").Replace("\\", " ");
            return Regex.Replace(n, @"\s+", " ").Trim();
        }

        string AutoName()
        {
            if (probe == null) return "";
            string kind = J.S(J.G(probe, "kind")), src = J.S(J.G(probe, "path"));
            bool is3d = rad3D.Checked;
            if (txtOutName.Text.Trim() != "") { string n = FatClean(txtOutName.Text.Trim()); return n.EndsWith(".moflex", StringComparison.OrdinalIgnoreCase) ? n : n + ".moflex"; }
            if (txtTitle.Text.Trim() != "" && chkSuper.Checked)
            {
                string t = txtTitle.Text.Trim(), y = txtYear.Text.Trim() == "" ? "0" : txtYear.Text.Trim(), tag = is3d ? " (3D)" : "";
                if (cmbKind.SelectedIndex == 1 && numS.Value > 0 && numE.Value > 0)
                {
                    string p = string.Format("{0} ({1}){2} (S) - S{3:00}e{4:00}", t, y, tag, (int)numS.Value, (int)numE.Value);
                    string ep = txtEpTitle.Text.Trim();
                    return FatClean(p + (ep != "" ? " - " + ep : "")) + ".moflex";
                }
                return FatClean(string.Format("{0} ({1}){2} (S)", t, y, tag)) + ".moflex";
            }
            if (kind == "video") return Path.GetFileNameWithoutExtension(src) + (is3d ? "_3D" : "_2D") + ".moflex";
            return Path.GetFileName(src);
        }

        void UpdateAutoName()
        {
            if (lblAutoName == null) return;
            string n = AutoName();
            lblAutoName.Text = n == "" ? "" : (txtOutName.Text.Trim() == "" ? "Automatic: " : "Will be saved as: ") + n;
        }

        // ============================================================ editor <-> job
        void ClearEditor()
        {
            probe = null; editing = null;
            txtSource.Text = "";
            lblInfo.Text = "Open or drop a video file, moflex or SUPER MOFLEX (drop several, or a folder, to queue them all).";
            cmbLayout.Items.Clear(); cmbLayout.Enabled = false;
            numBitrate.Value = numDefBitrate.Value; numBitrate.Enabled = false;
            audio1.Setup(null); audio2.Setup(null);
            lvSubs.Items.Clear(); cmbSubFrom.Items.Clear(); txtSubLabel.Text = ""; numSubOffset.Value = 0; RenumberSubs();
            foreach (var t in new[] { txtTitle, txtEpTitle, txtGenres, txtYear, txtRuntime, txtDate, txtDesc, txtQuery, txtOutName }) t.Text = "";
            cmbResults.Items.Clear(); cmbCategory.Text = "Movies"; cmbFormat.SelectedIndex = 0; cmbKind.SelectedIndex = 0; numS.Value = numE.Value = 0;
            origShowDesc = ""; btnArtKeep.Enabled = false; SetArt("none", null);
            txtOutDir.Text = txtDefOut.Text; chkSuper.Checked = true; chkOverwrite.Checked = false;
            btnAdd.Text = "Add to queue"; btnCancelEdit.Visible = false; lblEditing.Text = "";
            UpdateVideoNote(); UpdateAutoName();
            if (tabs.SelectedIndex == 5 && toolsReady) tabs.SelectedIndex = 0;
        }

        Dict BuildJob(out string error)
        {
            error = null;
            if (probe == null) { error = "Open a source file first."; return null; }
            string kind = J.S(J.G(probe, "kind"));
            var job = new Dict();
            job["source"] = J.S(J.G(probe, "path"));
            var lit = cmbLayout.SelectedItem as Item;
            job["source_type"] = lit == null ? "auto" : (string)lit.Tag;
            job["output"] = rad3D.Checked ? "3d" : "2d";
            job["bitrate"] = (int)numBitrate.Value;
            if (kind == "video" && rad3D.Checked)
            {
                string lay = (string)job["source_type"];
                if (lay == "auto") lay = J.S(J.G(J.G(probe, "video"), "detected"));
                if (lay == "2d") { error = "3D output needs a side-by-side source: set Video > Source layout to HSBS/FSBS or choose 2D."; tabs.SelectedIndex = 0; return null; }
            }
            error = audio1.Validate() ?? audio2.Validate();
            if (error != null) { tabs.SelectedIndex = 1; return null; }
            job["audio"] = new Dict { { "main", audio1.ToJson() ?? new Dict() }, { "second", audio2.ToJson() } };
            if (lvSubs.Items.Count > 16) { error = "At most 16 subtitle tracks."; tabs.SelectedIndex = 2; return null; }
            job["subtitles"] = lvSubs.Items.Cast<ListViewItem>().Select(i => (object)J.Clone((Dict)i.Tag)).ToArray();
            if (txtTitle.Text.Trim() != "")
            {
                int y; int.TryParse(txtYear.Text.Trim(), out y);
                int rt; int.TryParse(txtRuntime.Text.Trim(), out rt);
                bool tv = cmbKind.SelectedIndex == 1;
                var md = new Dict {
                    { "title", txtTitle.Text.Trim() }, { "year", y }, { "category", cmbCategory.Text.Trim() == "" ? (tv ? "TV Shows" : "Movies") : cmbCategory.Text.Trim() },
                    { "genres", txtGenres.Text.Trim() }, { "runtime", rt }, { "date", txtDate.Text.Trim() },
                    { "desc", txtDesc.Text.Replace("\r\n", " ").Replace("\n", " ").Trim() }, { "showdesc", origShowDesc },
                    { "eptitle", tv ? txtEpTitle.Text.Trim() : "" }, { "format", cmbFormat.Text },
                    { "season_episode", tv && numS.Value > 0 && numE.Value > 0 ? new object[] { (int)numS.Value, (int)numE.Value } : null } };
                job["metadata"] = md;
            }
            else job["metadata"] = null;
            job["art"] = currentArtMode == "keep" ? "keep" : currentArtMode == "none" ? (kind == "super" && J.B(J.G(J.G(probe, "moflex"), "art")) ? "none" : null)
                       : currentArtMode == "file" ? artFile : artUrl;
            if (J.S(job["art"]) == "" && currentArtMode != "none" && currentArtMode != "keep") job["art"] = null;
            job["super"] = chkSuper.Checked;
            job["output_dir"] = txtOutDir.Text.Trim();
            job["output_name"] = txtOutName.Text.Trim();
            job["overwrite"] = chkOverwrite.Checked;
            if (job["output_dir"] as string != "" && !Directory.Exists((string)job["output_dir"]))
            {
                try { Directory.CreateDirectory((string)job["output_dir"]); }
                catch { error = "Output folder can't be created."; tabs.SelectedIndex = 4; return null; }
            }
            if (kind == "video" && !File.Exists(Path.Combine(ToolPath("encoder_dir", Path.Combine(appDir, "encoder")), "MobiclipMulticoreEncoder.exe")))
            { error = "The Mobiclip encoder is missing (app\\encoder), so video files can't be converted."; return null; }
            return job;
        }

        void AddToQueue()
        {
            string err;
            var job = BuildJob(out err);
            if (job == null) { SetStatus(err); return; }
            if (editing != null && queue.Contains(editing) && editing != running)
            {
                editing.Job = job; editing.Probe = probe; editing.Status = "Pending"; editing.Error = ""; editing.Output = "";
                RefreshQ(editing);
                SetStatus("Queue item updated.");
            }
            else { AddQueueItem(new QItem { Job = job, Probe = probe }); SetStatus("Added to the queue."); }
            SaveQueue();
            ClearEditor();
        }

        // ============================================================ queue
        static string QName(QItem q)
        {
            var md = J.G(q.Job, "metadata");
            string t = J.S(J.G(md, "title"));
            var se = J.A(J.G(md, "season_episode"));
            if (t != "") return t + (se.Length == 2 ? string.Format(" S{0:00}E{1:00}", J.I(se[0]), J.I(se[1])) : "") + (J.S(J.G(q.Job, "output")) == "3d" ? "  (3D)" : "  (2D)");
            return Path.GetFileName(J.S(J.G(q.Job, "source")));
        }

        void AddQueueItem(QItem q)
        {
            q.Lvi = new ListViewItem(new[] { QName(q), q.Status }); q.Lvi.Tag = q;
            queue.Add(q); lvQueue.Items.Add(q.Lvi); RefreshQ(q);
        }

        void RefreshQ(QItem q)
        {
            q.Lvi.SubItems[0].Text = QName(q);
            q.Lvi.SubItems[1].Text = q.Status;
            q.Lvi.ToolTipText = J.S(J.G(q.Job, "source")) + (q.Output != "" ? "\n-> " + q.Output : "") + (q.Error != "" ? "\n" + q.Error : "");
            q.Lvi.ForeColor = q.Status.StartsWith("Error") ? Color.Firebrick : q.Status.StartsWith("Done") ? Color.SeaGreen : q.Status == "Stopped" ? Color.DarkOrange : SystemColors.WindowText;
        }

        QItem SelectedQ() { return lvQueue.SelectedItems.Count > 0 ? (QItem)lvQueue.SelectedItems[0].Tag : null; }

        async Task EditSelected()
        {
            var q = SelectedQ(); if (q == null) return;
            if (q == running) { SetStatus("That item is running; stop the queue to edit it."); return; }
            if (!File.Exists(J.S(J.G(q.Job, "source")))) { SetStatus("The source file is gone."); return; }
            SetStatus("Loading queue item...");
            await LoadIntoEditor(q.Probe, J.Clone(q.Job));
            editing = q;
            btnAdd.Text = "Update item"; btnCancelEdit.Visible = true; lblEditing.Text = "Editing: " + QName(q);
            SetStatus("");
        }

        void RemoveSelected()
        {
            foreach (ListViewItem it in lvQueue.SelectedItems.Cast<ListViewItem>().ToList())
            {
                var q = (QItem)it.Tag;
                if (q == running) { SetStatus("Stop the queue to remove the running item."); continue; }
                if (q == editing) ClearEditor();
                queue.Remove(q); lvQueue.Items.Remove(it);
            }
            SaveQueue();
        }

        void MoveQ(int dir)
        {
            var q = SelectedQ(); if (q == null) return;
            int i = queue.IndexOf(q), j = i + dir;
            if (j < 0 || j >= queue.Count) return;
            queue.RemoveAt(i); queue.Insert(j, q);
            lvQueue.Items.Remove(q.Lvi); lvQueue.Items.Insert(j, q.Lvi); q.Lvi.Selected = true;
            SaveQueue();
        }

        void SaveQueue()
        {
            try
            {
                var arr = queue.Select(q => (object)new Dict { { "job", q.Job }, { "probe", q.Probe }, { "status", q == running ? "Pending" : q.Status }, { "was_running", q == running }, { "output", q.Output }, { "error", q.Error } }).ToArray();
                File.WriteAllText(queuePath, J.Dump(arr), new UTF8Encoding(false));
            }
            catch { }
        }

        void LoadQueue()
        {
            try
            {
                if (!File.Exists(queuePath)) return;
                foreach (var o in J.A(J.Parse(File.ReadAllText(queuePath, Encoding.UTF8))))
                {
                    var q = new QItem { Job = J.D(J.G(o, "job")), Probe = J.D(J.G(o, "probe")), Status = J.S(J.G(o, "status")), Output = J.S(J.G(o, "output")), Error = J.S(J.G(o, "error")) };
                    if (q.Job == null) continue;
                    if (q.Status == "" || !(q.Status.StartsWith("Done") || q.Status.StartsWith("Error") || q.Status == "Stopped")) q.Status = "Pending";
                    if (J.B(J.G(o, "was_running"))) Log("Interrupted last time, will start again: " + QName(q));
                    AddQueueItem(q);
                }
            }
            catch (Exception ex) { SetStatus("queue.json could not be read: " + ex.Message); }
        }

        void Log(string s)
        {
            if (txtLog.TextLength > 200000) txtLog.Text = txtLog.Text.Substring(100000);
            txtLog.AppendText(s + "\r\n");
        }

        void SetRunningUi(bool on)
        {
            queueActive = on;
            btnStart.Enabled = !on && toolsReady; btnStop.Enabled = on;
        }

        void StartQueue()
        {
            if (queueActive) return;
            if (!queue.Any(q => q.Status == "Pending" || q.Status == "Stopped" || q.Status.StartsWith("Error")))
            { SetStatus("Nothing to do: add something to the queue first."); return; }
            foreach (var q in queue.Where(q => q.Status == "Stopped" || q.Status.StartsWith("Error"))) { q.Status = "Pending"; q.Error = ""; RefreshQ(q); }
            stopRequested = false;
            SetRunningUi(true);
            RunNext();
        }

        void RunNext()
        {
            var q = queue.FirstOrDefault(x => x.Status == "Pending");
            if (q == null || stopRequested)
            {
                running = null; SetRunningUi(false);
                int done = queue.Count(x => x.Status.StartsWith("Done")), bad = queue.Count(x => x.Status.StartsWith("Error"));
                lblStage.Text = stopRequested ? "Stopped" : "Queue finished"; pbStage.Style = ProgressBarStyle.Blocks; pbStage.Value = stopRequested ? 0 : 100;
                lblDetail.Text = string.Format("{0} done, {1} failed", done, bad);
                SaveQueue();
                if (!stopRequested) FlashWindow();
                return;
            }
            running = q; q.Status = "Starting"; q.Error = ""; RefreshQ(q); SaveQueue();
            Log("=== " + QName(q));
            string jf = Path.Combine(Path.GetTempPath(), "moflex_studio_job_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(jf, J.Dump(q.Job), new UTF8Encoding(false));
            lblStage.Text = "Starting: " + QName(q); lblDetail.Text = ""; pbStage.Style = ProgressBarStyle.Blocks; pbStage.Value = 0;
            jobDuration = 0;
            string lastError = null;
            var p = new Process { StartInfo = Backend.Psi("run", jf), EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                UI(() => { var m = OnEvent(q, e.Data); if (m != null) lastError = m; });
            };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) UI(() => Log(e.Data)); };
            p.Exited += (s, e) =>
            {
                // drain the redirected output first, so 'done' is handled before the exit
                int code = -1; try { p.WaitForExit(); code = p.ExitCode; } catch { }
                UI(() =>
            {
                try { File.Delete(jf); } catch { }
                proc = null;
                if (stopRequested) { q.Status = "Stopped"; }
                else if (!q.Status.StartsWith("Done"))
                {
                    q.Status = "Error"; q.Error = lastError ?? ("backend exited with code " + code);
                    Log("! " + q.Error);
                }
                RefreshQ(q); SaveQueue();
                RunNext();
            });
            };
            try { p.Start(); Native.KillWithApp(p); proc = p; p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
            catch (Exception ex) { q.Status = "Error"; q.Error = ex.Message; RefreshQ(q); Log("! " + ex.Message); running = null; SetRunningUi(false); }
        }

        void UI(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        static string Clock(double s) { int t = (int)Math.Round(s); return t >= 3600 ? string.Format("{0}:{1:00}:{2:00}", t / 3600, t / 60 % 60, t % 60) : string.Format("{0}:{1:00}", t / 60, t % 60); }

        // one backend event; returns an error message when it is one
        string OnEvent(QItem q, string line)
        {
            object ev;
            try { ev = J.Parse(line); } catch { Log(line); return null; }
            string type = J.S(J.G(ev, "type"));
            switch (type)
            {
                case "start":
                    jobDuration = J.F(J.G(ev, "duration"));
                    break;
                case "stage":
                    stageStart = DateTime.Now;
                    lblStage.Text = string.Format("{0}/{1}  {2}", J.I(J.G(ev, "index")), J.I(J.G(ev, "total")), J.S(J.G(ev, "text")));
                    pbStage.Style = ProgressBarStyle.Blocks; pbStage.Value = 0; lblDetail.Text = "";
                    q.Status = string.Format("{0}/{1} {2}", J.I(J.G(ev, "index")), J.I(J.G(ev, "total")), StageShort(J.S(J.G(ev, "stage"))));
                    RefreshQ(q); Log(lblStage.Text);
                    break;
                case "progress":
                {
                    string st = J.S(J.G(ev, "stage"));
                    object pct = J.G(ev, "pct");
                    double el = J.F(J.G(ev, "elapsed"));
                    if (st == "moflex")
                    {
                        // the encoder writes nothing until the end: show elapsed vs a rough estimate (~19x realtime + startup)
                        double est = 20 + jobDuration / 19.0;
                        int v = (int)Math.Min(97, el / Math.Max(1, est) * 100);
                        pbStage.Value = Math.Max(0, v);
                        lblDetail.Text = string.Format("{0}  {1} elapsed, roughly {2} expected", J.S(J.G(ev, "note")), Clock(el), Clock(est));
                        q.Status = "2/3 Encoding " + Clock(el);
                        try { Native.HideEncoderWindows(); } catch { }
                    }
                    else if (pct != null)
                    {
                        double pv = J.F(pct);
                        pbStage.Value = (int)Math.Max(0, Math.Min(100, pv));
                        object eta = J.G(ev, "eta"), sp = J.G(ev, "speed");
                        string note = J.S(J.G(ev, "note"));
                        lblDetail.Text = string.Format("{0:0}%{1}{2}{3}", pv, eta != null ? "  " + Clock(J.F(eta)) + " left" : "",
                            sp != null ? string.Format("  {0:0.0}x", J.F(sp)) : "", note != "" ? "  " + note : "");
                        var parts = q.Status.Split(' ');
                        q.Status = (parts.Length >= 2 ? parts[0] + " " + parts[1] : q.Status) + string.Format(" {0:0}%", pv);
                    }
                    RefreshQ(q);
                    break;
                }
                case "log":
                    Log("  " + J.S(J.G(ev, "text")));
                    break;
                case "done":
                    q.Output = J.S(J.G(ev, "output"));
                    q.Status = "Done " + Clock(J.F(J.G(ev, "seconds")));
                    pbStage.Value = 100;
                    lblDetail.Text = string.Format("{0}  ({1:0.0} MB)", Path.GetFileName(q.Output), J.F(J.G(ev, "size")) / 1048576.0);
                    Log("done: " + q.Output);
                    RefreshQ(q);
                    break;
                case "error":
                    string m = J.S(J.G(ev, "message"));
                    Log("! " + m);
                    return m;
            }
            return null;
        }

        static string StageShort(string s) { return s == "avi" ? "AVI" : s == "moflex" ? "Encoding" : s == "super" ? "Building" : s; }

        void StopQueue()
        {
            stopRequested = true;
            btnStop.Enabled = false;
            if (proc != null)
            {
                try { Process.Start(new ProcessStartInfo("taskkill", "/F /T /PID " + proc.Id) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
                try { Process.Start(new ProcessStartInfo("taskkill", "/F /IM MobiclipMulticoreEncoder.exe") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
                lblStage.Text = "Stopping...";
            }
            else RunNext();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (queueActive)
            {
                // closing stops the running job (nothing half-written is left at the output)
                stopRequested = true;
                if (proc != null)
                {
                    try { Process.Start(new ProcessStartInfo("taskkill", "/F /T /PID " + proc.Id) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
                    try { Process.Start(new ProcessStartInfo("taskkill", "/F /IM MobiclipMulticoreEncoder.exe") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
                }
                if (running != null) running.Status = "Pending";
            }
            running = null;
            SaveQueue();
            try { if (txtToken.Text.Trim() != J.S(J.G(settings, "tmdb_token"))) SaveSettings(); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindow(IntPtr h, bool invert);
        void FlashWindow() { try { if (!ContainsFocus) FlashWindow(Handle, true); } catch { } }

        static MainForm instance;

        static void Report(Exception ex, bool fatal)
        {
            try
            {
                string log = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "app", "error.log");
                File.AppendAllText(log, string.Format("==== {0:yyyy-MM-dd HH:mm:ss}{1}\r\n{2}\r\n", DateTime.Now, fatal ? "  (fatal)" : "", ex), new UTF8Encoding(false));
            }
            catch { }
            try
            {
                var f = instance;
                if (f != null && !f.IsDisposed)
                    f.SetStatus("Something went wrong (" + ex.GetType().Name + ": " + ex.Message + "). Details are in app\\error.log. The queue keeps running.");
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception ?? new Exception("unknown error"), true);
            TaskScheduler.UnobservedTaskException += (s, e) => { Report(e.Exception, false); e.SetObserved(); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            instance = new MainForm();
            Application.Run(instance);
        }
    }
}
