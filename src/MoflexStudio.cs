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
using System.Security.Cryptography;
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

        [DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, int len, IntPtr ret);
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);

        // every process currently in the job: the backend, ffmpeg, RunAsDate, the encoder
        static List<int> JobPids()
        {
            var pids = new List<int>();
            if (job == IntPtr.Zero) return pids;
            int size = 8 + IntPtr.Size * 512;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(job, 3, buf, size, IntPtr.Zero))     // JobObjectBasicProcessIdList
                {
                    int n = Marshal.ReadInt32(buf, 4);
                    for (int i = 0; i < n; i++) pids.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size).ToInt64());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return pids;
        }

        // pause = freeze every process of the running job where it is; resume continues exactly there
        public static int SetJobSuspended(bool suspend)
        {
            int n = 0;
            foreach (int pid in JobPids())
            {
                try { using (var p = Process.GetProcessById(pid)) { if (suspend) NtSuspendProcess(p.Handle); else NtResumeProcess(p.Handle); n++; } }
                catch { }
            }
            return n;
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        // the Mobiclip encoder pops up its own window even when started hidden: hide each window once when it
        // appears (it goes to the tray); if the user opens it from the tray it is left alone
        static readonly HashSet<IntPtr> hiddenOnce = new HashSet<IntPtr>();
        public static void ResetHidden() { hiddenOnce.Clear(); }
        public static void HideEncoderWindows()
        {
            var pids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("MobiclipMulticoreEncoder")) { pids.Add(p.Id); p.Dispose(); }
            if (pids.Count == 0) return;
            EnumWindows((h, l) => { int pid; GetWindowThreadProcessId(h, out pid); if (pids.Contains(pid) && IsWindowVisible(h) && hiddenOnce.Add(h)) ShowWindow(h, 0); return true; }, IntPtr.Zero);
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

        // audio streams of other files offered as choices (a version's own tracks): "vs:<k>" -> (file, stream)
        List<KeyValuePair<string, object>> fileStreams = new List<KeyValuePair<string, object>>();

        public void AddFileStreams(string path, string what, Dict pr)
        {
            foreach (var a in J.A(J.G(pr, "audio")))
            {
                fileStreams.Add(new KeyValuePair<string, object>(path, a));
                var it = new Item(what + "  " + StreamText(a), "vs:" + (fileStreams.Count - 1));
                int other = -1;                                  // before "Other file...", which stays last
                for (int i = 0; i < cmbMode.Items.Count; i++) if ((string)((Item)cmbMode.Items[i]).Tag == "file") other = i;
                if (other >= 0) cmbMode.Items.Insert(other, it); else cmbMode.Items.Add(it);
            }
        }

        public void RemoveFileStreams(string path)
        {
            for (int i = cmbMode.Items.Count - 1; i >= 0; i--)
            {
                string tg = (string)((Item)cmbMode.Items[i]).Tag;
                if (tg.StartsWith("vs:") && string.Equals(fileStreams[int.Parse(tg.Substring(3))].Key, path, StringComparison.OrdinalIgnoreCase))
                    cmbMode.Items.RemoveAt(i);
            }
            if (cmbMode.SelectedIndex < 0 && cmbMode.Items.Count > 0) cmbMode.SelectedIndex = 0;
        }

        static string StreamText(object a)
        {
            string t = J.S(J.G(a, "title"));
            return string.Format("#{0}  {1}  {2} {3}ch{4}{5}", J.I(J.G(a, "index")), J.S(J.G(a, "lang")).ToUpper(),
                J.S(J.G(a, "codec")), J.I(J.G(a, "channels")), t.Length > 0 ? "  " + t : "", J.B(J.G(a, "default")) ? "  (default)" : "");
        }

        public void Setup(Dict probe)
        {
            SourceProbe = probe; otherProbe = null; fileStreams.Clear();
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
                foreach (var a in J.A(J.G(J.G(probe, "moflex"), "audios")))
                    cmbMode.Items.Add(new Item(KeepText(a), "keep:" + J.I(J.G(a, "existing"))));
                if (kind == "video") foreach (var a in J.A(J.G(probe, "audio"))) cmbMode.Items.Add(new Item("Source " + StreamText(a), "src:" + J.I(J.G(a, "index"))));
                cmbMode.Items.Add(new Item("Other file...", "file"));
            }
            cmbMode.SelectedIndex = cmbMode.Items.Count > 0 ? 0 : -1;
            ModeChanged();
        }

        public static string KeepText(object a)
        {
            int sec = (int)J.F(J.G(a, "seconds"));
            return string.Format("Current track {0}: {1}  ({2} Hz, {3}:{4:00}:{5:00})", J.I(J.G(a, "existing")) + 2, J.S(J.G(a, "lang")),
                J.I(J.G(a, "rate")), sec / 3600, sec / 60 % 60, sec % 60);
        }

        /* what a list row shows for the track this editor describes */
        public string Describe()
        {
            string m = Mode;
            if (m == "file")
            {
                var it = cmbStream.SelectedItem as Item;
                return Path.GetFileName(txtFile.Text) + (it != null ? "  " + it.Text : "");
            }
            var mi = cmbMode.SelectedItem as Item;
            return mi == null ? "" : mi.Text;
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
            numOffset.Enabled = file || m.StartsWith("src:") || m.StartsWith("vs:");
            txtLang.Enabled = m != "none" && m != "silent" && m != "normalize" && !(m == "keep" && !second) && m != "";
            if (m.StartsWith("src:"))
            {
                var a = FindStream(SourceProbe, int.Parse(m.Substring(4)));
                txtLang.Text = Lang3(J.S(J.G(a, "lang")));
            }
            else if (m.StartsWith("vs:"))
                txtLang.Text = Lang3(J.S(J.G(fileStreams[int.Parse(m.Substring(3))].Value, "lang")));
            else if (m.StartsWith("keep:"))
            {
                int k = int.Parse(m.Substring(5));
                var a = J.A(J.G(J.G(SourceProbe, "moflex"), "audios")).FirstOrDefault(x => J.I(J.G(x, "existing")) == k);
                txtLang.Text = J.S(J.G(a, "lang"));
            }
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
            if (m == "keep") { d["keep"] = true; return d; }
            if (m.StartsWith("keep:")) { d["keep"] = int.Parse(m.Substring(5)); if (lang != "") d["label"] = lang; return d; }
            if (m == "normalize") { d["normalize"] = true; return d; }
            if (m.StartsWith("src:")) { d["file"] = null; d["stream"] = int.Parse(m.Substring(4)); }
            else if (m.StartsWith("vs:"))
            {
                var fs = fileStreams[int.Parse(m.Substring(3))];
                d["file"] = fs.Key; d["stream"] = J.I(J.G(fs.Value, "index"));
            }
            else { d["file"] = txtFile.Text; d["stream"] = J.I(J.G(((Item)cmbStream.SelectedItem).Tag, "index")); }
            d["offset"] = (double)numOffset.Value;
            if (lang == "") lang = second ? "alt" : "eng";
            d["lang"] = lang;
            if (second) d["label"] = lang;
            return d;
        }

        public async Task FromJson(object o)
        {
            if (o == null) { if (second) { if (cmbMode.Items.Count > 0) cmbMode.SelectedIndex = 0; } else SelectMode("silent"); return; }
            if (second && J.Has(o, "keep"))
            {
                object kv = J.G(o, "keep");
                SelectMode("keep:" + (kv is bool ? 0 : J.I(kv)));
                if (J.Has(o, "label")) txtLang.Text = J.S(J.G(o, "label")).ToUpper();
                return;
            }
            if (J.B(J.G(o, "keep"))) { SelectMode("keep"); return; }
            if (J.B(J.G(o, "normalize"))) { SelectMode("normalize"); return; }
            if (!J.Has(o, "stream")) { SelectMode("silent"); return; }
            string f = J.S(J.G(o, "file"));
            int vsk = fileStreams.FindIndex(fs => string.Equals(fs.Key, f, StringComparison.OrdinalIgnoreCase) && J.I(J.G(fs.Value, "index")) == J.I(J.G(o, "stream")));
            if (f == "") SelectMode("src:" + J.I(J.G(o, "stream")));
            else if (vsk >= 0) SelectMode("vs:" + vsk);
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
        const string AppVersion = "2.0";
        // the staff-only update channel: the full bundle, unlinked on clownsec.com (latest.txt = version, md5 of the zip)
        const string UpdateBase = "https://www.clownsec.com/studio/";
        const string PyUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";
        const string FfUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        // the open moflex encoder (Mobipeg-MOFLEX + clownsec patches, GPL-2.0; source: github.com/brainphreak/clownsec-moflex-encoder)
        const string OpenEncUrl = "https://www.clownsec.com/studio/tools/moflex-ffmpeg-windows-x64.zip";

        string appDir, settingsPath, queuePath;
        Dict settings = new Dict();
        Dict probe;                         // the source loaded in the editor
        QItem editing;                      // queue item being edited (null = new)
        List<QItem> queue = new List<QItem>();
        QItem running; Process proc; bool stopRequested, queueActive, paused;
        string currentWork = "";
        double jobDuration; DateTime stageStart;
        string currentArtMode = "none", artUrl = "", artFile = "";
        string origShowDesc = "";
        string curTmdbId = "";              // TMDB id of the show/movie picked in the editor
        Dict showCarry;                     // the last queued TV episode's show info + poster, reused for the next episodes
        bool toolsReady;

        // editor controls
        TextBox txtSource; Label lblInfo; Button btnOpen;
        TabControl tabs;
        ComboBox cmbLayout; RadioButton rad3D, rad2D; NumericUpDown numBitrate; Label lblVideoNote;
        CheckBox chkConv3d, chkKeepSbs; ComboBox cmbTpl; Button btnTplEdit, btnIw3Install; Label lblIw3;
        Dict iw3Status; Dict conv3dOver = new Dict(); bool iw3Installing, tplRefilling;
        AudioPicker audio1, audio2;           // audio2 = the editor for one extra track (2..8)
        ListView lvAud; Label lblAudCount;
        ListView lvVer; TextBox txtVerLabel; ComboBox cmbVerLayout, cmbVerAudio;
        ListView lvSubs; ComboBox cmbSubFrom; TextBox txtSubLabel; NumericUpDown numSubOffset; Label lblSubCount;
        // Type (what the video IS, saved as kind=) and the shelf it sits on (category=) are separate:
        // the shelf follows the type unless it is set to something of its own
        static readonly string[] KindNames = { "movie", "episode", "short", "musicvideo" };
        static readonly string[] KindShelf = { "Movies", "TV Shows", "Shorts", "Music Videos" };
        bool fillingEditor;
        ComboBox cmbKind, cmbResults, cmbCategory, cmbFormat; TextBox txtQuery, txtTitle, txtEpTitle, txtGenres, txtYear, txtRuntime, txtDate, txtDesc;
        NumericUpDown numS, numE; PictureBox picArt; Label lblArt; Button btnArtKeep;
        TextBox txtOutDir, txtOutName; Label lblAutoName; CheckBox chkSuper, chkOverwrite;
        CheckBox chkCarry, chkRememberOut;
        TextBox txtToken, txtDefOut; NumericUpDown numDefBitrate; Label lblTools; Button btnDownload; ComboBox cmbEncoder;
        Button btnUpdate; Label lblVersion; string updateVer, updateMd5;
        Button btnAdd, btnClear, btnCancelEdit; Label lblEditing;
        // queue + progress
        ListView lvQueue; Button btnStart, btnPause, btnStop, btnEdit, btnRemove, btnUp, btnDown, btnClearDone;
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
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            // resizable + snappable: the editor keeps its size, the queue column takes the rest.
            // 936 wide still fits a half-screen snap on a 1920 display.
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true;
            MinimumSize = SizeFromClientSize(new Size(936, 600));
            Resize += delegate { LayoutQueueColumn(); };
            LoadSettings();
            ClearEditor();
            LoadQueue();
            Shown += async delegate
            {
                CleanStaleWork();
                try { string ud = Path.Combine(appDir, "update"); if (Directory.Exists(ud)) Directory.Delete(ud, true); } catch { }   // a finished update's leftovers
                await CheckTools(true);
                await RefreshIw3(null);
                await CheckUpdate(false);         // quiet: a status-bar note when there is a newer build
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
            chkConv3d = Add(tVideo, new CheckBox { Text = "2D \u2192 3D (iw3)", Left = 10, Top = 110, Width = 140 });
            cmbTpl = Add(tVideo, new ComboBox { Left = 152, Top = 108, Width = 272, DropDownStyle = ComboBoxStyle.DropDownList });
            btnTplEdit = Add(tVideo, new Button { Text = "Settings...", Left = 430, Top = 107, Width = 80, Height = 25 });
            chkKeepSbs = Add(tVideo, new CheckBox { Text = "Keep the 3D side-by-side video", Left = 152, Top = 136, Width = 210 });
            lblIw3 = Note(tVideo, "", 366, 139, 136, 18);
            btnIw3Install = Add(tVideo, new Button { Text = "Install iw3...", Left = 506, Top = 134, Width = 82, Height = 25, Visible = false });
            lblVideoNote = Note(tVideo, "", 10, 164, 576, 50);
            chkConv3d.CheckedChanged += delegate { if (chkConv3d.Checked) rad3D.Checked = true; UpdateVideoNote(); UpdateAutoName(); };
            // a template the user picks starts clean; refilling the list (RefreshIw3) is not a pick and must
            // keep "Use for this video" changes (it used to wipe them: the job ran the plain template)
            cmbTpl.SelectedIndexChanged += delegate { if (!tplRefilling) conv3dOver.Clear(); UpdateVideoNote(); };
            btnTplEdit.Click += delegate { EditIw3Settings(); };
            btnIw3Install.Click += async delegate { await InstallIw3(); };
            var grpVer = Add(tVideo, new GroupBox { Text = "Other versions of the film  (CLOWNSEC player: VIDEO > Version)", Left = 8, Top = 216, Width = 580, Height = 196 });
            lvVer = new ListView { Left = 8, Top = 20, Width = 460, Height = 100, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
            lvVer.Columns.Add("#", 44); lvVer.Columns.Add("Name", 62); lvVer.Columns.Add("From", 246); lvVer.Columns.Add("Audio", 104);
            var bVerAdd = new Button { Text = "Add file...", Left = 474, Top = 19, Width = 98 };
            var bVerRm = new Button { Text = "Remove", Left = 474, Top = 47, Width = 98 };
            var bVerUp = new Button { Text = "Up", Left = 474, Top = 75, Width = 47 };
            var bVerDn = new Button { Text = "Down", Left = 525, Top = 75, Width = 47 };
            var lVn = new Label { Text = "Name", Left = 8, Top = 132, Width = 38 };
            txtVerLabel = new TextBox { Left = 46, Top = 128, Width = 62, MaxLength = 7 };
            var lVl = new Label { Text = "Layout", Left = 114, Top = 132, Width = 44 };
            cmbVerLayout = new ComboBox { Left = 158, Top = 128, Width = 84, DropDownStyle = ComboBoxStyle.DropDownList };
            var lVa = new Label { Text = "Default audio", Left = 248, Top = 132, Width = 78 };
            cmbVerAudio = new ComboBox { Left = 326, Top = 128, Width = 142, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbVerLayout.Items.AddRange(new object[] { new Item("Auto", "auto"), new Item("2D", "2d"), new Item("Half SBS", "hsbs"), new Item("Full SBS", "fsbs") });
            cmbVerLayout.SelectedIndex = 0;
            var bVerUpd = new Button { Text = "Update selected", Left = 474, Top = 127, Width = 98 };
            var nVer = new Label { Text = "Row 1 is the MAIN version: the file's normal video, what every player shows. The others are for the CLOWNSEC player (VIDEO > Version), " +
                "which switches to each one's default audio (the viewer can still change it). Same film, same timeline.",
                Left = 8, Top = 156, Width = 564, Height = 34, ForeColor = SystemColors.GrayText };
            grpVer.Controls.AddRange(new Control[] { lvVer, bVerAdd, bVerRm, bVerUp, bVerDn, lVn, txtVerLabel, lVl, cmbVerLayout, lVa, cmbVerAudio, bVerUpd, nVer });
            bVerAdd.Click += delegate { AddVersionFromUi(); };
            bVerRm.Click += delegate
            {
                if (lvVer.SelectedItems.Count == 0) return;
                if (J.B(J.G(lvVer.SelectedItems[0].Tag, "self"))) { SetStatus("This file's own video can't be removed; move another version above it to make that one the main."); return; }
                string vsrc = J.S(J.G(lvVer.SelectedItems[0].Tag, "source"));
                lvVer.Items.Remove(lvVer.SelectedItems[0]); RenumberVer();
                if (vsrc != "" && !lvVer.Items.Cast<ListViewItem>().Any(i => string.Equals(J.S(J.G(i.Tag, "source")), vsrc, StringComparison.OrdinalIgnoreCase)))
                    ForgetVersionStreams(vsrc);
            };
            bVerUp.Click += delegate { MoveRow(lvVer, -1); }; bVerDn.Click += delegate { MoveRow(lvVer, 1); };
            bVerUpd.Click += delegate { UpdateSelectedVersion(); };
            lvVer.SelectedIndexChanged += delegate
            {
                if (lvVer.SelectedItems.Count == 0) return;
                var d = (Dict)lvVer.SelectedItems[0].Tag;
                txtVerLabel.Text = J.S(J.G(d, "label"));
                SelectItem(cmbVerLayout, J.S(J.G(d, "source_type")) == "" ? "auto" : J.S(J.G(d, "source_type")));
                SelectItem(cmbVerAudio, J.S(J.G(d, "audio_ref")) == "" ? "t1" : J.S(J.G(d, "audio_ref")));
            };
            rad3D.CheckedChanged += delegate { UpdateVideoNote(); UpdateAutoName(); };
            cmbLayout.SelectedIndexChanged += delegate { UpdateVideoNote(); };

            // ---- Audio
            audio1 = new AudioPicker("Track 1  (in the video stream, plays everywhere)", false, 8, 8);
            lvAud = Add(tAudio, new ListView { Left = 8, Top = 132, Width = 496, Height = 104, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false });
            lvAud.Columns.Add("#", 28); lvAud.Columns.Add("Label", 50); lvAud.Columns.Add("From", 340); lvAud.Columns.Add("Offset", 60);
            var bAUp = Add(tAudio, new Button { Text = "Up", Left = 510, Top = 132, Width = 78 });
            var bADn = Add(tAudio, new Button { Text = "Down", Left = 510, Top = 160, Width = 78 });
            var bARm = Add(tAudio, new Button { Text = "Remove", Left = 510, Top = 188, Width = 78 });
            lblAudCount = Note(tAudio, "Tracks 2-8: 0 / 7", 510, 216, 80, 20);
            audio2 = new AudioPicker("Add or edit a track  (2-8: SUPER MOFLEX trailer, CLOWNSEC player only)", true, 8, 240);
            foreach (var ap in new[] { audio1, audio2 })
            {
                ap.ProbeFile = ProbeDict; ap.Status = SetStatus;
                tAudio.Controls.Add(ap.Box);
            }
            var bAAdd = Add(tAudio, new Button { Text = "Add track", Left = 8, Top = 362, Width = 96, Height = 25 });
            var bAUpd = Add(tAudio, new Button { Text = "Update selected", Left = 110, Top = 362, Width = 128, Height = 25 });
            Note(tAudio, "Every new track is loudness-normalised (EBU R128, -16 LUFS). Offset shifts an audio file (negative = starts earlier). " +
                "The label is what the player shows (JPN, COM, DUB...).", 246, 360, 342, 44);
            bAUp.Click += delegate { MoveRow(lvAud, -1); }; bADn.Click += delegate { MoveRow(lvAud, 1); };
            bARm.Click += delegate { if (lvAud.SelectedItems.Count > 0) { lvAud.Items.Remove(lvAud.SelectedItems[0]); RenumberAud(); } };
            bAAdd.Click += delegate { AddAudFromUi(false); };
            bAUpd.Click += delegate { AddAudFromUi(true); };
            lvAud.SelectedIndexChanged += async delegate
            {
                if (lvAud.SelectedItems.Count == 0) return;
                await audio2.FromJson(lvAud.SelectedItems[0].Tag);
            };

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
            cmbKind.Items.AddRange(new object[] { "Movie", "TV episode", "Short", "Music video" }); cmbKind.SelectedIndex = 0;
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
            cmbCategory.Items.AddRange(new object[] { "Movies", "TV Shows", "Shorts", "Music Videos", "Anime", "Concerts", "Uncategorized" });
            L(tInfo, "Format", 236, ly + 112, 46);
            cmbFormat = Add(tInfo, new ComboBox { Left = 284, Top = ly + 112, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList });
            cmbFormat.Items.AddRange(new object[] { "converted", "native" }); cmbFormat.SelectedIndex = 0;
            L(tInfo, "Description", 8, ly + 140, 80);
            txtDesc = Add(tInfo, new TextBox { Left = 8, Top = ly + 160, Width = 426, Height = 128, Multiline = true, ScrollBars = ScrollBars.Vertical });
            Note(tInfo, "Leave the title empty to keep the file's current info.", 8, ly + 292, 426, 18);
            chkCarry = Add(tInfo, new CheckBox { Left = 8, Top = ly + 312, Width = 580, Height = 22, Checked = true, Visible = false });
            tips.SetToolTip(chkCarry, "After you queue a TV episode, the next files you open or drop get the same show title, year, genres, category, format and poster.\n" +
                "Season and episode come from each file name (S01E05 / 1x05), or count up from the last one. With a TMDB pick, each episode's title and description are fetched too.");
            tips.SetToolTip(txtRuntime, "Filled in from the length of the file. Change it only if you want a different number.");
            picArt = Add(tInfo, new PictureBox { Left = 448, Top = ly, Width = 132, Height = 188, BorderStyle = BorderStyle.FixedSingle, SizeMode = PictureBoxSizeMode.StretchImage, BackColor = Color.FromArgb(40, 40, 40) });
            var bImg = Add(tInfo, new Button { Text = "Image...", Left = 448, Top = ly + 194, Width = 64 });
            var bNone = Add(tInfo, new Button { Text = "None", Left = 516, Top = ly + 194, Width = 64 });
            btnArtKeep = Add(tInfo, new Button { Text = "Keep original", Left = 448, Top = ly + 222, Width = 132 });
            lblArt = Note(tInfo, "", 448, ly + 252, 140, 58);
            bSearch.Click += async delegate { await TmdbSearch(); };
            txtQuery.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await TmdbSearch(); } };
            cmbResults.SelectedIndexChanged += async delegate { await TmdbPick(); };
            cmbKind.SelectedIndexChanged += delegate
            {
                bool tv = cmbKind.SelectedIndex == 1; numS.Enabled = numE.Enabled = txtEpTitle.Enabled = tv;
                // the shelf follows the type, unless it has been pointed at a shelf of its own (Anime...)
                if (!fillingEditor && (cmbCategory.Text.Trim() == "" || KindShelf.Contains(cmbCategory.Text.Trim())))
                    cmbCategory.Text = KindShelf[Math.Max(0, cmbKind.SelectedIndex)];
                UpdateAutoName();
            };
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
            Note(tSet, "The API Read Access Token from themoviedb.org (Settings > API). Optional: without one, searches go through clownsec.com.", 108, 40, 478, 32);
            L(tSet, "Bitrate", 10, 80, 96);
            numDefBitrate = Add(tSet, new NumericUpDown { Left = 108, Top = 80, Width = 80, Minimum = 200, Maximum = 8000, Increment = 100, Value = 1400 });
            L(tSet, "kbps default", 192, 80, 80);
            L(tSet, "Encoder", 290, 80, 56);
            cmbEncoder = Add(tSet, new ComboBox { Left = 348, Top = 80, Width = 236, DropDownStyle = ComboBoxStyle.DropDownList });
            cmbEncoder.Items.Add(new Item("Mobiclip (Nintendo's, best quality)", "mobiclip"));
            cmbEncoder.Items.Add(new Item("Open encoder (any system, open source)", "open"));
            cmbEncoder.SelectedIndex = 0;
            cmbEncoder.SelectedIndexChanged += async delegate
            {
                settings["encoder"] = EncoderChoice(); try { SaveSettings(); } catch { }
                UpdateVideoNote();
                if (EncoderChoice() == "open") await CheckTools(true);    // fetches it if it isn't here yet
            };
            L(tSet, "Output folder", 10, 112, 96);
            txtDefOut = Add(tSet, new TextBox { Left = 108, Top = 112, Width = 436 });
            var bDefOut = Add(tSet, new Button { Text = "...", Left = 548, Top = 111, Width = 34, Height = 25 });
            bDefOut.Click += delegate { var p = PickFolder(txtDefOut.Text); if (p != null) txtDefOut.Text = p; };
            chkRememberOut = Add(tSet, new CheckBox { Text = "Remember the last output folder (unticked: always start at the folder above)", Left = 108, Top = 142, Width = 478, Checked = true });
            var bSave = Add(tSet, new Button { Text = "Save settings", Left = 108, Top = 170, Width = 120, Height = 27 });
            bSave.Click += delegate { SaveSettings(); SetStatus("Settings saved."); };
            var grp = Add(tSet, new GroupBox { Text = "Tools", Left = 8, Top = 200, Width = 578, Height = 212 });
            lblTools = new Label { Left = 12, Top = 20, Width = 556, Height = 96, Font = new Font("Consolas", 9f) };
            grp.Controls.Add(lblTools);
            // 2D -> 3D: which iw3, its models
            var bIw3Inst = new Button { Text = "Install iw3...", Left = 12, Top = 120, Width = 110, Height = 27 };
            var bIw3Mine = new Button { Text = "Use my iw3...", Left = 128, Top = 120, Width = 110, Height = 27 };
            var bIw3Models = new Button { Text = "Models...", Left = 244, Top = 120, Width = 90, Height = 27 };
            var nIw3 = new Label { Text = "2D \u2192 3D (optional): install iw3 into Studio's folder, or use one you have", Left = 340, Top = 118, Width = 230, Height = 32, ForeColor = SystemColors.GrayText };
            grp.Controls.AddRange(new Control[] { bIw3Inst, bIw3Mine, bIw3Models, nIw3 });
            bIw3Inst.Click += async delegate { await InstallIw3(); };
            bIw3Mine.Click += async delegate { await UseMyIw3(); };
            bIw3Models.Click += async delegate
            {
                if (!J.B(J.G(iw3Status, "installed"))) { SetStatus("Install iw3 first (or use yours)."); return; }
                await ManageModels(this); await RefreshIw3(null);
            };
            btnDownload = new Button { Text = "Download missing tools", Left = 12, Top = 172, Width = 180, Height = 27 };
            var bAppDir = new Button { Text = "Open app folder", Left = 198, Top = 172, Width = 130, Height = 27 };
            btnUpdate = new Button { Text = "Check for updates", Left = 334, Top = 172, Width = 130, Height = 27 };
            lblVersion = new Label { Text = "Version " + AppVersion, Left = 470, Top = 178, Width = 100, ForeColor = SystemColors.GrayText };
            grp.Controls.Add(btnDownload); grp.Controls.Add(bAppDir); grp.Controls.Add(btnUpdate); grp.Controls.Add(lblVersion);
            btnUpdate.Click += async delegate { await CheckUpdate(true); };
            btnDownload.Click += async delegate { downloadAll = true; try { await CheckTools(true); } finally { downloadAll = false; } };
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
            btnStart = Add(this, new Button { Text = "Start queue", Left = 628, Top = 336, Width = 132, Height = 32, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
            btnPause = Add(this, new Button { Text = "Pause", Left = 766, Top = 336, Width = 102, Height = 32, Enabled = false });
            btnStop = Add(this, new Button { Text = "Stop", Left = 874, Top = 336, Width = 102, Height = 32, Enabled = false });
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
            btnPause.Click += delegate { TogglePause(); };

            // ---- status bar
            lblStatus = Add(this, new Label { Left = 8, Top = 578, Width = 780, Height = 18, AutoEllipsis = true, ForeColor = SystemColors.GrayText });
            pbTools = Add(this, new ProgressBar { Left = 796, Top = 578, Width = 180, Height = 16, Visible = false });
        }

        void SetStatus(string s) { lblStatus.Text = s ?? ""; }

        // the queue column (x 628..) and status bar follow the window size; the editor stays put.
        // Designed at 984x600: extra height is split between the queue list and the log.
        void LayoutQueueColumn()
        {
            if (lvQueue == null || WindowState == FormWindowState.Minimized) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W < 300 || H < 300) return;
            int qx = 628, qw = Math.Max(300, W - 636), dh = Math.Max(0, H - 600), dq = dh / 2;
            SuspendLayout();
            lvQueue.SetBounds(qx, 8, qw, 290 + dq);
            lvQueue.Columns[0].Width = Math.Max(120, qw - 138);
            QRow(qx, qw, 304 + dq, 4, new Control[] { btnEdit, btnRemove, btnUp, btnDown, btnClearDone }, new[] { 62, 66, 48, 52, 104 });
            QRow(qx, qw, 336 + dq, 6, new Control[] { btnStart, btnPause, btnStop }, new[] { 132, 102, 102 });
            lblStage.SetBounds(qx, 376 + dq, qw, 18);
            pbStage.SetBounds(qx, 396 + dq, qw, 18);
            lblDetail.SetBounds(qx, 418 + dq, qw, 18);
            txtLog.SetBounds(qx, 440 + dq, qw, 130 + dh - dq);
            lblStatus.SetBounds(8, 578 + dh, W - 204, 18);
            pbTools.SetBounds(W - 188, 578 + dh, 180, 16);
            ResumeLayout();
        }
        // one row of buttons spread across the column, each keeping its share of the designed width
        static void QRow(int x0, int width, int y, int gap, Control[] cs, int[] ws)
        {
            int total = ws.Sum(), avail = width - gap * (cs.Length - 1), x = x0;
            for (int i = 0; i < cs.Length; i++)
            {
                int w = i == cs.Length - 1 ? x0 + width - x : ws[i] * avail / total;
                cs[i].SetBounds(x, y, w, cs[i].Height);
                x += w + gap;
            }
        }

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
            chkRememberOut.Checked = !J.Has(settings, "remember_output") || J.B(J.G(settings, "remember_output"));
            SelectItem(cmbEncoder, J.S(J.G(settings, "encoder")) == "open" ? "open" : "mobiclip");
        }

        string EncoderChoice() { var it = cmbEncoder == null ? null : cmbEncoder.SelectedItem as Item; return it == null ? "mobiclip" : (string)it.Tag; }
        string OpenEncPath() { return Path.Combine(ToolPath("ffmpeg_dir", Path.Combine(appDir, "bin")), "moflex-ffmpeg.exe"); }

        // where a new job's output goes: the last folder used (if remembered), else the default from Settings
        string DefaultOutDir()
        {
            if (chkRememberOut != null && chkRememberOut.Checked && J.Has(settings, "last_output")) return J.S(J.G(settings, "last_output"));
            return txtDefOut.Text.Trim();
        }

        void RememberOutDir(string dir)
        {
            if (!chkRememberOut.Checked || (J.Has(settings, "last_output") && J.S(J.G(settings, "last_output")) == dir)) return;
            settings["last_output"] = dir;
            try { SaveSettings(); } catch { }
        }

        void SaveSettings()
        {
            settings["tmdb_token"] = txtToken.Text.Trim();
            settings["default_bitrate"] = (int)numDefBitrate.Value;
            settings["default_output"] = txtDefOut.Text.Trim();
            settings["remember_output"] = chkRememberOut.Checked;
            settings["encoder"] = EncoderChoice();
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

        bool downloadAll;   // the Download button: optional tools too (the open encoder)
        async Task CheckTools(bool download)
        {
            FindPython();
            string bin = ToolPath("ffmpeg_dir", Path.Combine(appDir, "bin"));
            bool py = File.Exists(Backend.Python);
            bool ff = File.Exists(Path.Combine(bin, "ffmpeg.exe")) && File.Exists(Path.Combine(bin, "ffprobe.exe"));
            bool oe = File.Exists(OpenEncPath());
            bool wantOe = EncoderChoice() == "open" || downloadAll;
            if (download && (!py || !ff || (!oe && wantOe)))
            {
                btnDownload.Enabled = false;
                try
                {
                    try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
                    catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                    if (!py) { await Fetch(PyUrl, "Python 3.11 (embeddable)", Path.Combine(appDir, "python"), null); }
                    if (!ff) { await Fetch(FfUrl, "ffmpeg (gyan.dev release essentials)", Path.Combine(appDir, "bin"), new[] { "ffmpeg.exe", "ffprobe.exe" }); }
                    if (!oe && wantOe) { await Fetch(OpenEncUrl, "the open moflex encoder", Path.GetDirectoryName(OpenEncPath()), new[] { "moflex-ffmpeg.exe" }); }
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
            bool mc = File.Exists(enc) && File.Exists(rad) && File.Exists(lic);
            toolsText = string.Format("{0}  Python        {1}\n{2}  ffmpeg        {3}\n{4}  Mobiclip      {5}\n{6}  open encoder  {7}",
                ok(py), rel(Backend.Python), ok(ff), rel(bin),
                ok(mc), mc ? rel(Path.GetDirectoryName(enc)) + "  (+ RunAsDate, licence)"
                    : (File.Exists(enc) ? "" : "encoder ") + (File.Exists(rad) ? "" : "RunAsDate ") + (File.Exists(lic) ? "" : "licence ") + "missing",
                oe ? "OK     " : "-      ", oe ? rel(OpenEncPath()) : "not installed (optional: Download missing tools)");
            toolsNote = mc || oe ? "" : "No moflex encoder: video files can't be converted (moflex editing still works). Download missing tools gets the open one.";
            UpdateToolsLabel();
            toolsReady = py && ff;
            btnAdd.Enabled = btnStart.Enabled = btnOpen.Enabled = toolsReady && !queueActive;
            if (!toolsReady) { SetStatus("Python / ffmpeg missing: Settings > Download missing tools."); tabs.SelectedIndex = 5; }
            else if (lblStatus.Text == "" || lblStatus.Text.StartsWith("Tools")) SetStatus("Ready. " + (File.Exists(enc) ? "" : "(Mobiclip encoder missing: only moflex editing works.)"));
        }

        string toolsText = "", toolsNote = "";
        void UpdateToolsLabel()
        {
            string iw3 = iw3Status == null ? "...      iw3 (2D->3D) checking"
                : J.B(J.G(iw3Status, "installed"))
                    ? "OK       iw3 2D->3D    " + RelApp(J.S(J.G(iw3Status, "dir"))) + "  (" + J.A(J.G(iw3Status, "catalog")).Count(m => J.B(J.G(m, "have"))) + " depth models)"
                    : "-        iw3 2D->3D    not installed (optional)";
            lblTools.Text = toolsText + "\n" + iw3 + (toolsNote != "" ? "\n" + toolsNote : "");
        }
        string RelApp(string x) { return x.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) ? "app" + x.Substring(appDir.Length) : x; }

        // ============================================================ self-update
        static string Http(string url)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MoflexStudio/" + AppVersion;
                wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                return wc.DownloadString(url + "?t=" + DateTime.UtcNow.Ticks);   // never a cached latest.txt
            }
        }

        static bool Newer(string a, string b)   // a > b, "1.10" > "1.9"
        {
            Version va, vb;
            if (!Version.TryParse(a, out va) || !Version.TryParse(b, out vb)) return false;
            return va > vb;
        }

        async Task CheckUpdate(bool interactive)
        {
            string txt;
            try { txt = await Task.Run(() => Http(UpdateBase + "latest.txt")); }
            catch (Exception ex) { if (interactive) SetStatus("Couldn't check for updates: " + ex.Message); return; }
            var lines = txt.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l != "").ToArray();
            if (lines.Length < 2 || !Regex.IsMatch(lines[1], "^[0-9a-fA-F]{32}$")) { if (interactive) SetStatus("The update information on the server is unreadable."); return; }
            updateVer = lines[0]; updateMd5 = lines[1].ToLowerInvariant();
            if (!Newer(updateVer, AppVersion))
            {
                lblVersion.Text = "Version " + AppVersion;
                if (interactive) SetStatus("Moflex Studio " + AppVersion + " is the newest version.");
                return;
            }
            lblVersion.Text = AppVersion + " -> " + updateVer;
            btnUpdate.Text = "Update to " + updateVer;
            if (!interactive) { SetStatus("Moflex Studio " + updateVer + " is available: Settings > Update to " + updateVer + "."); return; }
            if (queueActive) { SetStatus("Finish or stop the queue first: updating restarts the app."); return; }
            if (MessageBox.Show(this, "Moflex Studio " + updateVer + " is available (you have " + AppVersion + ").\n\n" +
                "Update now? It downloads the new version (about 10 MB) and restarts the app. Your settings, queue and tools are kept.",
                "Update Moflex Studio", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await InstallUpdate();
        }

        async Task InstallUpdate()
        {
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            string ud = Path.Combine(appDir, "update"), zip = Path.Combine(ud, "Moflex.Studio.zip"), staged = Path.Combine(ud, "staged");
            btnUpdate.Enabled = false;
            try
            {
                if (Directory.Exists(ud)) Directory.Delete(ud, true);
                Directory.CreateDirectory(ud);
                pbTools.Visible = true; pbTools.Value = 0;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "MoflexStudio/" + AppVersion;
                    wc.DownloadProgressChanged += (s, e) =>
                    {
                        pbTools.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                        SetStatus(string.Format("Downloading Moflex Studio {0}... {1:0.0} / {2:0.0} MB", updateVer, e.BytesReceived / 1048576.0, e.TotalBytesToReceive / 1048576.0));
                    };
                    await wc.DownloadFileTaskAsync(new Uri(UpdateBase + "Moflex.Studio.zip?v=" + Uri.EscapeDataString(updateVer)), zip);
                }
                SetStatus("Checking the download...");
                string got = await Task.Run(() =>
                {
                    using (var md5 = MD5.Create()) using (var fs = File.OpenRead(zip))
                        return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                });
                if (got != updateMd5) throw new Exception("the download was damaged (checksum mismatch); try again");
                SetStatus("Unpacking...");
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged));
                string top = Path.Combine(staged, "Moflex Studio");
                if (!File.Exists(Path.Combine(top, "Moflex Studio.exe"))) throw new Exception("the update has no Moflex Studio.exe in it");
                File.Delete(zip);
                // the running exe can't overwrite itself: a script waits for this process to end, copies the
                // new files over the install (settings, queue, python and ffmpeg aren't in the bundle, so they stay),
                // starts the new version and removes the staging folder
                string cmd = Path.Combine(ud, "update.cmd");
                File.WriteAllText(cmd, string.Join("\r\n", new[] {
                    "@echo off",
                    ":wait",
                    "tasklist /FI \"PID eq " + Process.GetCurrentProcess().Id + "\" 2>nul | find \"" + Process.GetCurrentProcess().Id + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
                    "robocopy \"" + top + "\" \"" + root + "\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                    "start \"\" \"" + Path.Combine(root, "Moflex Studio.exe") + "\"",
                    "cd /d \"" + root + "\"",
                    "rd /s /q \"" + ud + "\"" }) + "\r\n", Encoding.ASCII);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = root });
                SetStatus("Restarting into Moflex Studio " + updateVer + "...");
                SaveQueue();
                Application.Exit();
            }
            catch (Exception ex)
            {
                SetStatus("Update failed: " + ex.Message);
                try { if (Directory.Exists(ud)) Directory.Delete(ud, true); } catch { }
            }
            finally { btnUpdate.Enabled = true; pbTools.Visible = false; }
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
                    await ApplyShowCarry(pr, job);
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
            job["encoder"] = EncoderChoice();
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
                    // every other audio stream becomes track 2, 3, ... (up to 7): reorder or remove them on the Audio tab
                    var extra = new List<object>();
                    foreach (var au in streams)
                    {
                        if (au == main || extra.Count >= MaxExtraAudio) continue;
                        string sl = J.S(J.G(au, "lang")).ToLowerInvariant(), t = J.S(J.G(au, "title")).ToLowerInvariant();
                        string lbl = t.Contains("comment") ? "com" : (Regex.IsMatch(sl, "^[a-z][a-z0-9]{1,2}$") && sl != "und" ? sl : "a" + (extra.Count + 2));
                        extra.Add(new Dict { { "file", null }, { "stream", J.I(J.G(au, "index")) }, { "offset", 0.0 }, { "lang", lbl }, { "label", lbl },
                            { "desc", string.Format("Source #{0}  {1}  {2} {3}ch  {4}", J.I(J.G(au, "index")), J.S(J.G(au, "lang")).ToUpper(), J.S(J.G(au, "codec")), J.I(J.G(au, "channels")), J.S(J.G(au, "title"))).Trim() } });
                    }
                    aud["extra"] = extra.ToArray();
                }
                else { aud["main"] = new Dict(); aud["extra"] = new object[0]; }
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
                md["kind"] = se.Length == 2 ? "episode" : "movie";
                md["format"] = "converted";
                md["runtime"] = Minutes(pr);
                job["metadata"] = md;
            }
            else
            {
                var mf = J.G(pr, "moflex");
                aud["main"] = new Dict { { "keep", true } };
                aud["extra"] = J.A(J.G(mf, "audios")).Select(a => (object)new Dict { { "keep", J.I(J.G(a, "existing")) },
                    { "label", J.S(J.G(a, "lang")).ToLowerInvariant() }, { "desc", AudioPicker.KeepText(a) } }).ToArray();
                var vaud = J.A(J.G(mf, "vaud")).Select(x => J.I(x)).ToArray();
                job["main_default_audio"] = vaud.Length > 0 ? vaud[0] : 1;
                job["versions"] = J.A(J.G(mf, "videos")).Select(v => (object)new Dict { { "existing", J.I(J.G(v, "existing")) },
                    { "default_audio", J.I(J.G(v, "existing")) + 1 < vaud.Length ? vaud[J.I(J.G(v, "existing")) + 1] : 1 },
                    { "label", J.S(J.G(v, "label")) }, { "desc", "Current version " + (J.I(J.G(v, "existing")) + 2) } }).ToArray();
                job["main_label"] = J.S(J.G(mf, "vid_main"));
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
                    md["year"] = J.I(J.G(nfo, "year")); md["runtime"] = J.I(J.G(nfo, "runtime")) > 0 ? J.I(J.G(nfo, "runtime")) : Minutes(pr);
                    // season / episode / episode title / type: the file's own fields, the file name only for
                    // files made before those were stored (the backend does the fallback)
                    var mff = J.G(pr, "meta_from_file");
                    var se = J.A(J.G(mff, "season_episode"));
                    md["season_episode"] = se.Length == 2 ? new object[] { J.I(se[0]), J.I(se[1]) } : null;
                    md["eptitle"] = J.S(J.G(mff, "eptitle"));
                    md["kind"] = J.S(J.G(mff, "kind"));
                    if (J.S(J.G(mff, "tmdb_id")) != "") md["tmdb_id"] = J.S(J.G(mff, "tmdb_id"));
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
            job["output_dir"] = DefaultOutDir();
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
            bool fresh = job == null;
            if (fresh) { job = DefaultJob(pr); await ApplyShowCarry(pr, job); }
            await LoadIntoEditor(pr, job);
            SetStatus(fresh && showCarry != null && chkCarry.Checked && J.S(J.G(J.G(job, "metadata"), "title")) == J.S(J.G(showCarry, "title"))
                ? "Filled in from " + J.S(J.G(showCarry, "title")) + " (untick the box on Info + poster to stop)." : "");
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
            {
                var cv = J.D(J.G(job, "conv3d"));
                tplRefilling = true;
                try { SelectItem(cmbTpl, cv != null && J.S(J.G(cv, "template")) != "" ? J.S(J.G(cv, "template")) : "best"); }
                finally { tplRefilling = false; }
                conv3dOver = new Dict();
                if (cv != null) foreach (var kv in cv) if (kv.Key != "enabled" && kv.Key != "template" && kv.Key != "keep_sbs") conv3dOver[kv.Key] = kv.Value;
                chkConv3d.Checked = cv != null && J.B(J.G(cv, "enabled"));
                chkKeepSbs.Checked = cv != null && J.B(J.G(cv, "keep_sbs"));
                chkConv3d.Enabled = kind == "video";
            }
            int br = J.I(J.G(job, "bitrate")); numBitrate.Value = br >= 200 ? Math.Min(8000, br) : numDefBitrate.Value;

            // audio
            audio1.Setup(pr); audio2.Setup(pr);
            await audio1.FromJson(J.G(J.G(job, "audio"), "main"));
            lvAud.Items.Clear();
            var ja = J.G(job, "audio");
            if (J.Has(ja, "extra")) foreach (var x in J.A(J.G(ja, "extra"))) { var d = J.D(x); if (d != null) AddAudRow(J.Clone(d)); }
            else if (J.D(J.G(ja, "second")) != null)                 // a job saved by an older build
            {
                var d = J.Clone(J.D(J.G(ja, "second")));
                if (J.G(d, "keep") is bool) d["keep"] = 0;
                AddAudRow(d);
            }
            RenumberAud();
            await audio2.FromJson(null);
            lvVer.Items.Clear();
            var vrows = J.A(J.G(job, "versions")).Select(vx => J.D(vx)).Where(vx => vx != null).Select(vx => J.Clone(vx)).ToList();
            if (!vrows.Any(vx => J.B(J.G(vx, "self"))))                    // this file's own video: row 1 unless moved
                vrows.Insert(0, new Dict { { "self", true }, { "label", J.S(J.G(job, "main_label")) }, { "default_audio", J.I(J.G(job, "main_default_audio")) } });
            foreach (var vd in vrows) AddVerRow(vd);
            RenumberVer();
            txtVerLabel.Text = ""; cmbVerLayout.SelectedIndex = 0;
            for (int vi = 0; vi < vrows.Count; vi++)
                if (J.S(J.G(vrows[vi], "source")) != "") await OfferVersionStreams(J.S(J.G(vrows[vi], "source")), vi + 1, false);

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
            int rt = J.I(J.G(md, "runtime")); if (rt <= 0) rt = Minutes(pr); txtRuntime.Text = rt > 0 ? rt.ToString() : "";
            curTmdbId = J.S(J.G(md, "tmdb_id"));
            txtDate.Text = J.S(J.G(md, "date"));
            txtDesc.Text = J.S(J.G(md, "desc")).Replace("\r", "").Replace("\n", "\r\n");
            origShowDesc = J.S(J.G(md, "showdesc"));
            cmbCategory.Text = J.S(J.G(md, "category")) == "" ? "Movies" : J.S(J.G(md, "category"));
            cmbFormat.SelectedIndex = J.S(J.G(md, "format")) == "native" ? 1 : 0;
            var se = J.A(J.G(md, "season_episode"));
            int ki = Array.IndexOf(KindNames, J.S(J.G(md, "kind")));
            if (ki < 0)                                             // a file from before kind= was stored
            {
                int ci = Array.IndexOf(KindShelf, cmbCategory.Text);
                ki = se.Length == 2 ? 1 : (ci == 2 || ci == 3) ? ci : 0;
            }
            fillingEditor = true;                                   // the file's own shelf stays as it is
            cmbKind.SelectedIndex = ki;
            fillingEditor = false;
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
            if (rad3D.Checked && lay == "2d" && chkConv3d.Checked)
                s = "2D \u2192 3D: iw3 makes the full-size picture 3D first (" + Iw3Summary() + "), then it's encoded as 3D. Slow: a top graphics card does " +
                    "about 0.6x real time with Best, so a 2-hour film takes ~3 hours. The file's info records how it was made 3D.";
            else if (rad3D.Checked && lay == "2d") s += "3D output needs a side-by-side source. Set the layout to HSBS or FSBS, tick 2D \u2192 3D (iw3), or choose 2D output.";
            else if (rad3D.Checked) s += "3D: the left and right halves become the two eyes (" + (EncoderChoice() == "open"
                ? "the open encoder, straight from the source)." : "lossless 800x240 AVI, then the official Mobiclip encoder, 2 passes).");
            else if (lay != "2d") s += "2D from a side-by-side source: only the left eye is used.";
            else s += EncoderChoice() == "open" ? "2D: the open encoder, straight from the source." : "2D: lossless 400x240 AVI, then the official Mobiclip encoder (2 passes).";
            lblVideoNote.Text = s;
        }

        // ---- shared list helpers
        void RenumberList(ListView lv)
        {
            for (int i = 0; i < lv.Items.Count; i++) lv.Items[i].Text = (i + 2).ToString();   // track / version 2, 3, ...
        }

        void MoveRow(ListView lv, int dir)
        {
            if (lv.SelectedItems.Count == 0) return;
            var it = lv.SelectedItems[0]; int i = it.Index, j = i + dir;
            if (j < 0 || j >= lv.Items.Count) return;
            lv.Items.RemoveAt(i); lv.Items.Insert(j, it); it.Selected = true;
            if (lv == lvAud) RenumberAud(); else if (lv == lvVer) RenumberVer(); else RenumberList(lv);
        }

        // ---- extra audio tracks (2..8)
        const int MaxExtraAudio = 7;

        static string AudDesc(Dict d, Dict pr)
        {
            if (J.Has(d, "desc")) return J.S(d["desc"]);
            if (J.Has(d, "keep"))
            {
                object kv = d["keep"]; int k = kv is bool ? 0 : J.I(kv);
                var a = J.A(J.G(J.G(pr, "moflex"), "audios")).FirstOrDefault(x => J.I(J.G(x, "existing")) == k);
                return a != null ? AudioPicker.KeepText(a) : "Current track " + (k + 2);
            }
            string f = J.S(J.G(d, "file"));
            return (f == "" ? "Source" : Path.GetFileName(f)) + " stream #" + J.I(J.G(d, "stream"));
        }

        void AddAudRow(Dict d)
        {
            if (J.S(J.G(d, "_id")) == "") d["_id"] = Guid.NewGuid().ToString("N").Substring(0, 10);
            var lvi = new ListViewItem(new[] { "", J.S(J.G(d, "label")).ToUpper(), AudDesc(d, probe),
                J.F(J.G(d, "offset")) == 0 ? "" : J.F(J.G(d, "offset")).ToString("0.###", J.CI) });
            lvi.Tag = d; lvAud.Items.Add(lvi);
        }

        void RenumberAud()
        {
            RenumberList(lvAud);
            lblAudCount.Text = "Tracks 2-8: " + lvAud.Items.Count + " / " + MaxExtraAudio;
            lblAudCount.ForeColor = lvAud.Items.Count > MaxExtraAudio ? Color.Firebrick : SystemColors.GrayText;
            RefreshVerAudio();
        }

        void AddAudFromUi(bool update)
        {
            if (probe == null) { SetStatus("Open a source file first."); return; }
            string err = audio2.Validate(); if (err != null) { SetStatus(err); return; }
            var d = audio2.ToJson() as Dict;
            if (d == null || d.Count == 0) { SetStatus("Choose where the track comes from first."); return; }
            if (!J.Has(d, "label") || J.S(d["label"]) == "") d["label"] = J.Has(d, "lang") ? J.S(d["lang"]) : "alt";
            d["desc"] = audio2.Describe();
            if (update)
            {
                if (lvAud.SelectedItems.Count == 0) { SetStatus("Select a track in the list first."); return; }
                var it = lvAud.SelectedItems[0];
                d["_id"] = J.S(J.G(it.Tag, "_id"));          // versions point at this track: keep its identity
                it.Tag = d; it.SubItems[1].Text = J.S(d["label"]).ToUpper(); it.SubItems[2].Text = J.S(d["desc"]);
                RefreshVerAudio();
                it.SubItems[3].Text = J.F(J.G(d, "offset")) == 0 ? "" : J.F(J.G(d, "offset")).ToString("0.###", J.CI);
                return;
            }
            if (lvAud.Items.Count >= MaxExtraAudio) { SetStatus("The player holds 8 audio tracks: track 1 plus 7 more."); return; }
            AddAudRow(d); RenumberAud();
        }

        // ---- alternate video versions
        const int MaxVersions = 3;

        static string VerLabelCheck(string l, string fallback)
        {
            l = (l ?? "").Trim(); if (l == "") l = fallback;
            return Regex.IsMatch(l, "^[ -~]{1,7}$") ? l : null;   // the player's font: plain ASCII, 7 characters
        }

        void RenumberVer()
        {
            RefreshVerAudio();
            for (int i = 0; i < lvVer.Items.Count; i++)
            {
                lvVer.Items[i].Text = i == 0 ? "1 main" : (i + 1).ToString();
                lvVer.Items[i].Font = i == 0 ? new Font(lvVer.Font, FontStyle.Bold) : lvVer.Font;
            }
        }

        void AddVerRow(Dict d)
        {
            if (J.S(J.G(d, "audio_ref")) == "")
            {
                int n = J.I(J.G(d, "default_audio"));
                d["audio_ref"] = n >= 2 && n - 2 < lvAud.Items.Count ? J.S(J.G(lvAud.Items[n - 2].Tag, "_id")) : "t1";
            }
            string desc = J.B(J.G(d, "self")) ? "This file's video" + (probe != null ? "  (" + Path.GetFileName(J.S(J.G(probe, "path"))) + ")" : "")
                : J.Has(d, "desc") ? J.S(d["desc"]) : J.Has(d, "existing") && !J.Has(d, "source")
                ? "Current version " + (J.I(d["existing"]) + 2) : Path.GetFileName(J.S(J.G(d, "source")));
            var lvi = new ListViewItem(new[] { "", J.S(J.G(d, "label")), desc, "" }); lvi.Tag = d; lvVer.Items.Add(lvi);
        }

        async void AddVersionFromUi()
        {
            if (probe == null) { SetStatus("Open a source file first."); return; }
            if (lvVer.Items.Count >= MaxVersions + 1) { SetStatus("The player holds 4 versions: the main one plus 3 more."); return; }
            string f;
            using (var d = new OpenFileDialog { Title = "Another version of the same film (video file or moflex)",
                Filter = "Video|*" + string.Join(";*", VideoExt) + "|All files|*.*" })
            {
                try { d.InitialDirectory = Path.GetDirectoryName(J.S(J.G(probe, "path"))); } catch { }
                if (d.ShowDialog() != DialogResult.OK) return;
                f = d.FileName;
            }
            string l = VerLabelCheck(txtVerLabel.Text, "V" + (lvVer.Items.Count + 1));
            if (l == null) { SetStatus("A version name is up to 7 plain characters (e.g. B&W, COLOR, IMAX)."); return; }
            bool mf = f.EndsWith(".moflex", StringComparison.OrdinalIgnoreCase);
            var lit = cmbVerLayout.SelectedItem as Item;
            var row = new Dict { { "source", f }, { "label", l }, { "source_type", lit == null ? "auto" : (string)lit.Tag },
                { "desc", Path.GetFileName(f) + (mf ? "  (moflex: used as it is)" : "  (encoded like the main one)") } };
            AddVerRow(row); RenumberVer();
            txtVerLabel.Text = "";
            await OfferVersionStreams(f, lvVer.Items.Count, true);
        }

        /* a version file's own audio and text subtitle streams: always offered in the Audio and Subtitles
         * "add" boxes; with autoAdd, the ones in a language the film doesn't have yet go straight into the
         * lists (the same timeline, so they line up). They're ordinary tracks: the player's lists are global. */
        async Task OfferVersionStreams(string f, int row, bool autoAdd)
        {
            Dict pr;
            try { pr = await ProbeDict(f); }
            catch (Exception ex) { SetStatus("Could not read " + Path.GetFileName(f) + ": " + ex.Message); return; }
            string what = "Version " + row + " (" + Path.GetFileName(f) + ")";
            audio2.AddFileStreams(f, what, pr);
            foreach (var st in J.A(J.G(pr, "subtitles")))
            {
                if (J.B(J.G(st, "image"))) continue;
                string txt = string.Format("{0} stream #{1}: {2} {3} {4}", what, J.I(J.G(st, "index")), J.S(J.G(st, "lang")).ToUpper(), J.S(J.G(st, "codec")), J.S(J.G(st, "title"))).Trim();
                cmbSubFrom.Items.Insert(Math.Max(0, cmbSubFrom.Items.Count - 2), new Item(txt, new Dict { { "file", f }, { "stream", J.I(J.G(st, "index")) },
                    { "label", SubLabel(J.S(J.G(st, "lang")), J.S(J.G(st, "title")), J.B(J.G(st, "forced"))) }, { "desc", txt }, { "from_version", f } }));
            }
            if (!autoAdd) return;
            // languages the film already has: track 1 and the list
            var t1 = audio1.ToJson() as Dict;
            string lang1 = J.S(J.G(t1, "lang")); if (lang1 == "") lang1 = J.S(J.G(J.G(probe, "moflex"), "lang_main"));
            var haveA = new HashSet<string>(lvAud.Items.Cast<ListViewItem>().Select(i => J.S(J.G(i.Tag, "label")).ToLowerInvariant()));
            if (lang1 != "") haveA.Add(lang1.ToLowerInvariant().Substring(0, Math.Min(3, lang1.Length)));
            var haveS = new HashSet<string>(lvSubs.Items.Cast<ListViewItem>().Select(i => J.S(J.G(i.Tag, "label")).ToLowerInvariant()));
            int na = 0, ns = 0;
            foreach (var a in J.A(J.G(pr, "audio")))
            {
                string l = J.S(J.G(a, "lang")).ToLowerInvariant(), t = J.S(J.G(a, "title")).ToLowerInvariant();
                if (t.Contains("comment")) l = "com";
                if (!Regex.IsMatch(l, "^[a-z][a-z0-9]{1,2}$") || l == "und" || haveA.Contains(l) || lvAud.Items.Count >= MaxExtraAudio) continue;
                AddAudRow(new Dict { { "file", f }, { "stream", J.I(J.G(a, "index")) }, { "offset", 0.0 }, { "lang", l }, { "label", l }, { "from_version", f },
                    { "desc", string.Format("{0}  #{1}  {2}  {3} {4}ch", what, J.I(J.G(a, "index")), J.S(J.G(a, "lang")).ToUpper(), J.S(J.G(a, "codec")), J.I(J.G(a, "channels"))) } });
                haveA.Add(l); na++;
            }
            foreach (var st in J.A(J.G(pr, "subtitles")))
            {
                if (J.B(J.G(st, "image")) || lvSubs.Items.Count >= 16) continue;
                string l = SubLabel(J.S(J.G(st, "lang")), J.S(J.G(st, "title")), J.B(J.G(st, "forced")));
                if (haveS.Contains(l)) continue;
                AddSubRow(new Dict { { "file", f }, { "stream", J.I(J.G(st, "index")) }, { "label", l }, { "offset", 0.0 }, { "from_version", f },
                    { "desc", string.Format("{0} stream #{1} {2} {3}", what, J.I(J.G(st, "index")), J.S(J.G(st, "codec")), J.S(J.G(st, "title"))).Trim() } });
                haveS.Add(l); ns++;
            }
            RenumberAud(); RenumberSubs();
            int nAudio = J.A(J.G(pr, "audio")).Length, nSubs = J.A(J.G(pr, "subtitles")).Count(x => !J.B(J.G(x, "image")));
            SetStatus(string.Format("{0}: {1} audio and {2} subtitle stream(s). Added {3} audio track(s) and {4} subtitle track(s) in new languages; the rest are in the Audio / Subtitles add boxes.",
                Path.GetFileName(f), nAudio, nSubs, na, ns));
        }

        void ForgetVersionStreams(string f)
        {
            foreach (var lv in new[] { lvAud, lvSubs })
                for (int i = lv.Items.Count - 1; i >= 0; i--)
                    if (string.Equals(J.S(J.G(lv.Items[i].Tag, "from_version")), f, StringComparison.OrdinalIgnoreCase)) lv.Items.RemoveAt(i);
            for (int i = cmbSubFrom.Items.Count - 1; i >= 0; i--)
            {
                var it = cmbSubFrom.Items[i] as Item;
                if (it != null && string.Equals(J.S(J.G(it.Tag, "from_version")), f, StringComparison.OrdinalIgnoreCase)) cmbSubFrom.Items.RemoveAt(i);
            }
            if (cmbSubFrom.SelectedIndex < 0 && cmbSubFrom.Items.Count > 0) cmbSubFrom.SelectedIndex = 0;
            audio2.RemoveFileStreams(f);
            RenumberAud(); RenumberSubs();
        }

        void UpdateSelectedVersion()
        {
            if (lvVer.SelectedItems.Count == 0) { SetStatus("Select a version in the list first."); return; }
            var it = lvVer.SelectedItems[0]; var d = (Dict)it.Tag;
            string l = VerLabelCheck(txtVerLabel.Text, J.S(J.G(d, "label")));
            if (l == null) { SetStatus("A version name is up to 7 plain characters (e.g. B&W, COLOR, IMAX)."); return; }
            d["label"] = l; it.SubItems[1].Text = l;
            var lit = cmbVerLayout.SelectedItem as Item;
            if (J.Has(d, "source")) d["source_type"] = lit == null ? "auto" : (string)lit.Tag;
            var ait = cmbVerAudio.SelectedItem as Item;
            if (ait != null) d["audio_ref"] = (string)ait.Tag;
            RefreshVerAudio();
        }

        // ---- each version's default audio: "t1" = track 1, else the _id of a row in the audio list
        string Track1Lang()
        {
            string l = J.S(J.G(audio1.ToJson(), "lang"));
            if (l == "") l = J.S(J.G(J.G(probe, "moflex"), "lang_main"));
            return l.ToUpper();
        }

        // the track number a reference points at now (1 = track 1), or 0 if that track is gone
        int TrackNumber(string aref)
        {
            if (aref == "" || aref == "t1") return 1;
            for (int i = 0; i < lvAud.Items.Count; i++) if (J.S(J.G(lvAud.Items[i].Tag, "_id")) == aref) return i + 2;
            return 0;
        }

        void RefreshVerAudio()
        {
            if (cmbVerAudio == null) return;
            var keep = cmbVerAudio.SelectedItem as Item;
            cmbVerAudio.Items.Clear();
            string l1 = Track1Lang();
            cmbVerAudio.Items.Add(new Item("Track 1" + (l1 != "" ? "  " + l1 : ""), "t1"));
            for (int i = 0; i < lvAud.Items.Count; i++)
                cmbVerAudio.Items.Add(new Item("Track " + (i + 2) + "  " + J.S(J.G(lvAud.Items[i].Tag, "label")).ToUpper(), J.S(J.G(lvAud.Items[i].Tag, "_id"))));
            if (keep == null || !SelectItemQuiet(cmbVerAudio, (string)keep.Tag)) cmbVerAudio.SelectedIndex = 0;
            foreach (ListViewItem it in lvVer.Items)
            {
                var d = (Dict)it.Tag;
                int n = TrackNumber(J.S(J.G(d, "audio_ref")));
                if (n == 0) { d["audio_ref"] = "t1"; n = 1; }                    // its track was removed: back to track 1
                string lab = n == 1 ? l1 : J.S(J.G(lvAud.Items[n - 2].Tag, "label")).ToUpper();
                while (it.SubItems.Count < 4) it.SubItems.Add("");
                it.SubItems[3].Text = "Track " + n + (lab != "" ? "  " + lab : "");
            }
        }

        static bool SelectItemQuiet(ComboBox c, string tag)
        {
            for (int i = 0; i < c.Items.Count; i++) if ((string)((Item)c.Items[i]).Tag == tag) { c.SelectedIndex = i; return true; }
            return false;
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

        // ---- runtime from the file itself
        static int Minutes(Dict pr)
        {
            double d = J.F(J.G(pr, "duration"));
            return d > 0 ? Math.Max(1, (int)Math.Round(d / 60.0)) : 0;
        }

        // ---- TV runs: the next episodes reuse the last queued episode's show info + poster
        void CaptureShowCarry(Dict job)
        {
            var md = J.D(J.G(job, "metadata"));
            if (md == null || J.S(J.G(md, "title")) == "") return;          // "keep the file's info": leave the run alone
            var se = J.A(J.G(md, "season_episode"));
            if (se.Length != 2) { showCarry = null; UpdateCarryBox(); return; }   // a movie ends the run
            var c = new Dict();
            foreach (var k in new[] { "title", "genres", "category", "format", "showdesc", "tmdb_id" }) c[k] = J.S(J.G(md, k));
            c["year"] = J.I(J.G(md, "year"));
            c["season"] = J.I(se[0]); c["episode"] = J.I(se[1]);
            string a = J.S(J.G(job, "art"));
            if (a != "" && a != "keep" && a != "none") c["art"] = a;
            showCarry = c;
            UpdateCarryBox();
        }

        void UpdateCarryBox()
        {
            chkCarry.Visible = showCarry != null;
            if (showCarry == null) return;
            chkCarry.Text = string.Format("Next episodes: use the info{0} of {1} ({2})   [last queued: S{3:00}e{4:00}]",
                J.Has(showCarry, "art") ? " and poster" : "", J.S(J.G(showCarry, "title")), J.I(J.G(showCarry, "year")),
                J.I(J.G(showCarry, "season")), J.I(J.G(showCarry, "episode")));
        }

        async Task ApplyShowCarry(Dict pr, Dict job)
        {
            if (showCarry == null || !chkCarry.Checked) return;
            var md = J.D(J.G(job, "metadata"));
            if (md == null) { md = new Dict(); job["metadata"] = md; }
            if (J.B(J.G(md, "_from_file"))) return;                          // a SUPER MOFLEX with its own info: leave it
            var se = J.A(J.G(J.G(pr, "parsed"), "season_episode"));
            // only files that look like episodes: S01E05 / 1x05 in the name, or the show's own name -- never a movie dropped after a TV run
            Func<string, string> norm = x => Regex.Replace(x.ToLowerInvariant(), "[^a-z0-9]", "");
            string pt = norm(J.S(J.G(J.G(pr, "parsed"), "title"))), st = norm(J.S(J.G(showCarry, "title")));
            if (se.Length != 2 && (pt == "" || st == "" || !(pt.Contains(st) || st.Contains(pt)))) return;
            int s = se.Length == 2 ? J.I(se[0]) : J.I(J.G(showCarry, "season"));
            int e = se.Length == 2 ? J.I(se[1]) : J.I(J.G(showCarry, "episode")) + 1;
            foreach (var k in new[] { "title", "genres", "category", "format", "showdesc", "tmdb_id" }) md[k] = J.S(J.G(showCarry, k));
            md["year"] = J.I(J.G(showCarry, "year"));
            md["season_episode"] = new object[] { s, e }; md["kind"] = "episode";
            md["eptitle"] = ""; md["date"] = ""; md["desc"] = J.S(J.G(showCarry, "showdesc"));
            if (J.Has(showCarry, "art")) job["art"] = showCarry["art"];
            showCarry["season"] = s; showCarry["episode"] = e;               // so a file without S/E in its name counts on
            UpdateCarryBox();
            string tid = J.S(J.G(showCarry, "tmdb_id"));
            if (tid == "") return;
            try
            {
                var d = await Backend.Call("tmdb-details", "tv", tid, s + ":" + e);
                md["eptitle"] = J.S(J.G(d, "eptitle"));
                md["date"] = J.S(J.G(d, "date"));
                if (J.S(J.G(d, "eptitle")) != "") md["desc"] = J.S(J.G(d, "desc"));
            }
            catch (Exception ex) { Log(string.Format("! TMDB S{0:00}e{1:00}: {2}", s, e, ex.Message)); }
        }

        // ---- info tab
        async Task TmdbSearch()
        {
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
            if (tv && numE.Value > 0) args.Add(numS.Value + ":" + numE.Value);   // TMDB has season 0 (specials), no episode 0
            SetStatus("Getting TMDB details...");
            try
            {
                var d = await Backend.Call(args.ToArray());
                txtTitle.Text = J.S(J.G(d, "title"));
                int y = J.I(J.G(d, "year")); txtYear.Text = y > 0 ? y.ToString() : "";
                txtGenres.Text = J.S(J.G(d, "genres"));
                int rt = Minutes(probe); if (rt <= 0) rt = J.I(J.G(d, "runtime"));   // the file's real length beats TMDB's
                txtRuntime.Text = rt > 0 ? rt.ToString() : "";
                curTmdbId = J.S(J.G(it.Tag, "id"));
                txtDate.Text = J.S(J.G(d, "date"));
                txtDesc.Text = J.S(J.G(d, "desc"));
                txtEpTitle.Text = J.S(J.G(d, "eptitle"));
                origShowDesc = J.S(J.G(d, "showdesc"));
                if (cmbCategory.Text.Trim() == "" || KindShelf.Contains(cmbCategory.Text.Trim()))
                    cmbCategory.Text = J.S(J.G(d, "category"));          // a shelf of its own (Anime...) stays
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
                if (cmbKind.SelectedIndex == 1 && (numS.Value > 0 || numE.Value > 0))
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
            lvAud.Items.Clear(); RenumberAud(); lvVer.Items.Clear(); txtVerLabel.Text = "";
            lvSubs.Items.Clear(); cmbSubFrom.Items.Clear(); txtSubLabel.Text = ""; numSubOffset.Value = 0; RenumberSubs();
            foreach (var t in new[] { txtTitle, txtEpTitle, txtGenres, txtYear, txtRuntime, txtDate, txtDesc, txtQuery, txtOutName }) t.Text = "";
            cmbResults.Items.Clear(); cmbCategory.Text = "Movies"; cmbFormat.SelectedIndex = 0; cmbKind.SelectedIndex = 0; numS.Value = numE.Value = 0;
            origShowDesc = ""; btnArtKeep.Enabled = false; SetArt("none", null);
            curTmdbId = "";
            txtOutDir.Text = DefaultOutDir(); chkSuper.Checked = true; chkOverwrite.Checked = false;
            btnAdd.Text = "Add to queue"; btnCancelEdit.Visible = false; lblEditing.Text = "";
            chkConv3d.Checked = false; chkKeepSbs.Checked = false; conv3dOver.Clear(); SelectItem(cmbTpl, "best");
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
            job["encoder"] = EncoderChoice();
            if (kind == "video" && job["encoder"].ToString() == "open" && !File.Exists(OpenEncPath()))
            { error = "The open encoder isn't installed: Settings > Download missing tools (or choose Mobiclip)."; tabs.SelectedIndex = 5; return null; }
            if (kind == "video" && rad3D.Checked)
            {
                string lay = (string)job["source_type"];
                if (lay == "auto") lay = J.S(J.G(J.G(probe, "video"), "detected"));
                if (lay == "2d" && !chkConv3d.Checked) { error = "3D output needs a side-by-side source: set Video > Source layout to HSBS/FSBS, tick 2D \u2192 3D (iw3), or choose 2D."; tabs.SelectedIndex = 0; return null; }
                if (lay == "2d" && chkConv3d.Checked)
                {
                    if (!J.B(J.G(iw3Status, "installed"))) { error = "2D \u2192 3D needs iw3: Video > Install iw3."; tabs.SelectedIndex = 0; return null; }
                    var cv = new Dict(conv3dOver);
                    cv["enabled"] = true; cv["template"] = TplName(); cv["keep_sbs"] = chkKeepSbs.Checked;
                    job["conv3d"] = cv;
                }
            }
            error = audio1.Validate();
            if (error != null) { tabs.SelectedIndex = 1; return null; }
            if (lvAud.Items.Count > MaxExtraAudio) { error = "At most 8 audio tracks (track 1 plus 7)."; tabs.SelectedIndex = 1; return null; }
            job["audio"] = new Dict { { "main", audio1.ToJson() ?? new Dict() },
                { "extra", lvAud.Items.Cast<ListViewItem>().Select(i => (object)J.Clone((Dict)i.Tag)).ToArray() } };
            if (lvVer.Items.Count > MaxVersions + 1) { error = "At most 4 video versions (the main one plus 3)."; tabs.SelectedIndex = 0; return null; }
            var vrowsOut = lvVer.Items.Cast<ListViewItem>().Select(i => J.Clone((Dict)i.Tag)).ToList();
            foreach (var vd in vrowsOut) { vd.Remove("desc"); int tn = TrackNumber(J.S(J.G(vd, "audio_ref"))); vd["default_audio"] = tn < 1 ? 1 : tn; }
            job["main_label"] = vrowsOut.Count > 0 ? J.S(J.G(vrowsOut[0], "label")) : "";
            // just this file's video = no versions; otherwise the list in order, row 1 the main one
            job["versions"] = vrowsOut.Count <= 1 ? new object[0] : vrowsOut.Select(vd2 => (object)vd2).ToArray();
            if (!chkSuper.Checked && (lvAud.Items.Count > 0 || lvVer.Items.Count > 1))
            { error = "Extra audio tracks and video versions live in the SUPER MOFLEX trailer: tick SUPER MOFLEX on the Output tab, or remove them."; tabs.SelectedIndex = 4; return null; }
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
                    // season 0 = the show's specials, episode 0 = a season's special: only S00E00 says nothing
                    { "season_episode", tv && (numS.Value > 0 || numE.Value > 0) ? new object[] { (int)numS.Value, (int)numE.Value } : null },
                    { "kind", KindNames[Math.Max(0, cmbKind.SelectedIndex)] } };
                if (curTmdbId != "") md["tmdb_id"] = curTmdbId;
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
            // FAT32 (the 3DS SD card) can't hold a file of 4 GB or more: refuse up front, not after an hour of encoding
            {
                double dur = J.F(J.G(probe, "duration")), adpcm = 44100.0 / 1024 * (8 + 1024);   // bytes/s per ADPCM track
                double est = kind == "video" ? (int)numBitrate.Value * 1000.0 / 8 * dur * 1.03 + adpcm * dur
                                             : new FileInfo(J.S(J.G(probe, "path"))).Length;
                foreach (var ex in J.A(J.G(J.G(job, "audio"), "extra"))) if (!J.Has(ex, "keep")) est += adpcm * dur;
                foreach (var vx in J.A(J.G(job, "versions")))
                {
                    string vs = J.S(J.G(vx, "source"));
                    if (vs == "") continue;   // a kept version is already counted in the file's size
                    est += vs.EndsWith(".moflex", StringComparison.OrdinalIgnoreCase) && File.Exists(vs) ? new FileInfo(vs).Length
                         : (int)numBitrate.Value * 1000.0 / 8 * dur * 1.03 + adpcm * dur;
                }
                if (est > 4294967295.0)
                {
                    error = string.Format("The output would be about {0:0.00} GB, over the 4 GB a 3DS (FAT32) SD card can hold. Lower the bitrate on the Video tab, or drop track 2.", est / 1073741824.0);
                    tabs.SelectedIndex = 0; return null;
                }
            }
            bool encodes = kind == "video" || J.A(job["versions"]).Any(vv => J.S(J.G(vv, "source")) != "" && !J.S(J.G(vv, "source")).EndsWith(".moflex", StringComparison.OrdinalIgnoreCase));
            if (encodes && !File.Exists(Path.Combine(ToolPath("encoder_dir", Path.Combine(appDir, "encoder")), "MobiclipMulticoreEncoder.exe")))
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
            RememberOutDir(J.S(job["output_dir"]));
            CaptureShowCarry(job);
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
            q.Lvi.ForeColor = q.Status.StartsWith("Error") ? Color.Firebrick : q.Status.StartsWith("Done") ? Color.SeaGreen : q.Status == "Stopped" || q.Status == "Interrupted" || q.Status == "Paused" ? Color.DarkOrange : SystemColors.WindowText;
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
                    if (q.Status == "" || q.Status == "Interrupted" || !(q.Status.StartsWith("Done") || q.Status.StartsWith("Error") || q.Status == "Stopped")) q.Status = "Pending";
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
            btnStart.Enabled = !on && toolsReady; btnStop.Enabled = on; btnPause.Enabled = on;
            if (!on) { paused = false; btnPause.Text = "Pause"; }
        }

        void StartQueue()
        {
            if (queueActive) return;
            if (!queue.Any(q => q.Status == "Pending" || q.Status == "Interrupted" || q.Status == "Stopped" || q.Status.StartsWith("Error")))
            { SetStatus("Nothing to do: add something to the queue first."); return; }
            foreach (var q in queue.Where(q => q.Status == "Stopped" || q.Status.StartsWith("Error"))) { q.Status = "Pending"; q.Error = ""; RefreshQ(q); }
            stopRequested = false;
            SetRunningUi(true);
            RunNext();
        }

        void RunNext()
        {
            var q = queue.FirstOrDefault(x => x.Status == "Pending" || x.Status == "Interrupted");
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
                    if (lastError != null) { q.Status = "Error"; q.Error = lastError; Log("! " + q.Error); }
                    else { q.Status = "Interrupted"; q.Error = ""; Log("! interrupted (the backend was stopped from outside, exit code " + code + "); it will run again on Start"); }
                }
                if (!q.Status.StartsWith("Done")) DeleteWork(currentWork);
                currentWork = "";
                RefreshQ(q); SaveQueue();
                RunNext();
            });
            };
            Native.ResetHidden();
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
                    currentWork = J.S(J.G(ev, "work"));
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
                    if (paused) break;
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
                    var warns = J.A(J.G(ev, "warnings"));
                    if (warns.Length > 0)
                    {
                        q.Status += " - check log";
                        foreach (var w in warns) Log("! warning: " + J.S(w));
                    }
                    RefreshQ(q);
                    break;
                case "error":
                    string m = J.S(J.G(ev, "message"));
                    Log("! " + m);
                    return m;
            }
            return null;
        }

        static string StageShort(string s) { return s == "iw3" ? "2D\u21923D" : s == "avi" ? "AVI" : s == "moflex" ? "Encoding" : s == "super" ? "Building" : s; }

        // ============================================================ 2D -> 3D (iw3)
        string TplName() { var it = cmbTpl.SelectedItem as Item; return it == null ? "best" : (string)it.Tag; }

        // a template's settings with this job's changes on top
        Dict Iw3Current()
        {
            var d = new Dict();
            var def = J.D(J.G(iw3Status, "defaults")); if (def != null) foreach (var kv in def) d[kv.Key] = kv.Value;
            var tpl = J.D(J.G(J.G(iw3Status, "templates"), TplName()));
            if (tpl != null) foreach (var kv in tpl) if (def != null && def.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
            foreach (var kv in conv3dOver) d[kv.Key] = kv.Value;
            return d;
        }

        string Iw3Summary()
        {
            if (iw3Status == null) return "iw3";
            var c = Iw3Current();
            return J.S(J.G(c, "model")) + ", depth " + J.S(J.G(c, "resolution")) + ", " + J.S(J.G(c, "method")) + (conv3dOver.Count > 0 ? ", changed" : "");
        }

        async Task RefreshIw3(string select)
        {
            try { iw3Status = J.D(await Backend.Call("iw3-status")); }
            catch (Exception ex) { lblIw3.Text = "iw3: " + ex.Message; return; }
            string keep = select ?? TplName();
            tplRefilling = true;
            try
            {
                cmbTpl.Items.Clear();
                foreach (var kv in J.D(J.G(iw3Status, "templates")) ?? new Dict())
                    cmbTpl.Items.Add(new Item(J.S(J.G(kv.Value, "label")) + (J.B(J.G(kv.Value, "builtin")) ? "" : "  (yours)"), kv.Key));
                SelectItem(cmbTpl, keep); if (cmbTpl.SelectedIndex < 0 && cmbTpl.Items.Count > 0) cmbTpl.SelectedIndex = 0;
            }
            finally { tplRefilling = false; }
            bool ok = J.B(J.G(iw3Status, "installed"));
            lblIw3.Text = ok ? "iw3 found" : "iw3 not installed";
            btnIw3Install.Text = "Install iw3...";
            btnIw3Install.Visible = !ok;
            UpdateToolsLabel();
            UpdateVideoNote();
        }

        // a backend command that prints events (stage/log/error/done), shown in the log and status bar;
        // returns the error, or null
        async Task<string> RunEvents(params string[] args)
        {
            var tcs = new TaskCompletionSource<bool>();
            string err = null;
            var p = new Process { StartInfo = Backend.Psi(args), EnableRaisingEvents = true };
            p.OutputDataReceived += (s2, e) =>
            {
                if (e.Data == null) return;
                object ev; try { ev = J.Parse(e.Data); } catch { return; }
                string t = J.S(J.G(ev, "type"));
                UI(() =>
                {
                    if (t == "stage") { SetStatus(J.S(J.G(ev, "text"))); Log(J.S(J.G(ev, "text"))); lblIw3.Text = "working..."; }
                    else if (t == "progress") SetStatus(J.S(J.G(ev, "text")) + string.Format(" {0:0}%", J.F(J.G(ev, "pct"))));
                    else if (t == "log") Log("  " + J.S(J.G(ev, "text")));
                    else if (t == "error") err = J.S(J.G(ev, "message"));
                });
            };
            p.ErrorDataReceived += (s2, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) UI(() => Log(e.Data)); };
            p.Exited += (s2, e) => { try { p.WaitForExit(); } catch { } tcs.TrySetResult(true); };
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
            await tcs.Task;
            return err;
        }

        // which models to download now: a checklist with sizes (Distill Large ticked)
        bool pickUseMine;
        List<string> PickModels(string title, string text, Dict offer, IEnumerable<string> have, bool tickBest, bool offerMine = false)
        {
            pickUseMine = false;
            var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 300), Font = Font };
            f.Controls.Add(new Label { Text = text, Left = 12, Top = 10, Width = 436, Height = 92 });
            var cl = new CheckedListBox { Left = 12, Top = 106, Width = 436, Height = 120, CheckOnClick = true };
            foreach (var kv in offer ?? new Dict())
            {
                if (have != null && have.Contains(kv.Key)) continue;
                double mb = J.F(J.G(kv.Value, "mb"));
                int k = cl.Items.Add(new Item(J.S(J.G(kv.Value, "label")) + "  -  " + (mb >= 1000 ? (mb / 1024).ToString("0.0", J.CI) + " GB" : mb.ToString("0", J.CI) + " MB"), kv.Key));
                if (tickBest && kv.Key == "Distill_Any_L") cl.SetItemChecked(k, true);
            }
            f.Controls.Add(cl);
            f.Controls.Add(new Label { Text = "Unticked models download by themselves the first time a job uses one.", Left = 12, Top = 232, Width = 436, ForeColor = SystemColors.GrayText });
            var ok = new Button { Text = offerMine ? "Install" : "OK", Left = 292, Top = 262, Width = 75, DialogResult = DialogResult.OK };
            var cn = new Button { Text = "Cancel", Left = 373, Top = 262, Width = 75, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { ok, cn }); f.AcceptButton = ok; f.CancelButton = cn;
            if (offerMine)
            {
                var mine = new Button { Text = "Use my iw3...", Left = 12, Top = 262, Width = 110, DialogResult = DialogResult.Retry };
                f.Controls.Add(mine);
            }
            var res = f.ShowDialog(this);
            if (res == DialogResult.Retry) { pickUseMine = true; return null; }
            if (res != DialogResult.OK) return null;
            return cl.CheckedItems.Cast<Item>().Select(it => (string)it.Tag).ToList();
        }

        async Task InstallIw3()
        {
            if (iw3Installing) return;
            Dict info = null;
            try { info = J.D(await Backend.Call("iw3-models")); } catch { }
            var models = PickModels("Install iw3", "Install iw3 (nunif, MIT licence) for 2D \u2192 3D?\n\n" +
                "It goes inside Moflex Studio's folder (app\\iw3), with only what Studio uses: its own Python and PyTorch with " +
                "graphics-card support (about 3 GB), plus the depth models you tick below. An NVIDIA graphics card is strongly " +
                "recommended.\n\nAlready have iw3? Use my iw3... uses it as it is, with every model it has (nothing is downloaded).",
                J.D(J.G(info, "offer")), null, true, true);
            if (pickUseMine) { await UseMyIw3(); return; }
            if (models == null) return;
            iw3Installing = true; btnIw3Install.Enabled = false; lblIw3.Text = "installing...";
            var args = new List<string> { "iw3-install" };
            if (models.Count == 0) args.Add("none"); else args.AddRange(models);
            string err = await RunEvents(args.ToArray());
            iw3Installing = false; btnIw3Install.Enabled = true;
            await RefreshIw3(null);
            if (err != null) { SetStatus("iw3 install failed: " + err); MessageBox.Show(this, err, "iw3 install failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            else if (J.B(J.G(iw3Status, "installed"))) SetStatus("iw3 is installed.");
        }

        // an iw3 the user already has: checked, then remembered (Settings' iw3 folder)
        async Task UseMyIw3()
        {
            string dir = PickFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iw3"));
            if (dir == null) return;
            SetStatus("Checking iw3 in " + dir + "...");
            Dict r;
            try { r = J.D(await Backend.Call("iw3-check", dir)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "iw3"); return; }
            if (!J.B(J.G(r, "ok"))) { SetStatus(""); MessageBox.Show(this, J.S(J.G(r, "message")), "Not a usable iw3", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            settings["iw3_dir"] = dir; try { SaveSettings(); } catch { }
            await RefreshIw3(null);
            var ms = J.A(J.G(r, "models"));
            MessageBox.Show(this, "Using your iw3:\n" + J.S(J.G(r, "nunif")) + "\n\nPyTorch " + J.S(J.G(r, "torch")) +
                (J.B(J.G(r, "gpu")) ? " with your graphics card." : " -- it does NOT see a graphics card, so 2D \u2192 3D will be very slow.") +
                "\n" + ms.Length + " depth model" + (ms.Length == 1 ? "" : "s") + " found.", "iw3", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus("Using your iw3.");
        }

        // the downloaded depth models: sizes, delete, download more
        async Task ManageModels(IWin32Window owner)
        {
            Dict info;
            try { info = J.D(await Backend.Call("iw3-models")); }
            catch (Exception ex) { MessageBox.Show(owner, ex.Message); return; }
            var f = new Form { Text = "2D \u2192 3D depth models", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 330), Font = Font };
            var lv = new ListView { Left = 12, Top = 12, Width = 496, Height = 240, View = View.Details, FullRowSelect = true, MultiSelect = false };
            lv.Columns.Add("Model", 380); lv.Columns.Add("Size", 90);
            Action fill = null;
            fill = () =>
            {
                lv.Items.Clear(); double tot = 0;
                foreach (var m in J.A(J.G(info, "models")))
                {
                    double mb = J.F(J.G(m, "mb")); tot += mb;
                    if (J.S(J.G(m, "file")).StartsWith("iw3_")) continue;   // iw3's own small ones
                    var it = lv.Items.Add(J.S(J.G(m, "label")));
                    it.SubItems.Add(mb >= 1000 ? (mb / 1024).ToString("0.0", J.CI) + " GB" : mb.ToString("0", J.CI) + " MB");
                    it.Tag = J.S(J.G(m, "file"));
                }
                f.Text = "2D \u2192 3D depth models  (" + (tot / 1024).ToString("0.0", J.CI) + " GB)";
            };
            fill();
            var bDel = new Button { Text = "Delete", Left = 12, Top = 262, Width = 90, Height = 28 };
            var bGet = new Button { Text = "Download...", Left = 108, Top = 262, Width = 100, Height = 28 };
            var bImp = new Button { Text = "Import...", Left = 214, Top = 262, Width = 90, Height = 28 };
            var bClose = new Button { Text = "Close", Left = 418, Top = 262, Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
            f.Controls.Add(bImp);
            bImp.Click += async delegate
            {
                string dir = PickFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                if (dir == null) return;
                bImp.Enabled = bGet.Enabled = bDel.Enabled = false;
                string err = await RunEvents("iw3-import-models", dir);
                bImp.Enabled = bGet.Enabled = bDel.Enabled = true;
                if (err != null) MessageBox.Show(f, err, "Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                try { info = J.D(await Backend.Call("iw3-models")); fill(); } catch { }
                SetStatus(err == null ? "Models imported (linked, no extra space, where on the same drive)." : "Import failed.");
            };
            f.Controls.Add(new Label { Text = "Import: models you already have elsewhere (another iw3, a download folder), linked in without copying where possible. A deleted model downloads again if a job uses it.", Left = 12, Top = 296, Width = 496, Height = 32, ForeColor = SystemColors.GrayText });
            f.Controls.AddRange(new Control[] { lv, bDel, bGet, bClose }); f.CancelButton = bClose;
            bDel.Click += async delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                string file = (string)lv.SelectedItems[0].Tag;
                if (MessageBox.Show(f, "Delete " + lv.SelectedItems[0].Text + "?", "Delete model", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                try { await Backend.Call("iw3-delete-model", file); info = J.D(await Backend.Call("iw3-models")); fill(); }
                catch (Exception ex) { MessageBox.Show(f, ex.Message); }
            };
            bGet.Click += async delegate
            {
                var have = J.A(J.G(info, "models")).Select(m => J.S(J.G(m, "model"))).Where(x => x != "").ToList();
                var pick = PickModels("Download depth models", "Which models should be downloaded now?", J.D(J.G(info, "offer")), have, false);
                if (pick == null || pick.Count == 0) return;
                bGet.Enabled = bDel.Enabled = false;
                var args = new List<string> { "iw3-fetch" }; args.AddRange(pick);
                string err = await RunEvents(args.ToArray());
                bGet.Enabled = bDel.Enabled = true;
                if (err != null) MessageBox.Show(f, err, "Download failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                try { info = J.D(await Backend.Call("iw3-models")); fill(); } catch { }
                SetStatus(err == null ? "Models downloaded." : "Model download failed.");
            };
            f.ShowDialog(owner);
        }

        // every iw3 setting: use for this job, save as a template of your own, or delete one of yours
        void EditIw3Settings()
        {
            if (iw3Status == null) { SetStatus("Still checking for iw3..."); return; }
            var c = Iw3Current();
            var f = new Form { Text = "2D \u2192 3D settings  (" + cmbTpl.Text + ")", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(470, 600), Font = Font,
                AutoScaleMode = AutoScaleMode.Font };
            int y = 10;
            Func<string, Control, Control> row = (label, ctl) =>
            {
                f.Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, Width = 170 });
                ctl.Left = 186; ctl.Top = y; if (ctl.Width < 60) ctl.Width = 120; f.Controls.Add(ctl); y += 26; return ctl;
            };
            Func<object, decimal, decimal, decimal, int, NumericUpDown> num = (v, min, max, inc, dec) =>
                new NumericUpDown { Minimum = min, Maximum = max, Increment = inc, DecimalPlaces = dec, Width = 90,
                    Value = Math.Max(min, Math.Min(max, (decimal)J.F(v))) };
            // the same models Models... offers, each with its size and whether it's downloaded
            var cModel = new ComboBox { Width = 270, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 400 };
            string curModel = J.S(J.G(c, "model"));
            foreach (var m in J.A(J.G(iw3Status, "catalog")))
            {
                double mb = J.F(J.G(m, "mb"));
                string size = mb >= 1000 ? (mb / 1024).ToString("0.0", J.CI) + " GB" : mb.ToString("0", J.CI) + " MB";
                cModel.Items.Add(new Item(J.S(J.G(m, "label")) + "  -  " + (J.B(J.G(m, "have")) ? "downloaded" : size + ", downloads when used"),
                    J.S(J.G(m, "model"))));
            }
            if (curModel != "" && !cModel.Items.Cast<Item>().Any(it => (string)it.Tag == curModel)) cModel.Items.Add(new Item(curModel, curModel));
            SelectItem(cModel, curModel); if (cModel.SelectedIndex < 0 && cModel.Items.Count > 0) cModel.SelectedIndex = 0;
            row("Depth model", cModel);
            var nDiv = (NumericUpDown)row("3D strength", num(J.G(c, "divergence"), 0, 10, 0.1m, 2));
            var nConv = (NumericUpDown)row("Convergence plane", num(J.G(c, "convergence"), -2, 2, 0.05m, 2));
            var cCm = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList }; cCm.Items.AddRange(new object[] { "sod_v1", "constant" });
            cCm.SelectedItem = J.S(J.G(c, "convergence_mode")); if (cCm.SelectedIndex < 0) cCm.SelectedIndex = 0;
            row("Convergence (auto: sod_v1)", cCm);
            var nIpd = (NumericUpDown)row("Your own size (IPD offset)", num(J.G(c, "ipd_offset"), -10, 20, 1, 0));
            var cSyn = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList }; cSyn.Items.AddRange(new object[] { "both", "right", "left" });
            cSyn.SelectedItem = J.S(J.G(c, "synthetic_view")); if (cSyn.SelectedIndex < 0) cSyn.SelectedIndex = 0;
            row("Synthetic view", cSyn);
            var cMeth = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            cMeth.Items.AddRange(new object[] { "mlbw_l2s", "mlbw_l4s", "mlbw_l2", "mlbw_l4", "row_flow_v3", "row_flow_v3_sym", "row_flow_v2", "row_flow", "forward_fill", "backward" });
            cMeth.SelectedItem = J.S(J.G(c, "method")); if (cMeth.SelectedIndex < 0) cMeth.SelectedIndex = 0;
            row("Method", cMeth);
            var nSw = (NumericUpDown)row("Stereo width (0 = default)", num(J.G(c, "stereo_width"), 0, 4096, 64, 0));
            var nRes = (NumericUpDown)row("Depth resolution (0 = default)", num(J.G(c, "resolution"), 0, 2048, 8, 0));
            var kLim = (CheckBox)row("", new CheckBox { Text = "Limit to the source size", Width = 220, Checked = J.B(J.G(c, "limit_resolution")) });
            var nFg = (NumericUpDown)row("Foreground scale", num(J.G(c, "foreground_scale"), -3, 3, 0.25m, 2));
            var ed = J.A(J.G(c, "edge_dilation"));
            var nEdX = num(ed.Length > 0 ? ed[0] : 0, 0, 20, 1, 0); nEdX.Width = 60;
            var nEdY = num(ed.Length > 1 ? ed[1] : (ed.Length > 0 ? ed[0] : 0), 0, 20, 1, 0); nEdY.Width = 60;
            row("Edge fix  (x, y)", nEdX); nEdY.Left = 252; nEdY.Top = y - 28; f.Controls.Add(nEdY);
            var kEma = (CheckBox)row("", new CheckBox { Text = "Flicker reduction (EMA)", Width = 220, Checked = J.B(J.G(c, "ema")) });
            var nEmaD = (NumericUpDown)row("  EMA decay", num(J.G(c, "ema_decay"), 0, 1, 0.01m, 2));
            var nEmaB = (NumericUpDown)row("  EMA buffer (frames)", num(J.G(c, "ema_buffer"), 1, 300, 1, 0));
            var kScene = (CheckBox)row("", new CheckBox { Text = "Scene boundary detection", Width = 220, Checked = J.B(J.G(c, "scene_detect")) });
            var kBorder = (CheckBox)row("", new CheckBox { Text = "Preserve the screen border", Width = 220, Checked = J.B(J.G(c, "preserve_border")) });
            var nCrf = (NumericUpDown)row("Intermediate quality (CRF)", num(J.G(c, "crf"), 0, 40, 1, 0));
            y += 4;
            var bModels = new Button { Text = "Models...", Left = 12, Top = y, Width = 100, Height = 26, Enabled = J.B(J.G(iw3Status, "installed")) };
            f.Controls.Add(new Label { Text = "download or delete depth models (which iw3 is used: Settings tab)", Left = 118, Top = y + 5, Width = 340, ForeColor = SystemColors.GrayText });
            bModels.Click += async delegate { await ManageModels(f); };
            f.Controls.Add(bModels); y += 36;
            var bOk = new Button { Text = "Use for this video", Left = 12, Top = y, Width = 130, Height = 28, DialogResult = DialogResult.OK };
            var bSave = new Button { Text = "Save as template...", Left = 148, Top = y, Width = 130, Height = 28 };
            var bDel = new Button { Text = "Delete template", Left = 284, Top = y, Width = 110, Height = 28,
                Enabled = !J.B(J.G(J.G(J.G(iw3Status, "templates"), TplName()), "builtin")) };
            var bCancel = new Button { Text = "Cancel", Left = 400, Top = y, Width = 58, Height = 28, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { bOk, bSave, bDel, bCancel });
            f.AcceptButton = bOk; f.CancelButton = bCancel;
            f.ClientSize = new Size(470, y + 40);   // as tall as its rows (was cut off)
            // a small screen: scroll rather than cut off
            var wa = Screen.FromControl(this).WorkingArea;
            if (f.Height > wa.Height - 20) { f.AutoScroll = true; f.Height = wa.Height - 20; }
            Func<Dict> collect = () => new Dict {
                { "model", cModel.SelectedItem is Item ? (string)((Item)cModel.SelectedItem).Tag : curModel }, { "divergence", (double)nDiv.Value }, { "convergence", (double)nConv.Value },
                { "convergence_mode", cCm.Text }, { "ipd_offset", (int)nIpd.Value }, { "synthetic_view", cSyn.Text }, { "method", cMeth.Text },
                { "stereo_width", (int)nSw.Value }, { "resolution", (int)nRes.Value }, { "limit_resolution", kLim.Checked },
                { "foreground_scale", (double)nFg.Value }, { "edge_dilation", new object[] { (int)nEdX.Value, (int)nEdY.Value } },
                { "ema", kEma.Checked }, { "ema_decay", (double)nEmaD.Value }, { "ema_buffer", (int)nEmaB.Value },
                { "scene_detect", kScene.Checked }, { "preserve_border", kBorder.Checked }, { "crf", (int)nCrf.Value } };
            string saved = null; bool deleted = false;
            bSave.Click += async delegate
            {
                string name = Prompt(f, "Save as template", "A name for these settings (letters, digits, spaces):", "");
                if (string.IsNullOrWhiteSpace(name)) return;
                var t = collect(); t["label"] = name.Trim();
                try { saved = J.S(J.G(await Backend.Call("iw3-save-template", name.Trim(), J.Dump(t)), "name")); f.DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(f, ex.Message, "Save template", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            bDel.Click += async delegate
            {
                if (MessageBox.Show(f, "Delete the template \"" + TplName() + "\"?", "Delete template", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                try { await Backend.Call("iw3-delete-template", TplName()); deleted = true; f.DialogResult = DialogResult.Cancel; }
                catch (Exception ex) { MessageBox.Show(f, ex.Message); }
            };
            var res = f.ShowDialog(this);
            if (saved != null) { conv3dOver.Clear(); var _ = RefreshIw3(saved); return; }
            if (deleted) { conv3dOver.Clear(); var _ = RefreshIw3("best"); return; }
            if (res == DialogResult.OK)
            {
                // only what differs from the template is kept as this video's change
                conv3dOver = new Dict(); var basec = Iw3Current();
                foreach (var kv in collect()) if (J.Dump(kv.Value) != J.Dump(J.G(basec, kv.Key))) conv3dOver[kv.Key] = kv.Value;
            }
            var __ = RefreshIw3(null);
        }

        static string Prompt(IWin32Window owner, string title, string text, string def)
        {
            var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 104) };
            f.Controls.Add(new Label { Text = text, Left = 12, Top = 12, Width = 336 });
            var t = new TextBox { Left = 12, Top = 36, Width = 336, Text = def, MaxLength = 32 };
            var ok = new Button { Text = "OK", Left = 192, Top = 68, Width = 75, DialogResult = DialogResult.OK };
            var cn = new Button { Text = "Cancel", Left = 273, Top = 68, Width = 75, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { t, ok, cn }); f.AcceptButton = ok; f.CancelButton = cn;
            return f.ShowDialog(owner) == DialogResult.OK ? t.Text : null;
        }

        string stageBeforePause = "", statusBeforePause = "";
        void TogglePause()
        {
            if (proc == null || running == null) return;
            if (!paused)
            {
                int n = Native.SetJobSuspended(true);
                if (n == 0) { SetStatus("Nothing to pause right now."); return; }
                paused = true; btnPause.Text = "Resume";
                stageBeforePause = lblStage.Text; statusBeforePause = running.Status; lblStage.Text = "Paused  (" + stageBeforePause + ")";
                running.Status = "Paused"; RefreshQ(running);
                Log("paused");
            }
            else
            {
                Native.SetJobSuspended(false);
                paused = false; btnPause.Text = "Pause";
                lblStage.Text = stageBeforePause; running.Status = statusBeforePause; RefreshQ(running);
                Log("resumed");
            }
        }

        // the job's temp folder (lossless AVI etc.); the backend removes it itself unless it was killed
        void DeleteWork(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Path.GetFileName(dir).StartsWith("moflex_studio_")) return;
            Task.Run(() =>
            {
                for (int i = 0; i < 20 && Directory.Exists(dir); i++)
                {
                    try { Directory.Delete(dir, true); } catch { System.Threading.Thread.Sleep(500); }
                }
                bool gone = !Directory.Exists(dir);
                UI(() => Log(gone ? "removed the job's temp files" : "could not remove " + dir + " (it is cleaned up on the next start)"));
            });
        }

        void StopQueue()
        {
            if (paused) { Native.SetJobSuspended(false); paused = false; btnPause.Text = "Pause"; }
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
            try { RememberOutDir(txtOutDir.Text.Trim()); } catch { }
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
