// Оформление программы: тёмный градиент, карточки со скруглениями, крупные часы, градиентные кнопки.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace OfficeTime
{
    static class Theme
    {
        public static Color H(string hex) { return ColorTranslator.FromHtml(hex); }
        public static readonly Color Bg1 = H("#060A16"), Bg2 = H("#17235C");
        public static readonly Color Accent1 = H("#5B8CFF"), Accent2 = H("#8B5CF6");
        public static readonly Color Text = H("#F3F6FF"), Muted = H("#9FB0D9");
        public static readonly Color Green1 = H("#34D8A4"), Green2 = H("#1FA06A");
        public static readonly Color Amber1 = H("#FFC84A"), Amber2 = H("#F29A0B");
        public static readonly Color Red1 = H("#FF7A7A"), Red2 = H("#E0434B");
        public static readonly Color Field = H("#0E1636");

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(1f, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Lighter(Color c, float k)
        {
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));
        }

        public static string KindName(string kind)
        {
            if (kind == "evening") return "Вечернее время";
            if (kind == "remote") return "Удалённо";
            return "Офис";
        }

        public static Color KindColor(string kind)
        {
            if (kind == "evening") return H("#8B5CF6");
            if (kind == "remote") return H("#22B8CF");
            return H("#5B8CFF");
        }

        // Линейные значки: здание (офис), луна (вечернее время), дом (удалённо)
        public static void Icon(Graphics g, string kind, RectangleF r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(c, 2.4f))
            using (Brush b = new SolidBrush(c))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (kind == "evening")
                {
                    using (GraphicsPath moon = new GraphicsPath())
                    using (GraphicsPath cut = new GraphicsPath())
                    {
                        moon.AddEllipse(r.X + r.Width * 0.08f, r.Y + r.Height * 0.04f, r.Width * 0.84f, r.Height * 0.92f);
                        cut.AddEllipse(r.X + r.Width * 0.38f, r.Y - r.Height * 0.08f, r.Width * 0.74f, r.Height * 0.82f);
                        using (Region reg = new Region(moon))
                        {
                            reg.Exclude(cut);
                            g.FillRegion(b, reg);
                        }
                    }
                }
                else if (kind == "remote")
                {
                    PointF[] roof = { new PointF(r.X + 1, r.Y + r.Height * 0.46f), new PointF(r.X + r.Width / 2, r.Y + 1), new PointF(r.Right - 1, r.Y + r.Height * 0.46f) };
                    g.DrawLines(pen, roof);
                    g.DrawRectangle(pen, r.X + r.Width * 0.16f, r.Y + r.Height * 0.46f, r.Width * 0.68f, r.Height * 0.52f);
                    g.FillRectangle(b, r.X + r.Width * 0.43f, r.Y + r.Height * 0.66f, r.Width * 0.14f, r.Height * 0.32f);
                }
                else
                {
                    g.DrawRectangle(pen, r.X + r.Width * 0.2f, r.Y + 1, r.Width * 0.6f, r.Height - 2);
                    for (int row = 0; row < 3; row++)
                    {
                        g.FillRectangle(b, r.X + r.Width * 0.34f, r.Y + r.Height * (0.18f + row * 0.22f), r.Width * 0.12f, r.Height * 0.1f);
                        g.FillRectangle(b, r.X + r.Width * 0.54f, r.Y + r.Height * (0.18f + row * 0.22f), r.Width * 0.12f, r.Height * 0.1f);
                    }
                }
            }
        }
    }

    // Кнопка с градиентом и скруглением
    class RoundButton : Button
    {
        public Color From = Theme.Accent1, To = Theme.Accent2, Fore = Color.White;
        public float Radius = 16;
        public bool Ghost;
        bool hover, down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 13f);
        }

        // Углы кнопки обрезаем по скруглению: за пределами формы кнопки видно то, что под ней
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width > 6 && Height > 6)
                using (GraphicsPath gp = Theme.Round(new RectangleF(0, 0, Width, Height), Radius + 1)) Region = new Region(gp);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (Width < 6 || Height < 6) return;
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.Round(r, Radius))
            {
                if (!Enabled)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillPath(b, p);
                }
                else if (Ghost)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(down ? 70 : hover ? 52 : 30, 255, 255, 255))) g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(hover ? 120 : 70, 255, 255, 255), 1.2f)) g.DrawPath(pen, p);
                }
                else
                {
                    float k = down ? -0.08f : hover ? 0.14f : 0f;
                    Color a = k >= 0 ? Theme.Lighter(From, k) : Color.FromArgb(From.R * 9 / 10, From.G * 9 / 10, From.B * 9 / 10);
                    Color b2 = k >= 0 ? Theme.Lighter(To, k) : Color.FromArgb(To.R * 9 / 10, To.G * 9 / 10, To.B * 9 / 10);
                    using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), a, b2, 20f)) g.FillPath(br, p);
                    using (Pen pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                }
            }
            using (StringFormat sf = new StringFormat())
            using (Brush tb = new SolidBrush(Enabled ? Fore : Color.FromArgb(150, 255, 255, 255)))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                RectangleF tr = new RectangleF(0, down ? 1 : 0, Width, Height);
                g.DrawString(Text, Font, tb, tr, sf);
            }
        }
    }

    // Поле ввода со скруглением
    class RoundedInput : Control
    {
        public TextBox Box = new TextBox();
        bool focus;

        public RoundedInput(bool password)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Box.BorderStyle = BorderStyle.None;
            Box.UseSystemPasswordChar = password;
            Box.Font = new Font("Segoe UI", 14f);
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Box.GotFocus += (s, e) => { focus = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focus = false; Invalidate(); };
            Controls.Add(Box);
            Height = 54;
        }

        public string Value { get { return Box.Text; } set { Box.Text = value; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Box.Left = 18;
            Box.Width = Math.Max(10, Width - 36);
            Box.Top = (Height - Box.Height) / 2;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 6 || Height < 6) return;
            using (GraphicsPath p = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), 14))
            {
                using (Brush b = new SolidBrush(Theme.Field)) g.FillPath(b, p);
                using (Pen pen = new Pen(focus ? Theme.Accent1 : Color.FromArgb(60, 255, 255, 255), focus ? 2f : 1.2f)) g.DrawPath(pen, p);
            }
        }
    }

    // Плитка выбора типа смены: Офис / Вечернее время / Удалённо
    class KindChip : Control
    {
        public string Key;
        public bool Selected;
        public event EventHandler Picked;
        bool hover;

        public KindChip(string key)
        {
            Key = key;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Text = Theme.KindName(key);
            AccessibleName = Text;
            Font = new Font("Segoe UI Semibold", 11.5f);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { if (Picked != null) Picked(this, e); base.OnClick(e); }
        public void InvokePick() { OnClick(EventArgs.Empty); }

        // Плитка видна программам доступности как кнопка с названием
        class ChipAccessible : Control.ControlAccessibleObject
        {
            KindChip chip;
            public ChipAccessible(KindChip owner) : base(owner) { chip = owner; }
            public override string DefaultAction { get { return "Выбрать"; } }
            public override void DoDefaultAction() { chip.InvokePick(); }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override AccessibleStates State { get { return chip.Selected ? AccessibleStates.Checked | AccessibleStates.Focusable : AccessibleStates.Focusable; } }
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ChipAccessible(this); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (Width < 10 || Height < 10) return;
            RectangleF r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (GraphicsPath p = Theme.Round(r, 16))
            {
                if (Selected)
                {
                    Color c = Theme.KindColor(Key);
                    using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), c, Theme.Lighter(c, -0.0f), 90f))
                    {
                        ColorBlend cb = new ColorBlend(2);
                        cb.Colors = new Color[] { Theme.Lighter(c, 0.18f), Color.FromArgb(c.R * 8 / 10, c.G * 8 / 10, c.B * 8 / 10) };
                        cb.Positions = new float[] { 0f, 1f };
                        br.InterpolationColors = cb;
                        g.FillPath(br, p);
                    }
                    using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f)) g.DrawPath(pen, p);
                }
                else
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(hover ? 46 : 26, 255, 255, 255))) g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(hover ? 100 : 56, 255, 255, 255), 1.2f)) g.DrawPath(pen, p);
                }
            }
            float s = 28;
            Theme.Icon(g, Key, new RectangleF((Width - s) / 2, 12, s, s), Selected ? Color.White : Theme.Muted);
            using (StringFormat sf = new StringFormat())
            using (Brush tb = new SolidBrush(Selected ? Color.White : Theme.Text))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Text, Font, tb, new RectangleF(0, 46, Width, Height - 50), sf);
            }
        }
    }

    // ---------- плашка на весь экран ----------
    class LockForm : Form
    {
        Label lblClock, lblDate, lblTitle, lblText, lblError, lblLoginCap, lblPassCap, lblKindCap;
        RoundedInput inLogin, inPass;
        KindChip[] chips;
        RoundButton btnMain, btnSecond;
        Timer clock = new Timer(), fade = new Timer();
        bool windowed;
        Rectangle card;
        bool showLogin, showKinds;
        string mainText, secondText, errorText = "";
        public bool AllowClose;
        public event EventHandler MainClicked, SecondClicked, PinRequested;

        const int CardW = 660, Pad = 44;

        public LockForm(bool windowed)
        {
            this.windowed = windowed;
            Text = "Учёт времени";
            DoubleBuffered = true;
            BackColor = Theme.Bg1;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            if (windowed)
            {
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                ClientSize = new Size(1100, 760);
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

            lblClock = Lbl("Segoe UI Light", 72f, Theme.Text);
            lblDate = Lbl("Segoe UI", 17f, Theme.Muted);
            lblTitle = Lbl("Segoe UI Semibold", 26f, Color.White);
            lblText = Lbl("Segoe UI", 13f, Theme.Muted);
            lblError = Lbl("Segoe UI Semibold", 11f, Theme.Red1);
            lblLoginCap = Lbl("Segoe UI Semibold", 10f, Theme.Muted);
            lblPassCap = Lbl("Segoe UI Semibold", 10f, Theme.Muted);
            lblKindCap = Lbl("Segoe UI Semibold", 10f, Theme.Muted);
            lblLoginCap.Text = "ЛОГИН";
            lblPassCap.Text = "ПАРОЛЬ";
            lblKindCap.Text = "ЧТО ВЫ БУДЕТЕ ДЕЛАТЬ";
            inLogin = new RoundedInput(false);
            inPass = new RoundedInput(true);
            chips = new KindChip[] { new KindChip("office"), new KindChip("evening"), new KindChip("remote") };
            chips[0].Selected = true;
            foreach (KindChip c in chips) c.Picked += (s, e) => { foreach (KindChip k in chips) k.Selected = (k == s); Invalidate(true); };
            btnMain = new RoundButton();
            btnSecond = new RoundButton { Ghost = true, Radius = 14, Font = new Font("Segoe UI Semibold", 11.5f) };
            btnMain.Click += (s, e) => { if (MainClicked != null) MainClicked(this, e); };
            btnSecond.Click += (s, e) => { if (SecondClicked != null) SecondClicked(this, e); };
            Controls.AddRange(new Control[] { lblClock, lblDate, lblTitle, lblText, lblError, lblLoginCap, inLogin, lblPassCap, inPass, lblKindCap, chips[0], chips[1], chips[2], btnMain, btnSecond });
            AcceptButton = btnMain;

            clock.Interval = 1000;
            clock.Tick += (s, e) => UpdateClock();
            clock.Start();
            UpdateClock();

            fade.Interval = 16;
            fade.Tick += (s, e) => { Opacity = Math.Min(1.0, Opacity + 0.12); if (Opacity >= 1.0) fade.Stop(); };
        }

        Label Lbl(string font, float size, Color c)
        {
            return new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = c, Font = new Font(font, size), UseCompatibleTextRendering = false };
        }

        void UpdateClock()
        {
            lblClock.Text = DateTime.Now.ToString("HH:mm");
            lblDate.Text = DateTime.Now.ToString("dddd, d MMMM", new CultureInfo("ru-RU"));
        }

        public string LoginText { get { return inLogin.Value.Trim(); } }
        public string PassText { get { return inPass.Value; } }
        public string SelectedKind
        {
            get { foreach (KindChip c in chips) if (c.Selected) return c.Key; return "office"; }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { Opacity = 0.0; fade.Start(); }
        }

        public void Setup(string title, string text, string error, bool login, string main, string second, bool kinds)
        {
            lblTitle.Text = title;
            lblText.Text = text;
            errorText = error != null ? error : "";
            lblError.Text = errorText;
            showLogin = login;
            showKinds = kinds;
            mainText = main;
            secondText = second;
            btnMain.Text = main != null ? main : "";
            btnSecond.Text = second != null ? second : "";
            LayoutCard();
            if (login) { inPass.Value = ""; inLogin.Box.Focus(); }
            else if (main != null) btnMain.Focus();
        }

        public void Setup(string title, string text, string error, bool login, string main, string second)
        {
            Setup(title, text, error, login, main, second, false);
        }

        public void UpdateTexts(string title, string text, string main)
        {
            bool relayout = text != lblText.Text && MeasureText(text) != lblText.Height;
            lblTitle.Text = title;
            lblText.Text = text;
            btnMain.Text = main;
            if (relayout) LayoutCard();
        }

        int MeasureText(string s)
        {
            Size sz = TextRenderer.MeasureText(s, lblText.Font, new Size(CardW - Pad * 2, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return sz.Height + 8;
        }

        // Вертикальная раскладка карточки: заголовок, текст, поля / выбор типа, ошибка, кнопки
        void LayoutCard()
        {
            int w = CardW - Pad * 2;
            int y = 40;
            Rectangle pb = windowed ? ClientRectangle : new Rectangle(Screen.PrimaryScreen.Bounds.X - Bounds.X, Screen.PrimaryScreen.Bounds.Y - Bounds.Y, Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);

            // положения считаем от (0, 0) карточки и затем сдвигаем на её место на экране
            lblTitle.SetBounds(0, y, w, 46); y += 52;
            int th = lblText.Text.Length > 0 ? MeasureText(lblText.Text) : 0;
            lblText.SetBounds(0, y, w, th); y += th + (th > 0 ? 24 : 0);

            lblLoginCap.Visible = inLogin.Visible = lblPassCap.Visible = inPass.Visible = showLogin;
            if (showLogin)
            {
                lblLoginCap.SetBounds(0, y, w, 20); y += 24;
                inLogin.SetBounds(0, y, w, 54); y += 54 + 14;
                lblPassCap.SetBounds(0, y, w, 20); y += 24;
                inPass.SetBounds(0, y, w, 54); y += 54 + 18;
            }
            lblKindCap.Visible = showKinds;
            foreach (KindChip c in chips) c.Visible = showKinds;
            if (showKinds)
            {
                lblKindCap.SetBounds(0, y, w, 20); y += 26;
                int cw = (w - 24) / 3;
                for (int i = 0; i < 3; i++) chips[i].SetBounds(i * (cw + 12), y, cw, 92);
                y += 92 + 24;
            }
            if (errorText.Length > 0) { lblError.SetBounds(0, y, w, 46); y += 50; }
            else lblError.SetBounds(0, y, w, 0);
            btnMain.Visible = mainText != null;
            if (mainText != null) { btnMain.SetBounds(0, y, w, 62); y += 62 + 14; }
            btnSecond.Visible = secondText != null;
            if (secondText != null) { btnSecond.SetBounds(0, y, w, 50); y += 50 + 14; }
            int h = y + 26 - 14 + 10;

            card = new Rectangle(pb.X + (pb.Width - CardW) / 2, pb.Y + (pb.Height - h) / 2, CardW, h);
            int dx = card.X + Pad, dy = card.Y;
            foreach (Control c in new Control[] { lblTitle, lblText, lblLoginCap, inLogin, lblPassCap, inPass, lblKindCap, chips[0], chips[1], chips[2], lblError, btnMain, btnSecond })
                c.Location = new Point(c.Left + dx - (c is Label ? 3 : 0), c.Top + dy);   // у подписей есть внутренний отступ в 3 px

            lblClock.SetBounds(pb.X + 64, pb.Y + 40, 700, 118);
            lblDate.SetBounds(pb.X + 70, pb.Y + 150, 700, 36);
            Invalidate(true);
        }

        public void SetBusy(bool busy)
        {
            btnMain.Enabled = !busy;
            btnSecond.Enabled = !busy;
            inLogin.Enabled = !busy;
            inPass.Enabled = !busy;
            foreach (KindChip c in chips) c.Enabled = !busy;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle cr = ClientRectangle;
            if (cr.Width < 4 || cr.Height < 4) return;
            using (LinearGradientBrush br = new LinearGradientBrush(cr, Theme.Bg1, Theme.Bg2, 125f)) g.FillRectangle(br, cr);
            Glow(g, new RectangleF(-cr.Width * 0.25f, -cr.Height * 0.55f, cr.Width * 0.95f, cr.Height * 1.3f), Color.FromArgb(95, 91, 140, 255));
            Glow(g, new RectangleF(cr.Width * 0.45f, cr.Height * 0.35f, cr.Width * 0.9f, cr.Height * 1.1f), Color.FromArgb(80, 139, 92, 246));
            if (card.Width > 0)
            {
                for (int i = 10; i >= 1; i--)
                {
                    using (GraphicsPath sp = Theme.Round(new RectangleF(card.X - i * 2, card.Y - i * 2 + 10, card.Width + i * 4, card.Height + i * 4), 26 + i * 2))
                    using (Brush sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0))) g.FillPath(sb, sp);
                }
                using (GraphicsPath p = Theme.Round(card, 28))
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(222, 20, 29, 66))) g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(64, 255, 255, 255), 1.2f)) g.DrawPath(pen, p);
                }
            }
        }

        static void Glow(Graphics g, RectangleF area, Color c)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(area);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = c;
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, c.R, c.G, c.B) };
                    g.FillPath(pg, gp);
                }
            }
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
        public RoundButton BtnFinish, BtnPause;
        public Controller Ctrl;
        string kind = "office";
        bool online = true;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        public MiniForm(Controller ctrl)
        {
            Ctrl = ctrl;
            Text = "Учёт времени";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = !Program.NoBlock;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(350, 250);
            BackColor = Theme.H("#121A44");
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);
            Region = new Region(Theme.Round(new RectangleF(0, 0, Width, Height), 24));

            lblFio = new Label { Location = new Point(22, 58), Size = new Size(306, 26), Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
            lblTimer = new Label { Location = new Point(18, 84), Size = new Size(312, 70), Font = new Font("Segoe UI Light", 42f), Text = "00:00:00", BackColor = Color.Transparent, ForeColor = Color.White };
            lblStatus = new Label { Location = new Point(40, 156), Size = new Size(290, 24), ForeColor = Theme.Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 10f) };
            BtnPause = new RoundButton { Text = "Пауза", Location = new Point(20, 190), Size = new Size(124, 44), From = Theme.Amber1, To = Theme.Amber2, Fore = Theme.H("#2A1B00"), Radius = 14, Font = new Font("Segoe UI Semibold", 12f) };
            BtnFinish = new RoundButton { Text = "Завершить смену", Location = new Point(154, 190), Size = new Size(176, 44), From = Theme.Red1, To = Theme.Red2, Radius = 14, Font = new Font("Segoe UI Semibold", 11.5f) };
            Controls.AddRange(new Control[] { lblFio, lblTimer, lblStatus, BtnPause, BtnFinish });
        }

        public void SetFio(string fio) { lblFio.Text = fio; }
        public void SetTimer(TimeSpan t) { lblTimer.Text = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); }
        public void SetKind(string k) { kind = k; Invalidate(); }
        public void SetStatus(string s)
        {
            online = s.IndexOf("Нет связи", StringComparison.OrdinalIgnoreCase) < 0;
            lblStatus.Text = s;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Rectangle cr = ClientRectangle;
            using (LinearGradientBrush br = new LinearGradientBrush(cr, Theme.H("#1A2565"), Theme.H("#0B1029"), 110f)) g.FillRectangle(br, cr);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(-120, -140, 340, 300);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    Color c = Theme.KindColor(kind);
                    pg.CenterColor = Color.FromArgb(90, c.R, c.G, c.B);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, c.R, c.G, c.B) };
                    g.FillPath(pg, gp);
                }
            }
            using (GraphicsPath border = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 24))
            using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f)) g.DrawPath(pen, border);
            // значок и название типа смены
            Color kc = Theme.KindColor(kind);
            string name = Theme.KindName(kind);
            using (Font f = new Font("Segoe UI Semibold", 10.5f))
            {
                SizeF sz = g.MeasureString(name, f);
                RectangleF pill = new RectangleF(22, 18, sz.Width + 52, 32);
                using (GraphicsPath pp = Theme.Round(pill, 16))
                using (Brush pb = new SolidBrush(Color.FromArgb(70, kc.R, kc.G, kc.B))) g.FillPath(pb, pp);
                using (GraphicsPath pp2 = Theme.Round(pill, 16))
                using (Pen ppen = new Pen(Color.FromArgb(160, kc.R, kc.G, kc.B), 1.2f)) g.DrawPath(ppen, pp2);
                Theme.Icon(g, kind, new RectangleF(pill.X + 12, pill.Y + 7, 18, 18), Color.White);
                g.DrawString(name, f, Brushes.White, pill.X + 36, pill.Y + 6);
            }
            // индикатор связи
            using (Brush db = new SolidBrush(online ? Theme.Green1 : Theme.Amber1)) g.FillEllipse(db, 24, 162, 10, 10);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < 60) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); }   // перетаскивание за верхнюю часть
        }

        // Во время смены выключить или перезагрузить компьютер нельзя
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0011 && Ctrl.ShiftActive && !Program.NoBlock) // WM_QUERYENDSESSION
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
}
