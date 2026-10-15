using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DareuBattery
{
    public class DeviceState
    {
        public string Name = "";
        public int Percent = -1;
        public bool Charging;
        public bool Stale = true;
        public string Note;          // mensagem curta quando nao ha leitura
    }

    // Painel fixo na area de trabalho com a carga dos dois dispositivos.
    // Layout vertical e estreito: cada dispositivo ocupa um bloco com nome,
    // porcentagem e barra, empilhados.
    public class WidgetForm : Form
    {
        // Medidas do layout - mexer aqui muda o tamanho do painel inteiro.
        const int PAD = 14;          // margem interna
        const int BAR_W = 100;       // largura da barra (define a largura do painel)
        const int BAR_H = 6;
        const int HEADER_Y = 10;
        const int BLOCK_TOP = 30;    // onde comeca o primeiro dispositivo
        const int BLOCK_H = 60;      // altura de cada bloco de dispositivo
        const int NAME_DY = 0;       // deslocamentos dentro do bloco
        const int PCT_DY = 16;
        const int BAR_DY = 44;

        const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_NCHITTEST = 0x0084, HTCAPTION = 2, HTCLIENT = 1;

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        readonly DeviceState[] devices;
        bool alwaysOnTop;

        public event Action PositionChanged;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;  // fora do alt-tab, nao rouba foco
                return cp;
            }
        }

        public WidgetForm(DeviceState[] devices, bool onTop, ContextMenuStrip menu)
        {
            this.devices = devices;
            alwaysOnTop = onTop;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = onTop;
            Size = new Size(PAD * 2 + BAR_W, BLOCK_TOP + BLOCK_H * devices.Length - (BLOCK_H - BAR_DY - BAR_H) + PAD);
            Opacity = 0.93;
            BackColor = Color.FromArgb(18, 18, 22);
            DoubleBuffered = true;
            ContextMenuStrip = menu;

            Paint += OnPaintWidget;
        }

        public void SetAlwaysOnTop(bool v)
        {
            alwaysOnTop = v;
            TopMost = v;
            if (!v) SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, 0x13); // NOSIZE|NOMOVE|NOACTIVATE
        }

        public bool AlwaysOnTop { get { return alwaysOnTop; } }

        // A janela inteira arrasta, exceto com o botao direito (menu).
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && m.Result.ToInt32() == HTCLIENT)
                m.Result = (IntPtr)HTCAPTION;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (PositionChanged != null) PositionChanged();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            using (var path = Rounded(new Rectangle(0, 0, Width, Height), 12))
                Region = new Region(path);
            if (!alwaysOnTop) SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, 0x13);
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color LevelColor(DeviceState d)
        {
            if (d.Percent < 0) return Color.FromArgb(105, 105, 115);
            if (d.Charging) return Color.FromArgb(90, 200, 250);
            if (d.Percent <= 10) return Color.FromArgb(240, 70, 70);
            if (d.Percent <= 20) return Color.FromArgb(245, 170, 50);
            return Color.FromArgb(110, 215, 125);
        }

        void OnPaintWidget(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bg = new SolidBrush(Color.FromArgb(18, 18, 22)))
                g.FillRectangle(bg, ClientRectangle);

            using (var border = new Pen(Color.FromArgb(46, 46, 54)))
            using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 12))
                g.DrawPath(border, path);

            using (var titleFont = new Font("Segoe UI", 7f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(Color.FromArgb(120, 120, 132)))
                g.DrawString("BATERIAS", titleFont, titleBrush, PAD, HEADER_Y);

            using (var nameFont = new Font("Segoe UI", 8f))
            using (var pctFont = new Font("Segoe UI", 14f, FontStyle.Bold))
            using (var markFont = new Font("Segoe UI", 7f))
            using (var nameBrush = new SolidBrush(Color.FromArgb(165, 165, 176)))
            {
                int y = BLOCK_TOP;
                foreach (var d in devices)
                {
                    var color = LevelColor(d);

                    g.DrawString(d.Name, nameFont, nameBrush, PAD, y + NAME_DY);

                    string label = d.Percent < 0 ? "--" : d.Percent + "%";
                    if (d.Charging) label = "+" + label;
                    using (var pb = new SolidBrush(color))
                        g.DrawString(label, pctFont, pb, PAD - 2, y + PCT_DY);

                    // asterisco discreto quando o valor e o ultimo conhecido, nao uma leitura fresca
                    if (d.Percent >= 0 && d.Stale)
                        using (var sb = new SolidBrush(Color.FromArgb(120, 120, 132)))
                            g.DrawString("*", markFont, sb, PAD + BAR_W - 8, y + PCT_DY + 2);

                    var track = new Rectangle(PAD, y + BAR_DY, BAR_W, BAR_H);
                    using (var tb = new SolidBrush(Color.FromArgb(42, 42, 50)))
                    using (var tp = Rounded(track, BAR_H / 2))
                        g.FillPath(tb, tp);

                    if (d.Percent >= 0)
                    {
                        int w = Math.Max(BAR_H, (int)Math.Round(track.Width * d.Percent / 100.0));
                        using (var fb = new SolidBrush(color))
                        using (var fp = Rounded(new Rectangle(track.X, track.Y, w, track.Height), BAR_H / 2))
                            g.FillPath(fb, fp);
                    }

                    y += BLOCK_H;
                }
            }
        }
    }
}
