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
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
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
        public bool StartServer = false;   // компьютер администратора: программа сама запускает сайт (node server.js)
        public string ServerDir = "";      // папка с server.js (по умолчанию — папка выше программы)

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
                        else if (k == "start_server") c.StartServer = (v == "1" || v.ToLowerInvariant() == "yes");
                        else if (k == "server_dir") c.ServerDir = v;
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
        string role;
        NotifyIcon tray;
        string note = "";
        bool offlineMode;
        DateTime startUtc;
        long pausedMs;
        TimeSpan serverOffset = TimeSpan.Zero;
        bool busy;
        string shiftKind = "office";
        bool pauseActive;
        DateTime pauseStartUtc;
        int pausePlannedMin;
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
            Program.NoBlock = Program.Windowed || cfg.StartServer;   // на ПК администратора ничего не блокируем
            api = new Api(cfg.Server);
            LoadSession();

            lockForm = new LockForm(Program.NoBlock);
            mini = new MiniForm(this);
            IntPtr h1 = lockForm.Handle, h2 = mini.Handle; // создаём окна сразу, чтобы потоки могли к ним обращаться
            lockForm.MainClicked += OnMain;
            lockForm.SecondClicked += OnSecond;
            lockForm.PinRequested += OnPin;
            mini.BtnFinish.Click += delegate { FinishClicked(); };
            mini.BtnPause.Click += delegate { PauseClicked(); };

            tick.Interval = 1000;
            tick.Tick += delegate { OnTick(); };
            heartbeat.Interval = 30000;
            heartbeat.Tick += delegate { OnHeartbeat(); };

            // плашка не даёт перейти к другим окнам: возвращаем её наверх
            Timer top = new Timer();
            top.Interval = 700;
            top.Tick += delegate
            {
                if (lockForm.Visible && !Program.NoBlock) { lockForm.TopMost = true; lockForm.Activate(); }
            };
            top.Start();

            Log("Запуск, сервер " + cfg.Server);
            ShowMode("connecting", null);
            if (!cfg.StartServer) lockForm.Show();   // на ПК администратора окно появится, только если нужен вход
            if (cfg.BlockKeys && !Program.NoBlock) keys.Install();
            Bg(delegate { if (cfg.StartServer) EnsureServer(); RefreshState(); });
        }

        // ПК администратора: если сайт не запущен, запускаем его сами (скрытно)
        void EnsureServer()
        {
            ApiResult probe = api.Call("GET", "/api/agent/state", null);
            if (!probe.NetError) return;   // сервер уже работает
            try
            {
                string dir = cfg.ServerDir.Length > 0 ? cfg.ServerDir : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                if (!File.Exists(Path.Combine(dir, "server.js"))) { Log("server.js не найден в " + dir); return; }
                ProcessStartInfo psi = new ProcessStartInfo("node", "server.js");
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                try { Uri u = new Uri(cfg.Server); psi.EnvironmentVariables["PORT"] = u.Port.ToString(); } catch (Exception) { }
                Process.Start(psi);
                Log("Запущен сайт: node server.js в " + dir);
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(500);
                    if (!api.Call("GET", "/api/agent/state", null).NetError) return;
                }
                Log("Сайт не ответил после запуска");
            }
            catch (Exception ex) { Log("Не удалось запустить сайт: " + ex.Message); }
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
                role = Util.S(d, "role");
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
                d["role"] = role;
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
                if (p.ContainsKey("kind")) b["kind"] = p["kind"];
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
                    lockForm.Setup("Здравствуйте, " + name + "!", (note.Length > 0 ? note + "\n" : "") + "Выберите, чем вы занимаетесь, и нажмите «Начать смену».", error, false, "Начать смену", null, true); break;
                case "resume":
                    lockForm.Setup("С возвращением, " + name + "!", "Ваша смена («" + Theme.KindName(shiftKind) + "») идёт с " + startUtc.ToLocalTime().ToString("HH:mm") + ". Нажмите «Продолжить смену», чтобы вернуться к работе.", error, false, "Продолжить смену", null); break;
                case "offline":
                    {
                        bool canOffline = fio != null && role != "admin";
                        lockForm.Setup("Нет связи с сервером",
                            "Проверьте, что компьютер администратора включён." + (canOffline ? "\nМожно начать работу без связи: выберите тип, время сохранится на этом компьютере и отправится позже." : "\nПервый вход возможен только при наличии связи."),
                            error, false, "Повторить", canOffline ? "Работать без связи" : null, canOffline);
                        break;
                    }
                case "other":
                    lockForm.Setup("Идёт другое время", "У вас запущено вечернее время или удалённая работа. Завершите его на сайте и нажмите «Повторить».", error, false, "Повторить", null); break;
                case "paused":
                    {
                        string[] t = PauseTexts();
                        lockForm.Setup(t[0], t[1], error, false, t[2], null);
                        break;
                    }
                case "finished":
                    lockForm.Setup("Смена завершена", "Спасибо за работу! Компьютер можно выключить." + (note.Length > 0 ? "\n" + note : ""), error, false, "Выключить компьютер", "Начать новую смену"); break;
            }
            if (m != "connecting" && !lockForm.Visible) lockForm.Show();
            lockForm.SetBusy(false);
            busy = false;
        }

        // --- администратор: та же программа, но вместо плашки открывается панель администратора ---
        void AdminMode()
        {
            note = "";
            lockForm.Hide();
            keys.Remove();
            EnsureTray();
            Log("Администратор " + fio + ": открываю панель");
            OpenAdminPanel();
        }

        void EnsureTray()
        {
            if (tray != null) return;
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "Учёт времени (администратор)";
            ContextMenuStrip menu = new ContextMenuStrip();
            string addr = "http://" + Setup.LocalIp() + ":3000";
            try { addr = "http://" + Setup.LocalIp() + ":" + new Uri(cfg.Server).Port; } catch (Exception) { }
            menu.Items.Add("Адрес для других ПК: " + addr + " (скопировать)", null, delegate { try { Clipboard.SetText(addr); } catch (Exception) { } });
            menu.Items.Add("Открыть панель администратора", null, delegate { OpenAdminPanel(); });
            menu.Items.Add("Сменить аккаунт на этом ПК", null, delegate { SwitchAccount(); });
            menu.Items.Add("Закрыть программу", null, delegate { tray.Visible = false; lockForm.AllowClose = true; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OpenAdminPanel(); };
            tray.Visible = true;
        }

        void SwitchAccount()
        {
            ClearSession();
            fio = null;
            role = null;
            ShowMode("login", null);
        }

        void OpenAdminPanel()
        {
            Bg(delegate
            {
                ApiResult r = api.Call("POST", "/api/agent/ticket", new Dictionary<string, object>());
                if (r.NetError) { Ui(delegate { MessageBox.Show("Нет связи с сервером. Проверьте, что сайт запущен.", "Учёт времени"); }); return; }
                if (r.Status == 401) { ClearSession(); Ui(delegate { ShowMode("login", "Нужно войти заново."); }); return; }
                string code = Util.S(r.Data, "code");
                if (code == null) return;
                string url = api.Base + "/api/agent/open?c=" + code;
                Ui(delegate { LaunchBrowser(url); });
            });
        }

        // Открываем сайт в отдельном окне Edge без адресной строки (есть на всех Windows 10/11); иначе — в браузере по умолчанию
        void LaunchBrowser(string url)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("OFFICETIME_TEST") == "1") { Log("ТЕСТ: адрес панели " + url); return; }   // только для проверки
                string[] roots = { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") };
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string edge = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                    if (File.Exists(edge)) { Process.Start(edge, "--app=\"" + url + "\""); return; }
                }
                Process.Start(url);
            }
            catch (Exception ex) { Log("Не удалось открыть панель: " + ex.Message); }
        }

        void OnMain(object s, EventArgs e)
        {
            if (busy) return;
            switch (mode)
            {
                case "login": DoLogin(); break;
                case "start": DoStart(); break;
                case "resume": DoResume(); break;
                case "paused": EndPause(); break;
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
            string f = Util.S(user, "fio"), rl = Util.S(user, "role");
            Dictionary<string, object> open = Util.D(r.Data, "open");
            Dictionary<string, object> other = Util.D(r.Data, "other");
            string nowS = Util.S(r.Data, "now");
            Ui(delegate
            {
                if (f != null) { fio = f; if (rl != null) role = rl; SaveSession(); }
                if (role == "admin") { AdminMode(); return; }   // администратору плашка и смена не нужны: открывается панель
                if (nowS != null) serverOffset = Util.ParseIso(nowS) - DateTime.UtcNow;
                if (other != null) { ShowMode("other", null); return; }
                if (open != null)
                {
                    startUtc = Util.ParseIso(Util.S(open, "start_ts"));
                    pausedMs = Util.L(open, "paused_ms");
                    shiftKind = Util.S(open, "kind") ?? "office";
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
                role = Util.S(Util.D(r.Data, "user"), "role");
                Ui(delegate { SaveSession(); });
                Log("Выполнен вход: " + fio);
                RefreshState();
            });
        }

        void DoStart()
        {
            string kindChoice = lockForm.SelectedKind;   // «Офис», «Вечернее время» или «Удалённо»
            busy = true;
            lockForm.SetBusy(true);
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                b["kind"] = kindChoice;
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
            shiftKind = Util.S(open, "kind") ?? "office";
            mini.SetKind(shiftKind);
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
            if (Util.D(data, "pause") != null) BeginPause(data);   // пауза уже идёт на сервере
        }

        void OfflineStart()
        {
            Dictionary<string, object> p = LoadPending();
            p["start"] = Util.Iso(DateTime.UtcNow);
            p["kind"] = lockForm.SelectedKind;
            SavePending(p);
            shiftKind = lockForm.SelectedKind;
            mini.SetKind(shiftKind);
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
            if (Program.NoBlock) return;
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
            if (pauseActive)
            {   // на паузе рабочее время стоит, а на плашке идёт отсчёт паузы (и после её окончания тоже)
                if (lockForm.Visible && mode == "paused") { string[] t = PauseTexts(); lockForm.UpdateTexts(t[0], t[1], t[2]); }
                return;
            }
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
                        if (cfg.BlockKeys && !Program.NoBlock) keys.Install();
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
            if (!Prompt.Confirm(mini, "Завершение смены", "Завершить смену? Время окончания будет сохранено.")) return;
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

        // --- пауза: сотрудник отходит и указывает, на сколько минут ---
        static string Fmt(TimeSpan t) { return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); }

        string[] PauseTexts()
        {
            TimeSpan elapsed = DateTime.UtcNow + serverOffset - pauseStartUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            TimeSpan planned = TimeSpan.FromMinutes(pausePlannedMin);
            if (elapsed < planned)
                return new string[] { "Пауза", "Осталось " + Fmt(planned - elapsed) + " из " + pausePlannedMin + " мин.\nКогда вернётесь, нажмите «Вернуться к работе».", "Вернуться к работе" };
            return new string[] { "Время паузы закончилось", "Продолжаем работу?\nПауза идёт уже " + Fmt(elapsed) + " (сверх плана " + Fmt(elapsed - planned) + "). Это время тоже записывается как пауза.", "Продолжить работу" };
        }

        void PauseClicked()
        {
            if (busy || pauseActive) return;
            if (offlineMode) { MessageBox.Show(mini, "Нет связи с сервером: пауза сейчас недоступна.", "Пауза"); return; }
            int? minutes = Prompt.AskNumber(mini, "Пауза", "На сколько минут вы берёте паузу?", 15, 1, 720);
            if (minutes == null) return;
            busy = true;
            mini.BtnPause.Enabled = false;
            int m = minutes.Value;
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                b["minutes"] = m;
                ApiResult r = api.Call("POST", "/api/agent/pause", b);
                if (r.Status == 200) { Ui(delegate { busy = false; mini.BtnPause.Enabled = true; BeginPause(r.Data); }); return; }
                string er = r.NetError ? "Нет связи с сервером: пауза не записана." : r.Error;
                Ui(delegate { busy = false; mini.BtnPause.Enabled = true; MessageBox.Show(mini, er, "Не удалось начать паузу"); });
            });
        }

        void BeginPause(Dictionary<string, object> data)
        {
            Dictionary<string, object> p = Util.D(data, "pause");
            if (p == null) return;
            pauseStartUtc = Util.ParseIso(Util.S(p, "start_ts"));
            pausePlannedMin = (int)Util.L(p, "planned_min");
            string nowS = Util.S(data, "now");
            if (nowS != null) serverOffset = Util.ParseIso(nowS) - DateTime.UtcNow;
            pauseActive = true;
            mini.Hide();
            ShowMode("paused", null);
            lockForm.Show();
            if (cfg.BlockKeys && !Program.NoBlock) keys.Install();
            Log("Пауза на " + pausePlannedMin + " мин");
        }

        void EndPause()
        {
            busy = true;
            lockForm.SetBusy(true);
            Bg(delegate
            {
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["run_id"] = runId;
                ApiResult r = api.Call("POST", "/api/agent/pause-end", b);
                if (r.Status == 200)
                {
                    Dictionary<string, object> open = Util.D(r.Data, "open");
                    Ui(delegate
                    {
                        if (open != null) pausedMs = Util.L(open, "paused_ms");
                        pauseActive = false;
                        busy = false;
                        lockForm.SetBusy(false);
                        lockForm.Hide();
                        keys.Remove();
                        mini.SetStatus("Связь с сервером: есть");
                        mini.Show();
                        OnTick();
                        Log("Возврат с паузы");
                    });
                    return;
                }
                string er = r.NetError ? "Нет связи с сервером. Нажмите ещё раз, когда связь появится." : r.Error;
                Ui(delegate { ShowMode("paused", er); });
            });
        }

        void EndShiftLocal(string msg)
        {
            ShiftActive = false;
            pauseActive = false;
            offlineMode = false;
            tick.Stop();
            heartbeat.Stop();
            BlockShutdown(false);
            mini.Hide();
            mini.BtnFinish.Enabled = true;
            note = msg;
            ShowMode("finished", null);
            lockForm.Show();
            if (cfg.BlockKeys && !Program.NoBlock) keys.Install();
            Log("Смена завершена. " + msg);
        }
    }

    // простое окно ввода PIN
    static class Prompt
    {
        // Окно в общем стиле: тёмная карточка со скруглёнными кнопками
        static Form Styled(string title, int w, int h)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(w, h);
            f.TopMost = true;
            f.ShowInTaskbar = false;
            f.BackColor = Theme.H("#121A44");
            f.ForeColor = Color.White;
            f.Font = new Font("Segoe UI", 10.5f);
            f.Region = new Region(Theme.Round(new RectangleF(0, 0, w, h), 22));
            f.Paint += delegate (object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, w - 2, h - 2), 22))
                using (Pen pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1.2f)) e.Graphics.DrawPath(pen, p);
            };
            return f;
        }

        public static bool Confirm(IWin32Window owner, string title, string text)
        {
            using (Form f = Styled(title, 420, 190))
            {
                Label t = new Label { Text = title, Location = new Point(26, 22), Size = new Size(368, 30), Font = new Font("Segoe UI Semibold", 15f), BackColor = Color.Transparent };
                Label d = new Label { Text = text, Location = new Point(26, 62), Size = new Size(368, 48), ForeColor = Theme.Muted, BackColor = Color.Transparent };
                RoundButton yes = new RoundButton { Text = "Да", Location = new Point(26, 126), Size = new Size(176, 44), DialogResult = DialogResult.Yes, From = Theme.Red1, To = Theme.Red2 };
                RoundButton no = new RoundButton { Text = "Нет", Location = new Point(214, 126), Size = new Size(180, 44), DialogResult = DialogResult.No, Ghost = true };
                f.Controls.AddRange(new Control[] { t, d, yes, no });
                f.AcceptButton = yes;
                f.CancelButton = no;
                return f.ShowDialog(owner) == DialogResult.Yes;
            }
        }

        public static int? AskNumber(IWin32Window owner, string title, string text, int def, int min, int max)
        {
            using (Form f = Styled(title, 420, 230))
            {
                Label t = new Label { Text = title, Location = new Point(26, 22), Size = new Size(368, 30), Font = new Font("Segoe UI Semibold", 15f), BackColor = Color.Transparent };
                Label d = new Label { Text = text, Location = new Point(26, 60), Size = new Size(368, 26), ForeColor = Theme.Muted, BackColor = Color.Transparent };
                NumericUpDown n = new NumericUpDown { Location = new Point(26, 96), Size = new Size(120, 36), Minimum = min, Maximum = max, Value = def, Font = new Font("Segoe UI", 16f), BackColor = Theme.Field, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
                Label u = new Label { Text = "минут", Location = new Point(156, 104), Size = new Size(120, 26), ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 12f) };
                RoundButton ok = new RoundButton { Text = "Начать паузу", Location = new Point(26, 160), Size = new Size(200, 48), DialogResult = DialogResult.OK, From = Theme.Amber1, To = Theme.Amber2, Fore = Theme.H("#2A1B00") };
                RoundButton cancel = new RoundButton { Text = "Отмена", Location = new Point(238, 160), Size = new Size(156, 48), DialogResult = DialogResult.Cancel, Ghost = true };
                f.Controls.AddRange(new Control[] { t, d, n, u, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? (int?)(int)n.Value : null;
            }
        }

        public static string AskText(string title, string text, string initial)
        {
            using (Form f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(460, 190);
                f.TopMost = true;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.Font = new Font("Segoe UI", 10f);
                Label l = new Label { Text = text, Location = new Point(14, 12), Size = new Size(432, 66) };
                TextBox t = new TextBox { Text = initial, Location = new Point(14, 88), Size = new Size(432, 28), Font = new Font("Segoe UI", 12f) };
                Button ok = new Button { Text = "Готово", Location = new Point(14, 136), Size = new Size(140, 38), DialogResult = DialogResult.OK };
                Button cancel = new Button { Text = "Отмена", Location = new Point(164, 136), Size = new Size(120, 38), DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

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

    // ---------- первая настройка: всё делается само ----------
    static class Setup
    {
        const int DiscoveryPort = 41234;
        static bool Test { get { return Environment.GetEnvironmentVariable("OFFICETIME_TEST") == "1"; } }   // при проверке не трогаем систему

        public static string ConfigPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini"); } }
        public static bool NeedsSetup() { return !File.Exists(ConfigPath); }

        // false — настройку отменили или она не удалась (программа закроется)
        public static bool Run()
        {
            string who = AskWho();
            if (who == null) return false;
            if (who == "admin") return SetupAdmin();
            return SetupWorker();
        }

        static string AskWho()
        {
            using (Form f = new Form())
            {
                f.Text = "Учёт времени: первая настройка";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(460, 250);
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.TopMost = true;
                f.BackColor = ColorTranslator.FromHtml("#16202E");
                f.ForeColor = Color.White;
                f.Font = new Font("Segoe UI", 10f);
                Label t = new Label { Text = "Что это за компьютер?", Location = new Point(24, 20), Size = new Size(412, 34), Font = new Font("Segoe UI Semibold", 16f) };
                Label d = new Label { Text = "Остальное программа настроит сама.", Location = new Point(24, 58), Size = new Size(412, 22), ForeColor = ColorTranslator.FromHtml("#9FB0D9") };
                Button a = new Button { Text = "Компьютер администратора", Location = new Point(24, 96), Size = new Size(412, 56), DialogResult = DialogResult.Yes, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 12f), BackColor = ColorTranslator.FromHtml("#3B6FD4") };
                Button w = new Button { Text = "Компьютер сотрудника", Location = new Point(24, 164), Size = new Size(412, 56), DialogResult = DialogResult.No, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 12f), BackColor = ColorTranslator.FromHtml("#2F4059") };
                a.FlatAppearance.BorderSize = 0;
                w.FlatAppearance.BorderSize = 0;
                f.Controls.AddRange(new Control[] { t, d, a, w });
                DialogResult r = f.ShowDialog();
                if (r == DialogResult.Yes) return "admin";
                if (r == DialogResult.No) return "worker";
                return null;
            }
        }

        // ПК администратора: сайт запускается этой программой, порт открывается в брандмауэре, автозапуск при входе в Windows
        static bool SetupAdmin()
        {
            string nodeErr = CheckNode();
            if (nodeErr != null) { MessageBox.Show(nodeErr, "Учёт времени", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            WriteConfig("http://localhost:3000", true);
            bool fw = OpenFirewall(3000);
            AddAutostart();
            string ip = LocalIp();
            MessageBox.Show(
                "Компьютер администратора настроен.\n\n• сайт будет запускаться сам при входе в Windows;\n• автозапуск программы включён;\n" +
                (fw ? "• доступ для других компьютеров открыт.\n" : "• ВНИМАНИЕ: доступ в брандмауэре открыть не удалось (нужно было разрешить запрос Windows). Без него другие компьютеры не подключатся — запустите программу ещё раз после удаления файла config.ini.\n") +
                "\nКомпьютеры сотрудников найдут сервер сами. Если не найдут, адрес такой:\nhttp://" + ip + ":3000",
                "Учёт времени", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        static bool SetupWorker()
        {
            string url = Discover();
            if (url != null) MessageBox.Show("Сервер найден автоматически:\n" + url, "Учёт времени", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
            {
                url = Prompt.AskText("Адрес сервера", "Сервер не найден автоматически. Проверьте, что компьютер администратора включён, или введите адрес, который показывает программа на нём:", "http://");
                if (url == null || url.Trim().Length < 8) return false;
                url = url.Trim();
                if (!url.StartsWith("http")) url = "http://" + url;
            }
            WriteConfig(url, false);
            AddAutostart();
            return true;
        }

        static void WriteConfig(string server, bool admin)
        {
            string text = "server=" + server + "\r\n" + (admin ? "start_server=1\r\n" : "") + "# unlock_pin=1234\r\n# block_keys=0\r\n";
            File.WriteAllText(ConfigPath, text, new UTF8Encoding(false));
        }

        static string CheckNode()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("node", "--version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string v = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(5000);
                    int major;
                    if (v.StartsWith("v") && int.TryParse(v.Substring(1).Split('.')[0], out major))
                        return major >= 22 ? null : "Установлен Node.js " + v + ", а нужен версии 22 или новее. Скачайте с nodejs.org и запустите программу снова.";
                }
            }
            catch (Exception) { }
            return "Не найден Node.js (на нём работает сайт). Установите его с nodejs.org (версия 22 или новее) и запустите программу снова.";
        }

        // Открываем порты в брандмауэре Windows: для этого программа сама запускает свою копию с запросом прав администратора
        static bool OpenFirewall(int port)
        {
            if (Test) { Controller.Log("ТЕСТ: открытие брандмауэра пропущено"); return true; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--firewall " + port);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(30000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Controller.Log("Брандмауэр: " + ex.Message); return false; }   // запрос Windows отклонён
        }

        // Режим «--firewall <порт>» (запускается с правами администратора): правила для сайта и автопоиска
        public static int FirewallHelper(int port)
        {
            Netsh("advfirewall firewall delete rule name=\"OfficeTime\"");
            int a = Netsh("advfirewall firewall add rule name=\"OfficeTime\" dir=in action=allow protocol=TCP localport=" + port + " profile=any");
            int b = Netsh("advfirewall firewall add rule name=\"OfficeTime discovery\" dir=in action=allow protocol=UDP localport=" + DiscoveryPort + " profile=any");
            return (a == 0 && b == 0) ? 0 : 1;
        }

        static int Netsh(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi)) { p.WaitForExit(15000); return p.ExitCode; }
            }
            catch (Exception) { return 1; }
        }

        static void AddAutostart()
        {
            if (Test) { Controller.Log("ТЕСТ: автозапуск пропущен"); return; }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    k.SetValue("OfficeTimeClient", "\"" + Application.ExecutablePath + "\"");
            }
            catch (Exception ex) { Controller.Log("Автозапуск: " + ex.Message); }
        }

        public static string LocalIp()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties ip = ni.GetIPProperties();
                    if (ip.GatewayAddresses.Count == 0) continue;
                    foreach (UnicastIPAddressInformation ua in ip.UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
                }
            }
            catch (Exception) { }
            return "адрес-этого-компьютера";
        }

        // Ищем сервер в локальной сети: широковещательный запрос, ответ приходит с адресом и портом сервера
        public static string Discover()
        {
            try
            {
                List<IPAddress> targets = new List<IPAddress>();
                targets.Add(IPAddress.Broadcast);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes(), bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~m[i]);
                        targets.Add(new IPAddress(bc));
                    }
                }
                using (UdpClient c = new UdpClient())
                {
                    c.EnableBroadcast = true;
                    c.Client.ReceiveTimeout = 1000;
                    byte[] msg = Encoding.ASCII.GetBytes("OFFICETIME?");
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        foreach (IPAddress t in targets) { try { c.Send(msg, msg.Length, new IPEndPoint(t, DiscoveryPort)); } catch (Exception) { } }
                        try
                        {
                            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            string s = Encoding.ASCII.GetString(c.Receive(ref from));
                            int port;
                            if (s.StartsWith("OFFICETIME ") && int.TryParse(s.Substring(11).Trim(), out port)) return "http://" + from.Address + ":" + port;
                        }
                        catch (SocketException) { }
                    }
                }
            }
            catch (Exception ex) { Controller.Log("Автопоиск: " + ex.Message); }
            return null;
        }
    }

    static class Program
    {
        public static bool Windowed;   // --windowed: для проверки, обычное окно
        public static bool NoBlock;    // не блокировать экран, клавиши и выключение (проверка или ПК администратора)

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--firewall")   // вспомогательный запуск с правами администратора
            {
                int port;
                Environment.Exit(int.TryParse(args[1], out port) ? Setup.FirewallHelper(port) : 1);
                return;
            }
            if (args.Length >= 1 && args[0] == "--discover")   // проверка автопоиска сервера
            {
                string found = Setup.Discover();
                Controller.Log("Автопоиск: " + (found != null ? found : "сервер не найден"));
                return;
            }
            Windowed = Array.IndexOf(args, "--windowed") >= 0;   // для проверки: обычное окно без блокировок
            bool created;
            Mutex mutex = new Mutex(true, "OfficeTimeClient_" + Environment.UserName, out created);
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Controller.Log("UI: " + e.Exception); };
            if (Setup.NeedsSetup() && !Setup.Run()) return;   // при первом запуске программа спрашивает, что это за ПК, и настраивает всё сама
            new Controller().Start();
            Application.Run();
            GC.KeepAlive(mutex);
        }
    }
}
