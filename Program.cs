using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClassClock
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ClockForm());
        }
    }

    class ClockForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        Label clockLabel;
        Label cdLabel;
        TableLayoutPanel table;
        System.Windows.Forms.Timer uiTimer;

        DateTime cdEnd;
        TimeSpan pauseRemain;
        bool cdActive = false;
        bool cdPaused = false;
        bool finished = false;
        DateTime finishedAt;
        int flashCounter = 0;
        int topmostCounter = 0;
        bool modalOpen = false;

        float scale = 1.0f;
        double opacityVal = 0.92;
        Color fontColor = Color.White;
        Color bgColorCur = Color.FromArgb(20, 22, 30);
        string settingsPath;
        bool hasSavedPos = false;
        Point savedPos;

        public ClockForm()
        {
            settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "课堂时钟设置.txt");
            LoadSettings();

            Text = "课堂时钟";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = true;
            Opacity = opacityVal;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            table = new TableLayoutPanel();
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.ColumnCount = 1;
            table.RowCount = 2;
            table.Padding = new Padding(12, 6, 12, 6);
            Controls.Add(table);

            clockLabel = new Label();
            clockLabel.AutoSize = true;
            clockLabel.Anchor = AnchorStyles.None;
            clockLabel.TextAlign = ContentAlignment.MiddleCenter;
            table.Controls.Add(clockLabel, 0, 0);

            cdLabel = new Label();
            cdLabel.AutoSize = true;
            cdLabel.Anchor = AnchorStyles.None;
            cdLabel.TextAlign = ContentAlignment.MiddleCenter;
            cdLabel.Visible = false;
            cdLabel.Cursor = Cursors.Hand;
            cdLabel.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePause();
            };
            table.Controls.Add(cdLabel, 0, 1);

            BuildMenu();

            HookDrag(this);
            HookDrag(table);
            HookDrag(clockLabel);

            ApplyColors();
            ApplyFonts();

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 200;
            uiTimer.Tick += delegate { Tick(); };
            uiTimer.Start();

            Shown += delegate { PlaceWindow(); };
            FormClosing += delegate { SaveSettings(); };
            Tick();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            IntPtr hrgn = IntPtr.Zero;
            try
            {
                hrgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 14, 14);
                if (hrgn == IntPtr.Zero) return;
                Region old = Region;
                Region = Region.FromHrgn(hrgn);
                if (old != null) old.Dispose();
            }
            catch { }
            finally
            {
                // Region.FromHrgn 会复制区域数据，原始 HRGN 必须自己删掉，否则每次改变尺寸泄漏一个 GDI 对象
                if (hrgn != IntPtr.Zero) DeleteObject(hrgn);
            }
        }

        void ReassertTopmost()
        {
            // TopMost 只在创建时生效一次。PPT / WPS 开始放映时它的窗口被激活，会插到本窗口之上，
            // 所以要定时把自己重新压到最前；SWP_NOACTIVATE 保证不抢走放映窗口的焦点。
            if (modalOpen || !IsHandleCreated) return;
            try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { }
        }

        void HookDrag(Control c)
        {
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
            };
        }

        void BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();

            ToolStripMenuItem cdMenu = new ToolStripMenuItem("开始倒计时");
            int[] presets = new int[] { 3, 5, 10, 15, 20, 30, 45, 60 };
            foreach (int p in presets)
            {
                int mm = p;
                cdMenu.DropDownItems.Add(p + " 分钟", null, delegate { StartCountdown(TimeSpan.FromMinutes(mm)); });
            }
            cdMenu.DropDownItems.Add("自定义…", null, delegate { AskCustom(); });
            m.Items.Add(cdMenu);
            m.Items.Add("暂停 / 继续", null, delegate { TogglePause(); });
            m.Items.Add("停止倒计时", null, delegate { StopCountdown(); });
            m.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem sizeMenu = new ToolStripMenuItem("窗口大小");
            AddSize(sizeMenu, "特小", 0.7f);
            AddSize(sizeMenu, "小", 0.85f);
            AddSize(sizeMenu, "中（默认）", 1.0f);
            AddSize(sizeMenu, "大", 1.4f);
            AddSize(sizeMenu, "特大", 1.9f);
            m.Items.Add(sizeMenu);

            ToolStripMenuItem opMenu = new ToolStripMenuItem("透明度");
            AddOpacity(opMenu, "完全不透明", 1.0);
            AddOpacity(opMenu, "90%", 0.9);
            AddOpacity(opMenu, "80%", 0.8);
            AddOpacity(opMenu, "70%", 0.7);
            AddOpacity(opMenu, "60%", 0.6);
            AddOpacity(opMenu, "50%", 0.5);
            AddOpacity(opMenu, "20%", 0.2);
            AddOpacity(opMenu, "10%", 0.1);
            m.Items.Add(opMenu);

            ToolStripMenuItem fontColorMenu = new ToolStripMenuItem("字体颜色");
            AddFontColor(fontColorMenu, "白色", Color.White);
            AddFontColor(fontColorMenu, "黄色", Color.FromArgb(255, 220, 80));
            AddFontColor(fontColorMenu, "橙色", Color.FromArgb(255, 160, 60));
            AddFontColor(fontColorMenu, "浅绿", Color.FromArgb(120, 230, 130));
            AddFontColor(fontColorMenu, "天蓝", Color.FromArgb(110, 190, 255));
            AddFontColor(fontColorMenu, "粉色", Color.FromArgb(255, 150, 190));
            AddFontColor(fontColorMenu, "红色", Color.FromArgb(255, 80, 70));
            AddFontColor(fontColorMenu, "黑色", Color.Black);
            fontColorMenu.DropDownItems.Add("自定义…", null, delegate { PickFontColor(); });
            m.Items.Add(fontColorMenu);

            ToolStripMenuItem bgMenu = new ToolStripMenuItem("背景颜色");
            AddBgColor(bgMenu, "深蓝灰（默认）", Color.FromArgb(20, 22, 30));
            AddBgColor(bgMenu, "纯黑", Color.Black);
            AddBgColor(bgMenu, "白色", Color.White);
            bgMenu.DropDownItems.Add("自定义…", null, delegate { PickBgColor(); });
            m.Items.Add(bgMenu);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("退出", null, delegate { Close(); });

            ContextMenuStrip = m;
            table.ContextMenuStrip = m;
            clockLabel.ContextMenuStrip = m;
            cdLabel.ContextMenuStrip = m;
        }

        void AddSize(ToolStripMenuItem parent, string name, float s)
        {
            float v = s;
            parent.DropDownItems.Add(name, null, delegate { scale = v; ApplyFonts(); });
        }

        void AddOpacity(ToolStripMenuItem parent, string name, double o)
        {
            double v = o;
            parent.DropDownItems.Add(name, null, delegate { Opacity = v; });
        }

        void AddFontColor(ToolStripMenuItem parent, string name, Color c)
        {
            Color v = c;
            parent.DropDownItems.Add(name, null, delegate { fontColor = v; ApplyColors(); });
        }

        void AddBgColor(ToolStripMenuItem parent, string name, Color c)
        {
            Color v = c;
            parent.DropDownItems.Add(name, null, delegate { bgColorCur = v; ApplyColors(); });
        }

        void PickFontColor()
        {
            modalOpen = true;
            try
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.Color = fontColor;
                    cd.FullOpen = true;
                    if (cd.ShowDialog(this) == DialogResult.OK) { fontColor = cd.Color; ApplyColors(); }
                }
            }
            finally { modalOpen = false; }
        }

        void PickBgColor()
        {
            modalOpen = true;
            try
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.Color = bgColorCur;
                    cd.FullOpen = true;
                    if (cd.ShowDialog(this) == DialogResult.OK) { bgColorCur = cd.Color; ApplyColors(); }
                }
            }
            finally { modalOpen = false; }
        }

        void ApplyColors()
        {
            BackColor = bgColorCur;
            clockLabel.ForeColor = fontColor;
            cdLabel.ForeColor = fontColor;
        }

        void ApplyFonts()
        {
            clockLabel.Font = new Font("Consolas", (cdActive ? 10f : 20f) * scale, FontStyle.Bold);
            cdLabel.Font = new Font("Consolas", 28f * scale, FontStyle.Bold);
        }

        void StartCountdown(TimeSpan ts)
        {
            cdActive = true;
            cdPaused = false;
            finished = false;
            cdEnd = DateTime.Now + ts;
            cdLabel.Visible = true;
            cdLabel.Text = FormatSpan(ts);
            ApplyColors();
            ApplyFonts();
        }

        void StopCountdown()
        {
            cdActive = false;
            cdPaused = false;
            finished = false;
            cdLabel.Visible = false;
            ApplyColors();
            ApplyFonts();
        }

        void TogglePause()
        {
            if (!cdActive) return;
            if (finished) { StopCountdown(); return; }
            if (cdPaused)
            {
                cdEnd = DateTime.Now + pauseRemain;
                cdPaused = false;
            }
            else
            {
                pauseRemain = cdEnd - DateTime.Now;
                cdPaused = true;
            }
        }

        void Finish()
        {
            finished = true;
            finishedAt = DateTime.Now;
            flashCounter = 0;
            cdLabel.Text = "时间到";
            new Thread(BeepAlarm) { IsBackground = true }.Start();
        }

        static void BeepAlarm()
        {
            try
            {
                for (int i = 0; i < 3; i++) { Console.Beep(1200, 400); Thread.Sleep(180); }
            }
            catch { }
        }

        void Tick()
        {
            clockLabel.Text = DateTime.Now.ToString("HH:mm");

            topmostCounter++;
            if (topmostCounter >= 5) { topmostCounter = 0; ReassertTopmost(); }

            if (finished)
            {
                flashCounter++;
                Color alarm = Color.FromArgb(255, 80, 70);
                Color other = ColorClose(fontColor, alarm) ? bgColorCur : fontColor;
                cdLabel.ForeColor = (flashCounter / 2) % 2 == 0 ? alarm : other;
                if ((DateTime.Now - finishedAt).TotalSeconds > 20) StopCountdown();
                return;
            }
            if (!cdActive) return;
            TimeSpan remain = cdPaused ? pauseRemain : (cdEnd - DateTime.Now);
            if (remain.TotalSeconds <= 0) { Finish(); return; }
            cdLabel.Text = FormatSpan(remain);
            cdLabel.ForeColor = cdPaused ? Color.Gray : fontColor;
        }

        static bool ColorClose(Color a, Color b)
        {
            // 字体色本身就是报警红时，红↔字色的闪烁看不出来，改用背景色作为另一相
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 90;
        }

        static string FormatSpan(TimeSpan ts)
        {
            int total = (int)Math.Ceiling(ts.TotalSeconds);
            if (total < 0) total = 0;
            int h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            if (h > 0) return string.Format("{0}:{1:D2}:{2:D2}", h, m, s);
            return string.Format("{0:D2}:{1:D2}", m, s);
        }

        void AskCustom()
        {
            modalOpen = true;
            try
            {
                using (CustomForm f = new CustomForm())
                {
                    f.TopMost = true;
                    if (f.ShowDialog(this) == DialogResult.OK) StartCountdown(f.Result);
                }
            }
            finally { modalOpen = false; }
        }

        void PlaceWindow()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point p = hasSavedPos ? savedPos : new Point(wa.Right - Width - 24, wa.Top + 24);
            bool visible = false;
            foreach (Screen s in Screen.AllScreens)
                if (s.Bounds.IntersectsWith(new Rectangle(p, Size))) visible = true;
            if (!visible) p = new Point(wa.Right - Width - 24, wa.Top + 24);
            Location = p;
        }

        void LoadSettings()
        {
            string[] lines;
            try
            {
                if (!File.Exists(settingsPath)) return;
                lines = File.ReadAllLines(settingsPath);
            }
            catch { return; }

            // 逐行 TryParse：某一行坏掉只丢那一项，不能把后面的设置一起吞掉
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                int iv;
                float fv;
                double dv;
                if (k == "x") { if (int.TryParse(v, out iv)) { savedPos.X = iv; hasSavedPos = true; } }
                else if (k == "y") { if (int.TryParse(v, out iv)) savedPos.Y = iv; }
                else if (k == "scale")
                { if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out fv)) scale = fv; }
                else if (k == "opacity")
                { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) opacityVal = dv; }
                else if (k == "fontcolor") fontColor = ParseColor(v, fontColor);
                else if (k == "bgcolor") bgColorCur = ParseColor(v, bgColorCur);
            }

            if (scale < 0.4f || scale > 3f) scale = 1f;
            if (opacityVal < 0.05 || opacityVal > 1) opacityVal = 0.92;
            // Form.BackColor 不接受半透明色，会抛异常，文件被改坏时这里兜住
            if (bgColorCur.A != 255) bgColorCur = Color.FromArgb(255, bgColorCur);
            if (fontColor.A != 255) fontColor = Color.FromArgb(255, fontColor);
        }

        static Color ParseColor(string hex, Color fallback)
        {
            int argb;
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb))
                return Color.FromArgb(argb);
            return fallback;
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllLines(settingsPath, new string[]
                {
                    "x=" + Location.X,
                    "y=" + Location.Y,
                    "scale=" + scale.ToString(CultureInfo.InvariantCulture),
                    "opacity=" + Opacity.ToString(CultureInfo.InvariantCulture),
                    "fontcolor=" + fontColor.ToArgb().ToString("X8"),
                    "bgcolor=" + bgColorCur.ToArgb().ToString("X8")
                });
            }
            catch { }
        }
    }

    class CustomForm : Form
    {
        public TimeSpan Result;
        TextBox box;
        Label hint;

        public CustomForm()
        {
            Text = "自定义倒计时";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(270, 118);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label tip = new Label();
            tip.Text = "输入分钟数（如 8），或 分:秒（如 3:30）";
            tip.SetBounds(12, 10, 250, 20);
            Controls.Add(tip);

            box = new TextBox();
            box.SetBounds(12, 34, 246, 26);
            Controls.Add(box);

            hint = new Label();
            hint.ForeColor = Color.Firebrick;
            hint.SetBounds(12, 62, 250, 18);
            Controls.Add(hint);

            Button ok = new Button();
            ok.Text = "开始";
            ok.SetBounds(96, 84, 78, 28);
            ok.Click += OkClick;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(182, 84, 78, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        void OkClick(object s, EventArgs e)
        {
            TimeSpan ts;
            if (TryParseSpan(box.Text, out ts))
            {
                Result = ts;
                DialogResult = DialogResult.OK;
            }
            else
            {
                hint.Text = "没看懂这个时间，请重新输入";
            }
        }

        static bool TryParseSpan(string s, out TimeSpan ts)
        {
            ts = TimeSpan.Zero;
            if (s == null) return false;
            s = s.Trim().Replace("：", ":");
            if (s.Contains(":"))
            {
                string[] parts = s.Split(':');
                int m, sec;
                if (parts.Length == 2 && int.TryParse(parts[0], out m) && int.TryParse(parts[1], out sec)
                    && m >= 0 && sec >= 0 && sec < 60 && (m > 0 || sec > 0))
                {
                    ts = new TimeSpan(0, m, sec);
                    return true;
                }
                return false;
            }
            double mins;
            NumberStyles ns = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
                            | NumberStyles.AllowDecimalPoint;
            if (double.TryParse(s, ns, CultureInfo.InvariantCulture, out mins) && mins > 0 && mins <= 600)
            {
                TimeSpan cand = TimeSpan.FromMinutes(mins);
                // 不足 1 秒的一律当无效输入，让对话框给出提示而不是静默无反应
                if (cand.TotalSeconds >= 1) { ts = cand; return true; }
            }
            return false;
        }
    }
}
