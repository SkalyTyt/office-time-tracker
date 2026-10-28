// Программа для рабочих компьютеров сотрудников («Учёт времени»).
// До нажатия «Начать смену» экран закрыт плашкой, выключить ПК во время смены нельзя,
// а если ПК выключился внезапно, время смены ставится на паузу (считает сервер).
// Код под C# 5 (компилятор, встроенный в Windows), без внешних библиотек.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace OfficeTime
{
    // ---------- настройки (config.ini рядом с программой) ----------
    class Config
    {
        public string Server = "http://localhost:3000";
        public string UnlockPin = "";      // аварийный PIN администратора (Ctrl+Shift+F12 на плашке закрывает программу)
        public bool BlockKeys = true;      // блокировать Win, Alt+Tab, Ctrl+Esc, пока показана плашка

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
                if (File.Exists(p))
                {
                    foreach (string raw in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        int i = line.IndexOf('=');
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || i < 1) continue;
                        string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
                        if (k == "server") c.Server = v;
                        else if (k == "unlock_pin") c.UnlockPin = v;
                        else if (k == "block_keys") c.BlockKeys = !(v == "0" || v.ToLowerInvariant() == "no");
                    }
                }
            }
            catch (Exception) { }
            return c;
        }
    }

    // ---------- запросы к серверу ----------
    class ApiResult
    {
        public int Status;
        public bool NetError;
        public Dictionary<string, object> Data = new Dictionary<string, object>();
        public string Error { get { string e = Util.S(Data, "error"); return e != null ? e : "Ошибка сервера"; } }
    }

    class Api
    {
        public string Base;
        public string Token;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public Api(string baseUrl) { Base = baseUrl.TrimEnd('/'); }

        public ApiResult Call(string method, string path, Dictionary<string, object> body)
        {
            ApiResult r = new ApiResult();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Base + path);
                req.Method = method;
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.Proxy = null;
                if (!string.IsNullOrEmpty(Token)) req.Headers[HttpRequestHeader.Cookie] = "sid=" + Token;
                if (method != "GET")
                {
                    req.ContentType = "application/json";
                    byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body != null ? body : new Dictionary<string, object>()));
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    r.Data = Read(resp);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null) { r.Status = (int)resp.StatusCode; r.Data = Read(resp); resp.Close(); }
                else r.NetError = true;
            }
            catch (Exception) { r.NetError = true; }
            return r;
        }

        static Dictionary<string, object> Read(HttpWebResponse resp)
        {
            try
            {
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    Dictionary<string, object> d = Json.DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                    return d != null ? d : new Dictionary<string, object>();
                }
            }
            catch (Exception) { return new Dictionary<string, object>(); }
        }

        public static string Serialize(object o) { return Json.Serialize(o); }
        public static object Deserialize(string s) { return Json.DeserializeObject(s); }
    }

    static class Util
    {
        public static Dictionary<string, object> D(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v)) return v as Dictionary<string, object>;
            return null;
        }
        public static string S(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return v.ToString();
            return null;
        }
        public static long L(Dictionary<string, object> d, string k)
        {
            string s = S(d, k);
            long n;
            return s != null && long.TryParse(s, out n) ? n : 0;
        }
        public static DateTime ParseIso(string s)
        {
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        public static string Iso(DateTime utc) { return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture); }
    }

    // ---------- системные вызовы ----------
    static class Native
    {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string reason);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
    }

    // Пока показана плашка, блокируем клавиши, которыми можно из неё выбраться
    class KeyBlocker
    {
        IntPtr hook = IntPtr.Zero;
        Native.LowLevelKeyboardProc proc;

        public void Install()
        {
            if (hook != IntPtr.Zero) return;
            proc = Callback;
            using (Process p = Process.GetCurrentProcess())
            using (ProcessModule m = p.MainModule)
                hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(m.ModuleName), 0);
        }

        public void Remove()
        {
            if (hook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam);
                int flags = Marshal.ReadInt32(lParam, 8);
                bool alt = (flags & 0x20) != 0;
                bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool block = vk == 0x5B || vk == 0x5C || vk == 0x5D                // Win, меню
                    || (alt && (vk == 0x09 || vk == 0x1B || vk == 0x73))           // Alt+Tab, Alt+Esc, Alt+F4
                    || (ctrl && vk == 0x1B);                                        // Ctrl+Esc, Ctrl+Shift+Esc
                if (block) return (IntPtr)1;
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    // ---------- плашка на весь экран ----------
    class LockForm : Form
    {
        Panel panel;
        Label lblClock, lblTitle, lblText, lblLoginCap, lblPassCap, lblError;
        TextBox txtLogin, txtPass;
        Button btnMain, btnSecond;
        Timer clock = new Timer();
        public bool AllowClose;
        public event EventHandler MainClicked, SecondClicked, PinRequested;

        public LockForm(bool windowed)
        {
            Text = "Учёт времени";
            BackColor = ColorTranslator.FromHtml("#16202E");
            ForeColor = ColorTranslator.FromHtml("#E6ECF5");
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            if (windowed)
            {
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                ClientSize = new Size(1000, 700);
                StartPosition = FormStartPosition.CenterScreen;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                Bounds = SystemInformation.VirtualScreen;
            }

            lblClock = new Label { AutoSize = false, Location = new Point(48, 32), Size = new Size(900, 70), Font = new Font("Segoe UI Light", 36f), ForeColor = ColorTranslator.FromHtml("#8FB3F0") };
            Controls.Add(lblClock);

            panel = new Panel { Size = new Size(620, 520), BackColor = ColorTranslator.FromHtml("#1F2C3F") };
            Controls.Add(panel);
            lblTitle = new Label { Location = new Point(30, 26), Size = new Size(560, 50), Font = new Font("Segoe UI Semibold", 22f), ForeColor = Color.White };
            lblText = new Label { Location = new Point(30, 86), Size = new Size(560, 84), Font = new Font("Segoe UI", 12.5f), ForeColor = ColorTranslator.FromHtml("#B9C6DA") };
            lblLoginCap = Caption("Логин", 176);
            txtLogin = Input(198, false);
            lblPassCap = Caption("Пароль", 242);
            txtPass = Input(264, true);
            lblError = new Label { Location = new Point(30, 318), Size = new Size(560, 44), Font = new Font("Segoe UI", 10.5f), ForeColor = ColorTranslator.FromHtml("#FF8A7A") };
            btnMain = Btn(372, 60, 16f, "#3B6FD4", "#FFFFFF");
            btnSecond = Btn(442, 46, 12f, "#2F4059", "#E6ECF5");
            btnMain.Click += delegate (object s, EventArgs e) { if (MainClicked != null) MainClicked(this, e); };
            btnSecond.Click += delegate (object s, EventArgs e) { if (SecondClicked != null) SecondClicked(this, e); };
            panel.Controls.AddRange(new Control[] { lblTitle, lblText, lblLoginCap, txtLogin, lblPassCap, txtPass, lblError, btnMain, btnSecond });
            AcceptButton = btnMain;

            clock.Interval = 1000;
            clock.Tick += delegate { lblClock.Text = DateTime.Now.ToString("HH:mm") + "   " + DateTime.Now.ToString("d MMMM", new CultureInfo("ru-RU")); };
            clock.Start();
            lblClock.Text = DateTime.Now.ToString("HH:mm");
        }

        Label Caption(string t, int y) { Label l = new Label { Text = t, Location = new Point(30, y), Size = new Size(300, 22), ForeColor = ColorTranslator.FromHtml("#8A9BB4") }; return l; }

        TextBox Input(int y, bool pass)
        {
            TextBox t = new TextBox { Location = new Point(30, y), Size = new Size(560, 34), Font = new Font("Segoe UI", 13f), BackColor = ColorTranslator.FromHtml("#16202E"), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            t.UseSystemPasswordChar = pass;
            return t;
        }

        Button Btn(int y, int h, float size, string back, string fore)
        {
            Button b = new Button { Location = new Point(30, y), Size = new Size(560, h), Font = new Font("Segoe UI Semibold", size), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
            b.BackColor = ColorTranslator.FromHtml(back);
            b.ForeColor = ColorTranslator.FromHtml(fore);
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        public string LoginText { get { return txtLogin.Text.Trim(); } }
        public string PassText { get { return txtPass.Text; } }

        public void Setup(string title, string text, string error, bool login, string main, string second)
        {
            lblTitle.Text = title;
            lblText.Text = text;
            lblError.Text = error != null ? error : "";
            lblLoginCap.Visible = txtLogin.Visible = lblPassCap.Visible = txtPass.Visible = login;
            int dy = login ? 0 : -130;
            lblError.Top = 318 + dy;
            btnMain.Top = 372 + dy;
            btnSecond.Top = 442 + dy;
            btnMain.Visible = main != null;
            btnMain.Text = main != null ? main : "";
            btnSecond.Visible = second != null;
            btnSecond.Text = second != null ? second : "";
            int bottom = (second != null ? btnSecond.Bottom : (main != null ? btnMain.Bottom : lblError.Bottom)) + 30;
            panel.Height = bottom;
            Rectangle pb = Program.Windowed ? ClientRectangle : new Rectangle(Screen.PrimaryScreen.Bounds.X - Bounds.X, Screen.PrimaryScreen.Bounds.Y - Bounds.Y, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            panel.Location = new Point(pb.X + (pb.Width - panel.Width) / 2, pb.Y + (pb.Height - panel.Height) / 2);
            if (login) { txtPass.Text = ""; txtLogin.Focus(); }
            else if (main != null) btnMain.Focus();
        }

        public void SetBusy(bool busy)
        {
            btnMain.Enabled = !busy;
            btnSecond.Enabled = !busy;
            txtLogin.Enabled = !busy;
            txtPass.Enabled = !busy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Shift | Keys.F12))
            {
                if (PinRequested != null) PinRequested(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // ---------- маленькое окно во время смены ----------
    class MiniForm : Form
    {
        Label lblFio, lblTimer, lblStatus;
        public Button BtnFinish;
        public Controller Ctrl;

        public MiniForm(Controller ctrl)
        {
            Ctrl = ctrl;
            Text = "Учёт времени";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = !Program.Windowed;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(330, 196);
            BackColor = ColorTranslator.FromHtml("#1F2C3F");
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5f);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

            lblFio = new Label { Location = new Point(16, 12), Size = new Size(298, 24), Font = new Font("Segoe UI Semibold", 11f), AutoEllipsis = true };
            lblTimer = new Label { Location = new Point(16, 40), Size = new Size(298, 52), Font = new Font("Segoe UI Semibold", 28f), Text = "00:00:00" };
            lblStatus = new Label { Location = new Point(16, 96), Size = new Size(298, 40), ForeColor = ColorTranslator.FromHtml("#8A9BB4") };
            BtnFinish = new Button { Location = new Point(16, 142), Size = new Size(298, 42), Text = "Завершить смену", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 12f), Cursor = Cursors.Hand };
            BtnFinish.BackColor = ColorTranslator.FromHtml("#D2584A");
            BtnFinish.ForeColor = Color.White;
            BtnFinish.FlatAppearance.BorderSize = 0;
            Controls.AddRange(new Control[] { lblFio, lblTimer, lblStatus, BtnFinish });
        }

        public void SetFio(string fio) { lblFio.Text = fio; }
        public void SetTimer(TimeSpan t) { lblTimer.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); }
        public void SetStatus(string s) { lblStatus.Text = s; }

        // Во время смены выключить или перезагрузить компьютер нельзя
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0011 && Ctrl.ShiftActive && !Program.Windowed) // WM_QUERYENDSESSION
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.TaskManagerClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }
    }

    // ---------- вся логика ----------
    class Controller
    {
        Config cfg;
        Api api;
        LockForm lockForm;
        MiniForm mini;
        KeyBlocker keys = new KeyBlocker();
        Timer tick = new Timer(), heartbeat = new Timer();

        readonly string runId = Guid.NewGuid().ToString("N");
        string mode = "";
        string fio;
        string note = "";
        bool offlineMode;
        DateTime startUtc;
        long pausedMs;
        TimeSpan serverOffset = TimeSpan.Zero;
        bool busy;
        public bool ShiftActive;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfficeTime"); } }
        static string SessionFile { get { return Path.Combine(Dir, "session.dat"); } }
        static string PendingFile { get { return Path.Combine(Dir, "pending.json"); } }

        public static void Log(string s)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "client.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public void Start()
        {
            cfg = Config.Load();
            api = new Api(cfg.Server);
            LoadSession();

            lockForm = new LockForm(Program.Windowed);
            mini = new MiniForm(this);
            IntPtr h1 = lockForm.Handle, h2 = mini.Handle; // создаём окна сразу, чтобы потоки могли к ним обращаться
            lockForm.MainClicked += OnMain;
            lockForm.SecondClicked += OnSecond;
            lockForm.PinRequested += OnPin;
            mini.BtnFinish.Click += delegate { FinishClicked(); };

            tick.Interval = 1000;
            tick.Tick += delegate { OnTick(); };
            heartbeat.Interval = 30000;
            heartbeat.Tick += delegate { OnHeartbeat(); };

            // плашка не даёт перейти к другим окнам: возвращаем её наверх
            Timer top = new Timer();
            top.Interval = 700;
            top.Tick += delegate
            {
                if (lockForm.Visible && !Program.Windowed) { lockForm.TopMost = true; lockForm.Activate(); }
            };
            top.Start();

            Log("Запуск, сервер " + cfg.Server);
            ShowMode("connecting", null);
            lockForm.Show();
            if (cfg.BlockKeys && !Program.Windowed) keys.Install();
            Bg(RefreshState);
        }

        // --- вспомогательное ---
        void Bg(Action work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { Log("Ошибка: " + ex); }
            });
        }

        void Ui(Action a)
        {
            try { if (!lockForm.IsDisposed) lockForm.BeginInvoke(a); }
            catch (Exception) { }
        }

        void LoadSession()
        {
            try
            {
                if (!File.Exists(SessionFile)) return;
                byte[] raw = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(SessionFile)), null, DataProtectionScope.CurrentUser);
                Dictionary<string, object> d = Api.Deserialize(Encoding.UTF8.GetString(raw)) as Dictionary<string, object>;
                api.Token = Util.S(d, "token");
                fio = Util.S(d, "fio");
            }
            catch (Exception ex) { Log("Не удалось прочитать сохранённый вход: " + ex.Message); api.Token = null; }
        }

        void SaveSession()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["token"] = api.Token;
                d["fio"] = fio;
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(Api.Serialize(d)), null, DataProtectionScope.CurrentUser);
                File.WriteAllText(SessionFile, Convert.ToBase64String(enc));
            }
            catch (Exception ex) { Log("Не удалось сохранить вход: " + ex.Message); }
        }

        void ClearSession()
        {
            api.Token = null;
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch (Exception) { }
        }

        // Отметки, сделанные без связи с сервером: отправляются позже
        Dictionary<string, object> LoadPending()
        {
            try { if (File.Exists(PendingFile)) { Dictionary<string, object> d = Api.Deserialize(File.ReadAllText(PendingFile)) as Dictionary<string, object>; if (d != null) return d; } }
            catch (Exception) { }
            return new Dictionary<string, object>();
        }

        void SavePending(Dictionary<string, object> p)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (p.Count == 0) { if (File.Exists(PendingFile)) File.Delete(PendingFile); }
                else File.WriteAllText(PendingFile, Api.Serialize(p));
            }
            catch (Exception ex) { Log("pending: " + ex.Message); }
        }

        // true, если отправлять больше нечего; false — нет связи, повторим позже
        bool SyncPending()
        {
            Dictionary<string, object> p = LoadPending();
            if (p.Count == 0 || string.IsNullOrEmpty(api.Token)) return true;
            if (p.ContainsKey("start"))
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                b["at"] = p["start"];
                ApiResult r = api.Call("POST", "/api/agent/start", b);
                if (r.NetError) return false;
                if (r.Status == 401) return true;
                Log("Отправлено начало смены, сохранённое без связи: " + r.Status);
                p.Remove("start");
                SavePending(p);
            }
            if (p.ContainsKey("end"))
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["end"] = p["end"];
                ApiResult r = api.Call("POST", "/api/agent/finish", b);
                if (r.NetError) return false;
                Log("Отправлено окончание смены, сохранённое без связи: " + r.Status);
                p.Remove("end");
                SavePending(p);
            }
            return true;
        }

        // --- экран-плашка ---
        void ShowMode(string m, string error)
        {
            mode = m;
            string name = fio != null ? fio : "";
            switch (m)
            {
                case "connecting":
                    lockForm.Setup("Подключение…", "Проверяем связь с сервером.", null, false, null, null); break;
                case "login":
                    lockForm.Setup("Вход", "Войдите один раз: программа запомнит вас на этом компьютере и больше не будет спрашивать пароль.", error, true, "Войти", null); break;
                case "start":
                    lockForm.Setup("Здравствуйте, " + name + "!", (note.Length > 0 ? note + "\n" : "") + "Чтобы продолжить работу, нажмите «Начать смену».", error, false, "Начать смену", null); break;
                case "resume":
                    lockForm.Setup("С возвращением, " + name + "!", "Ваша смена идёт с " + startUtc.ToLocalTime().ToString("HH:mm") + ". Нажмите «Продолжить смену», чтобы вернуться к работе.", error, false, "Продолжить смену", null); break;
                case "offline":
                    lockForm.Setup("Нет связи с сервером",
                        "Проверьте, что компьютер администратора включён." + (fio != null ? "\nМожно начать работу без связи: время сохранится на этом компьютере и отправится позже." : "\nПервый вход возможен только при наличии связи."),
                        error, false, "Повторить", fio != null ? "Работать без связи" : null); break;
                case "other":
                    lockForm.Setup("Идёт другое время", "У вас запущено вечернее время или удалённая работа. Завершите его на сайте и нажмите «Повторить».", error, false, "Повторить", null); break;
                case "finished":
                    lockForm.Setup("Смена завершена", "Спасибо за работу! Компьютер можно выключить." + (note.Length > 0 ? "\n" + note : ""), error, false, "Выключить компьютер", "Начать новую смену"); break;
            }
            lockForm.SetBusy(false);
            busy = false;
        }

        void OnMain(object s, EventArgs e)
        {
            if (busy) return;
            switch (mode)
            {
                case "login": DoLogin(); break;
                case "start": DoStart(); break;
                case "resume": DoResume(); break;
                case "offline":
                case "other": Retry(); break;
                case "finished":
                    try { Process.Start("shutdown", "/s /t 5"); } catch (Exception ex) { Log("shutdown: " + ex.Message); }
                    break;
            }
        }

        void OnSecond(object s, EventArgs e)
        {
            if (busy) return;
            if (mode == "offline") OfflineStart();
            else if (mode == "finished") { note = ""; Retry(); }
        }

        void Retry()
        {
            busy = true;
            lockForm.SetBusy(true);
            Bg(RefreshState);
        }

        void OnPin(object s, EventArgs e)
        {
            if (string.IsNullOrEmpty(cfg.UnlockPin)) return;
            string pin = Prompt.Ask("Аварийный выход", "Введите PIN администратора:");
            if (pin != null && pin == cfg.UnlockPin)
            {
                Log("Программа закрыта по аварийному PIN");
                keys.Remove();
                lockForm.AllowClose = true;
                Application.Exit();
            }
        }

        // --- шаги ---
        void RefreshState()
        {
            if (!SyncPending() && !string.IsNullOrEmpty(api.Token))
            {
                Ui(delegate { ShowMode("offline", null); });
                return;
            }
            if (string.IsNullOrEmpty(api.Token)) { Ui(delegate { ShowMode("login", null); }); return; }

            ApiResult r = api.Call("GET", "/api/agent/state", null);
            if (r.NetError) { Ui(delegate { ShowMode("offline", null); }); return; }
            if (r.Status == 401)
            {
                ClearSession();
                Ui(delegate { ShowMode("login", "Нужно войти заново."); });
                return;
            }
            if (r.Status != 200) { string er = r.Error; Ui(delegate { ShowMode("offline", er); }); return; }

            Dictionary<string, object> user = Util.D(r.Data, "user");
            string f = Util.S(user, "fio");
            Dictionary<string, object> open = Util.D(r.Data, "open");
            Dictionary<string, object> other = Util.D(r.Data, "other");
            string nowS = Util.S(r.Data, "now");
            Ui(delegate
            {
                if (f != null) { fio = f; SaveSession(); }
                if (nowS != null) serverOffset = Util.ParseIso(nowS) - DateTime.UtcNow;
                if (other != null) { ShowMode("other", null); return; }
                if (open != null)
                {
                    startUtc = Util.ParseIso(Util.S(open, "start_ts"));
                    pausedMs = Util.L(open, "paused_ms");
                    if (ShiftActive) { ShowMode("resume", null); return; }   // вход был сброшен во время смены
                    ShowMode("resume", null);
                }
                else ShowMode("start", null);
            });
        }

        void DoLogin()
        {
            string login = lockForm.LoginText, pass = lockForm.PassText;
            if (login.Length == 0 || pass.Length == 0) { lockForm.Setup("Вход", "Введите логин и пароль.", "Заполните оба поля.", true, "Войти", null); return; }
            busy = true;
            lockForm.SetBusy(true);
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["login"] = login;
                b["password"] = pass;
                ApiResult r = api.Call("POST", "/api/agent/login", b);
                if (r.NetError) { Ui(delegate { ShowMode("login", "Нет связи с сервером."); }); return; }
                if (r.Status != 200) { string er = r.Error; Ui(delegate { ShowMode("login", er); }); return; }
                api.Token = Util.S(r.Data, "token");
                fio = Util.S(Util.D(r.Data, "user"), "fio");
                Ui(delegate { SaveSession(); });
                Log("Выполнен вход: " + fio);
                RefreshState();
            });
        }

        void DoStart()
        {
            busy = true;
            lockForm.SetBusy(true);
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                ApiResult r = api.Call("POST", "/api/agent/start", b);
                HandleShiftResult(r, "start");
            });
        }

        void DoResume()
        {
            busy = true;
            lockForm.SetBusy(true);
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                ApiResult r = api.Call("POST", "/api/agent/resume", b);
                HandleShiftResult(r, "resume");
            });
        }

        void HandleShiftResult(ApiResult r, string what)
        {
            if (r.NetError) { Ui(delegate { ShowMode("offline", null); }); return; }
            if (r.Status == 401) { ClearSession(); Ui(delegate { ShowMode("login", "Нужно войти заново."); }); return; }
            if (r.Status == 409) { DoResume(); return; }
            if (r.Status != 200) { string er = r.Error; Ui(delegate { ShowMode(what, er); }); return; }
            bool closed = r.Data.ContainsKey("closed") && Util.S(r.Data, "closed") == "True";
            if (closed)
            {
                string at = Util.S(r.Data, "closedAt");
                Ui(delegate
                {
                    note = "Прошлая смена закрыта автоматически" + (at != null ? " в " + Util.ParseIso(at).ToLocalTime().ToString("HH:mm") : "") + " (не было связи с компьютером).";
                    ShowMode("start", null);
                });
                return;
            }
            Ui(delegate { BeginShift(r.Data); });
        }

        void BeginShift(Dictionary<string, object> data)
        {
            Dictionary<string, object> open = Util.D(data, "open");
            if (open == null) { ShowMode("start", null); return; }
            startUtc = Util.ParseIso(Util.S(open, "start_ts"));
            pausedMs = Util.L(open, "paused_ms");
            string nowS = Util.S(data, "now");
            if (nowS != null) serverOffset = Util.ParseIso(nowS) - DateTime.UtcNow;
            long pausedMin = Util.L(data, "pausedMin");
            offlineMode = false;
            ShiftActive = true;
            busy = false;
            note = "";
            mini.SetFio(fio != null ? fio : "");
            mini.SetStatus(pausedMin > 0 ? "Время, пока компьютер был выключен (" + pausedMin + " мин), не учитывается." : "Связь с сервером: есть");
            lockForm.Hide();
            keys.Remove();
            mini.Show();
            BlockShutdown(true);
            OnTick();
            tick.Start();
            heartbeat.Start();
            Log("Смена идёт с " + startUtc.ToLocalTime().ToString("HH:mm"));
        }

        void OfflineStart()
        {
            Dictionary<string, object> p = LoadPending();
            p["start"] = Util.Iso(DateTime.UtcNow);
            SavePending(p);
            startUtc = DateTime.UtcNow;
            pausedMs = 0;
            offlineMode = true;
            ShiftActive = true;
            busy = false;
            mini.SetFio(fio != null ? fio : "");
            mini.SetStatus("Нет связи: время сохраняется на этом компьютере");
            lockForm.Hide();
            keys.Remove();
            mini.Show();
            BlockShutdown(true);
            OnTick();
            tick.Start();
            heartbeat.Start();
            Log("Смена начата без связи");
        }

        void BlockShutdown(bool on)
        {
            if (Program.Windowed) return;
            try
            {
                if (on) Native.ShutdownBlockReasonCreate(mini.Handle, "Сначала нажмите «Завершить смену»");
                else Native.ShutdownBlockReasonDestroy(mini.Handle);
            }
            catch (Exception ex) { Log("ShutdownBlock: " + ex.Message); }
        }

        void OnTick()
        {
            if (!ShiftActive) return;
            TimeSpan w = (DateTime.UtcNow + (offlineMode ? TimeSpan.Zero : serverOffset)) - startUtc - TimeSpan.FromMilliseconds(pausedMs);
            if (w < TimeSpan.Zero) w = TimeSpan.Zero;
            mini.SetTimer(w);
        }

        void OnHeartbeat()
        {
            if (!ShiftActive) return;
            if (offlineMode)
            {
                Bg(delegate
                {
                    if (!SyncPending()) return;
                    ApiResult r = api.Call("GET", "/api/agent/state", null);
                    if (r.NetError || r.Status != 200) return;
                    Dictionary<string, object> open = Util.D(r.Data, "open");
                    Ui(delegate
                    {
                        if (open != null && ShiftActive)
                        {
                            startUtc = Util.ParseIso(Util.S(open, "start_ts"));
                            pausedMs = Util.L(open, "paused_ms");
                            string nowS = Util.S(r.Data, "now");
                            if (nowS != null) serverOffset = Util.ParseIso(nowS) - DateTime.UtcNow;
                            offlineMode = false;
                            mini.SetStatus("Связь восстановлена, время отправлено");
                        }
                    });
                });
                return;
            }
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                ApiResult r = api.Call("POST", "/api/agent/heartbeat", b);
                if (r.NetError) { Ui(delegate { mini.SetStatus("Нет связи с сервером, время идёт"); }); return; }
                if (r.Status == 401)
                {   // администратор вышел из аккаунта на этом ПК: просим войти заново
                    ClearSession();
                    Ui(delegate
                    {
                        mini.Hide();
                        ShowMode("login", "Вход на этом компьютере был сброшен. Войдите снова — смена продолжится.");
                        lockForm.Show();
                        if (cfg.BlockKeys && !Program.Windowed) keys.Install();
                    });
                    return;
                }
                if (r.Status != 200) return;
                bool open = Util.S(r.Data, "open") == "True";
                bool stale = Util.S(r.Data, "stale") == "True";
                if (!open) { Ui(delegate { EndShiftLocal("Смена была закрыта на сервере."); }); return; }
                if (stale) { Dictionary<string, object> rb = new Dictionary<string, object>(); rb["run_id"] = runId; api.Call("POST", "/api/agent/resume", rb); }
                Ui(delegate { mini.SetStatus("Связь с сервером: есть"); });
            });
        }

        void FinishClicked()
        {
            if (busy) return;
            if (MessageBox.Show(mini, "Завершить смену? Время окончания будет сохранено.", "Завершение смены", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            busy = true;
            mini.BtnFinish.Enabled = false;
            bool wasOffline = offlineMode;
            Bg(delegate
            {
                if (wasOffline)
                {   // смена целиком сохранена на ПК: добавляем окончание и пробуем отправить
                    Dictionary<string, object> p = LoadPending();
                    p["end"] = Util.Iso(DateTime.UtcNow);
                    SavePending(p);
                    bool sent = SyncPending();
                    Ui(delegate { EndShiftLocal(sent ? "" : "Нет связи: смена сохранена на компьютере и отправится позже."); });
                    return;
                }
                ApiResult r = api.Call("POST", "/api/agent/finish", new Dictionary<string, object>());
                if (r.NetError)
                {
                    Dictionary<string, object> p = LoadPending();
                    p["end"] = Util.Iso(DateTime.UtcNow);
                    SavePending(p);
                    Ui(delegate { EndShiftLocal("Нет связи: время окончания сохранено и отправится позже."); });
                    return;
                }
                if (r.Status == 401)
                {
                    Dictionary<string, object> p = LoadPending();
                    p["end"] = Util.Iso(DateTime.UtcNow);
                    SavePending(p);
                    Ui(delegate { EndShiftLocal("Время окончания сохранено, оно отправится при следующем входе."); });
                    return;
                }
                if (r.Status != 200 && r.Status != 400)
                {
                    string er = r.Error;
                    Ui(delegate { busy = false; mini.BtnFinish.Enabled = true; MessageBox.Show(mini, er, "Не удалось завершить смену"); });
                    return;
                }
                Ui(delegate { EndShiftLocal(""); });
            });
        }

        void EndShiftLocal(string msg)
        {
            ShiftActive = false;
            offlineMode = false;
            tick.Stop();
            heartbeat.Stop();
            BlockShutdown(false);
            mini.Hide();
            mini.BtnFinish.Enabled = true;
            note = msg;
            ShowMode("finished", null);
            lockForm.Show();
            if (cfg.BlockKeys && !Program.Windowed) keys.Install();
            Log("Смена завершена. " + msg);
        }
    }

    // простое окно ввода PIN
    static class Prompt
    {
        public static string Ask(string title, string text)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(320, 120);
                f.TopMost = true;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                Label l = new Label { Text = text, Location = new Point(12, 12), Size = new Size(296, 22) };
                TextBox t = new TextBox { Location = new Point(12, 40), Size = new Size(296, 24), UseSystemPasswordChar = true };
                Button ok = new Button { Text = "OK", Location = new Point(12, 78), Size = new Size(120, 30), DialogResult = DialogResult.OK };
                f.Controls.AddRange(new Control[] { l, t, ok });
                f.AcceptButton = ok;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }
    }

    static class Program
    {
        public static bool Windowed;

        [STAThread]
        static void Main(string[] args)
        {
            Windowed = Array.IndexOf(args, "--windowed") >= 0;   // для проверки: обычное окно без блокировок
            bool created;
            Mutex mutex = new Mutex(true, "OfficeTimeClient_" + Environment.UserName, out created);
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Controller.Log("UI: " + e.Exception); };
            new Controller().Start();
            Application.Run();
            GC.KeepAlive(mutex);
        }
    }
}
