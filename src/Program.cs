using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DareuBattery
{
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DareuBattery");

        const string TASK_NAME = "Dareu Battery Monitor";

        [STAThread]
        static int Main(string[] args)
        {
            Directory.CreateDirectory(DataDir);
            string mode = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";

            if (mode == "once" || mode == "status") return RunOnce();
            if (mode == "install") return Install();
            if (mode == "uninstall") return Uninstall();
            if (mode == "test") return RunTestAlert();

            bool isNew;
            using (var mutex = new Mutex(true, @"Local\DareuBatteryMonitor", out isNew))
            {
                if (!isNew) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MonitorContext());
            }
            return 0;
        }

        // Em um executavel WinExe o stdout so funciona se reconectarmos apos AttachConsole.
        static void Say(string msg)
        {
            try { Console.WriteLine(msg); } catch { }
            try { File.AppendAllText(Path.Combine(DataDir, "last.txt"), msg + Environment.NewLine); } catch { }
        }

        static int RunOnce()
        {
            if (AttachConsole(-1))
            {
                try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); }
                catch { }
            }
            try { File.WriteAllText(Path.Combine(DataDir, "last.txt"), ""); } catch { }

            var cfg = Config.Load();

            using (var hid = new DareuHid())
            {
                if (!hid.Open()) Say("Mouse Dareu: dongle nao encontrado.");
                else
                {
                    bool cable = DareuHid.CablePresent(hid.DevicePath);
                    int link = hid.ReadLinkState();
                    int pct = hid.ReadPercent();
                    string estado = cable ? "carregando no cabo"
                                  : (link == 1 ? "na bateria" : "na bateria, mouse dormindo");
                    Say(pct < 0
                        ? "Mouse Dareu: sem resposta (" + estado + ")."
                        : string.Format("Mouse Dareu: {0}%  ({1})", pct, estado));
                }
            }

            using (var kb = new GamakayHid(cfg.KeyboardVid, cfg.KeyboardReportLen, cfg.KeyboardPercentByte))
            {
                if (!kb.Open()) Say("Teclado GAMAKAY: dongle nao encontrado.");
                else
                {
                    bool online;
                    int pct = kb.ReadPercent(out online);
                    Say(pct < 0
                        ? "Teclado GAMAKAY: dongle ainda nao publicou status."
                        : string.Format("Teclado GAMAKAY: {0}%  ({1})", pct, online ? "conectado" : "offline"));
                }
            }
            return 0;
        }

        static int RunTestAlert()
        {
            Application.EnableVisualStyles();
            Notifier.Register(IconPath());
            Notifier.Toast("Bateria baixa", "Mouse Dareu - 15% restante. Conecte o cabo USB.", "teste do monitor");
            Notifier.Overlay("Bateria baixa", "Mouse Dareu - 15% restante. Conecte o cabo USB.",
                             Color.FromArgb(232, 62, 62), 10);
            var quit = new System.Windows.Forms.Timer { Interval = 13000 };
            quit.Tick += (s, e) => { quit.Stop(); Application.ExitThread(); };
            quit.Start();
            Application.Run();
            return 0;
        }

        public static string IconPath()
        {
            string p = @"C:\Program Files (x86)\Dareu\A955 Wireless Mouse\appico.ico";
            return File.Exists(p) ? p : null;
        }

        static int Install()
        {
            AttachConsole(-1);
            try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); } catch { }

            string exe = Application.ExecutablePath;
            string target = Path.Combine(DataDir, "DareuBattery.exe");
            if (!string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(exe, target, true); }
                catch (Exception e) { Console.WriteLine("Falha ao copiar: " + e.Message); return 1; }
            }

            Notifier.Register(IconPath());

            var psi = new ProcessStartInfo("schtasks.exe",
                string.Format("/Create /TN \"{0}\" /TR \"\\\"{1}\\\"\" /SC ONLOGON /RL LIMITED /F", TASK_NAME, target))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode != 0) Console.WriteLine("schtasks: " + p.StandardError.ReadToEnd());
            }

            Console.WriteLine("Instalado em: " + target);
            Console.WriteLine("Config: " + Config.Path);
            return 0;
        }

        static int Uninstall()
        {
            AttachConsole(-1);
            var psi = new ProcessStartInfo("schtasks.exe", string.Format("/Delete /TN \"{0}\" /F", TASK_NAME))
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) p.WaitForExit();
            Console.WriteLine("Tarefa agendada removida. Pasta: " + DataDir);
            return 0;
        }

        public static void Log(string msg)
        {
            try
            {
                string f = Path.Combine(DataDir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        public static Icon MakeTrayIcon(int pct, bool charging, bool stale)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color fg = pct < 0 ? Color.FromArgb(150, 150, 155)
                         : charging ? Color.FromArgb(90, 200, 250)
                         : pct <= 10 ? Color.FromArgb(240, 70, 70)
                         : pct <= 20 ? Color.FromArgb(245, 170, 50)
                         : Color.FromArgb(120, 220, 120);

                string text = pct < 0 ? "--" : (pct >= 100 ? "99" : pct.ToString(CultureInfo.InvariantCulture));
                using (var font = new Font("Segoe UI", text.Length > 2 ? 15f : 19f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(fg))
                {
                    var size = g.MeasureString(text, font);
                    g.DrawString(text, font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
                }

                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    class Config
    {
        public static string Path { get { return System.IO.Path.Combine(Program.DataDir, "config.ini"); } }

        public int IntervalMinutes = 5;
        public int[] Thresholds = { 20, 10, 5 };
        public int OverlaySeconds = 12;
        public int RepeatMinutes = 30;
        public bool Toast = true;
        public bool Overlay = true;

        public bool Widget = true;
        public bool WidgetTopMost = true;    // visivel por cima das janelas; desligue para deixa-lo no nivel da area de trabalho
        public int WidgetX = -1, WidgetY = -1;

        public bool Keyboard = true;
        public ushort KeyboardVid = 0x3151;
        public int KeyboardReportLen = 65;
        public int KeyboardPercentByte = 2;

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(Path)) { c.Save(); return c; }
                foreach (var raw in File.ReadAllLines(Path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "intervalminutes": c.IntervalMinutes = Math.Max(1, ParseInt(v, 5)); break;
                        case "overlayseconds": c.OverlaySeconds = Math.Max(3, ParseInt(v, 12)); break;
                        case "repeatminutes": c.RepeatMinutes = Math.Max(0, ParseInt(v, 30)); break;
                        case "toast": c.Toast = v != "0"; break;
                        case "overlay": c.Overlay = v != "0"; break;
                        case "widget": c.Widget = v != "0"; break;
                        case "widgettopmost": c.WidgetTopMost = v != "0"; break;
                        case "widgetx": c.WidgetX = ParseInt(v, -1); break;
                        case "widgety": c.WidgetY = ParseInt(v, -1); break;
                        case "keyboard": c.Keyboard = v != "0"; break;
                        case "keyboardvid": c.KeyboardVid = (ushort)ParseHex(v, 0x3151); break;
                        case "keyboardreportlen": c.KeyboardReportLen = ParseInt(v, 65); break;
                        case "keyboardpercentbyte": c.KeyboardPercentByte = ParseInt(v, 2); break;
                        case "thresholds":
                            var list = v.Split(',').Select(x => ParseInt(x.Trim(), -1)).Where(x => x > 0 && x <= 100)
                                        .Distinct().OrderByDescending(x => x).ToArray();
                            if (list.Length > 0) c.Thresholds = list;
                            break;
                    }
                }
            }
            catch { }
            return c;
        }

        static int ParseInt(string s, int def)
        {
            int n;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def;
        }

        static int ParseHex(string s, int def)
        {
            int n;
            return int.TryParse(s.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) ? n : def;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Monitor de bateria - mouse Dareu + teclado GAMAKAY");
                sb.AppendLine("IntervalMinutes=" + IntervalMinutes);
                sb.AppendLine("# Niveis que disparam aviso, do maior para o menor");
                sb.AppendLine("Thresholds=" + string.Join(",", Thresholds.Select(x => x.ToString()).ToArray()));
                sb.AppendLine("RepeatMinutes=" + RepeatMinutes);
                sb.AppendLine("OverlaySeconds=" + OverlaySeconds);
                sb.AppendLine("Toast=" + (Toast ? 1 : 0));
                sb.AppendLine("Overlay=" + (Overlay ? 1 : 0));
                sb.AppendLine();
                sb.AppendLine("# Painel fixo na area de trabalho");
                sb.AppendLine("Widget=" + (Widget ? 1 : 0));
                sb.AppendLine("# 1 = fica por cima de tudo (inclusive jogo em tela cheia)");
                sb.AppendLine("WidgetTopMost=" + (WidgetTopMost ? 1 : 0));
                sb.AppendLine("WidgetX=" + WidgetX);
                sb.AppendLine("WidgetY=" + WidgetY);
                sb.AppendLine();
                sb.AppendLine("# Teclado (leitura apenas; nada e escrito no dispositivo)");
                sb.AppendLine("Keyboard=" + (Keyboard ? 1 : 0));
                sb.AppendLine("KeyboardVid=" + KeyboardVid.ToString("X4"));
                sb.AppendLine("KeyboardReportLen=" + KeyboardReportLen);
                sb.AppendLine("KeyboardPercentByte=" + KeyboardPercentByte);
                File.WriteAllText(Path, sb.ToString());
            }
            catch { }
        }
    }

    class AlertLatch
    {
        public int Level = int.MaxValue;
        public DateTime LastAlert = DateTime.MinValue;
    }

    class MonitorContext : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly DareuHid mouse = new DareuHid();
        GamakayHid keyboard;
        Config cfg = Config.Load();

        readonly DeviceState mouseState = new DeviceState { Name = "Mouse" };
        readonly DeviceState kbState = new DeviceState { Name = "Teclado" };
        readonly Dictionary<DeviceState, AlertLatch> latches = new Dictionary<DeviceState, AlertLatch>();

        WidgetForm widget;
        ToolStripMenuItem miWidget, miTop;

        public MonitorContext()
        {
            Notifier.Register(Program.IconPath());
            latches[mouseState] = new AlertLatch();
            latches[kbState] = new AlertLatch();
            keyboard = new GamakayHid(cfg.KeyboardVid, cfg.KeyboardReportLen, cfg.KeyboardPercentByte);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Verificar agora", null, (s, e) => Poll(true));

            miWidget = new ToolStripMenuItem("Mostrar painel", null, (s, e) => ToggleWidget()) { Checked = cfg.Widget };
            menu.Items.Add(miWidget);
            miTop = new ToolStripMenuItem("Painel sempre no topo", null, (s, e) => ToggleTopMost()) { Checked = cfg.WidgetTopMost };
            menu.Items.Add(miTop);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Testar aviso", null, (s, e) => Alert(mouseState, 15, true));
            menu.Items.Add("Abrir configuracao", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Config.Path) { UseShellExecute = true }); } catch { }
            });
            menu.Items.Add("Recarregar configuracao", null, (s, e) => Reload());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitThread());

            tray.ContextMenuStrip = menu;
            tray.Icon = Program.MakeTrayIcon(-1, false, true);
            tray.Text = "Baterias: consultando...";
            tray.Visible = true;
            tray.DoubleClick += (s, e) => Poll(true);

            if (cfg.Widget) ShowWidget();

            timer.Interval = cfg.IntervalMinutes * 60 * 1000;
            timer.Tick += (s, e) => Poll(false);
            timer.Start();

            Program.Log("monitor iniciado");
            Poll(false);
        }

        void Reload()
        {
            cfg = Config.Load();
            timer.Interval = cfg.IntervalMinutes * 60 * 1000;
            keyboard.Dispose();
            keyboard = new GamakayHid(cfg.KeyboardVid, cfg.KeyboardReportLen, cfg.KeyboardPercentByte);
            Program.Log("configuracao recarregada");
            Poll(true);
        }

        void ShowWidget()
        {
            if (widget != null && !widget.IsDisposed) return;
            widget = new WidgetForm(new[] { mouseState, kbState }, cfg.WidgetTopMost, tray.ContextMenuStrip);

            var wa = Screen.PrimaryScreen.WorkingArea;
            int x = cfg.WidgetX >= 0 ? cfg.WidgetX : wa.Right - widget.Width - 24;
            int y = cfg.WidgetY >= 0 ? cfg.WidgetY : wa.Bottom - widget.Height - 24;
            // garante que a posicao salva ainda cabe em algum monitor
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(x, y, widget.Width, widget.Height))))
            {
                x = wa.Right - widget.Width - 24;
                y = wa.Bottom - widget.Height - 24;
            }
            widget.Location = new Point(x, y);
            widget.PositionChanged += SaveWidgetPosition;
            widget.Show();
        }

        void SaveWidgetPosition()
        {
            if (widget == null || widget.IsDisposed) return;
            if (widget.Location.X == cfg.WidgetX && widget.Location.Y == cfg.WidgetY) return;
            cfg.WidgetX = widget.Location.X;
            cfg.WidgetY = widget.Location.Y;
            cfg.Save();
        }

        void ToggleWidget()
        {
            cfg.Widget = !cfg.Widget;
            miWidget.Checked = cfg.Widget;
            if (cfg.Widget) { ShowWidget(); RefreshWidget(); }
            else if (widget != null) { widget.Close(); widget.Dispose(); widget = null; }
            cfg.Save();
        }

        void ToggleTopMost()
        {
            cfg.WidgetTopMost = !cfg.WidgetTopMost;
            miTop.Checked = cfg.WidgetTopMost;
            if (widget != null && !widget.IsDisposed) widget.SetAlwaysOnTop(cfg.WidgetTopMost);
            cfg.Save();
        }

        void RefreshWidget()
        {
            if (widget != null && !widget.IsDisposed) widget.Invalidate();
        }

        void Poll(bool manual)
        {
            PollMouse(manual);
            if (cfg.Keyboard) PollKeyboard();
            else { kbState.Percent = -1; kbState.Note = "desativado"; }

            UpdateTray();
            RefreshWidget();
        }

        void PollMouse(bool manual)
        {
            try
            {
                if (!mouse.IsOpen && !mouse.Open())
                {
                    mouseState.Stale = true;
                    mouseState.Note = "dongle ausente";
                    if (mouseState.Percent < 0 && manual)
                        Notifier.Overlay("Mouse Dareu", "Dongle nao encontrado.", Color.Gray, 6);
                    return;
                }

                // carga vem da presenca do cabo, nao do estado do enlace:
                // o byte do enlace zera tambem quando o mouse dorme, o que dava carga falsa
                bool cable = DareuHid.CablePresent(mouse.DevicePath);
                int link = mouse.ReadLinkState();
                int pct = mouse.ReadPercent();
                mouseState.Charging = cable;

                if (pct < 0)
                {
                    mouse.Close();
                    mouseState.Stale = true;
                    mouseState.Note = cable ? "no cabo" : "mouse dormindo";
                    if (manual) Notifier.Overlay("Mouse Dareu", "Sem resposta agora. Mexa no mouse e tente de novo.", Color.Gray, 6);
                    return;
                }

                bool asleep = link == 0 && !cable;
                string estado = cable ? "carregando" : (asleep ? "na bateria (dormindo)" : "na bateria");
                if (pct != mouseState.Percent || cable != mouseState.Charging || asleep != mouseState.Stale)
                    Program.Log(string.Format("mouse {0}%  {1}", pct, estado));

                mouseState.Percent = pct;
                mouseState.Stale = asleep;
                mouseState.Note = asleep ? "mouse dormindo" : null;
                EvaluateAlert(mouseState);
            }
            catch (Exception ex)
            {
                Program.Log("erro no mouse: " + ex.Message);
                mouse.Close();
                mouseState.Stale = true;
            }
        }

        void PollKeyboard()
        {
            try
            {
                if (!keyboard.IsOpen && !keyboard.Open())
                {
                    kbState.Stale = true;
                    kbState.Note = "dongle ausente";
                    return;
                }

                bool online;
                int pct = keyboard.ReadPercent(out online);
                if (pct < 0)
                {
                    kbState.Stale = true;
                    kbState.Note = "sem status";
                    return;
                }

                if (pct != kbState.Percent || kbState.Stale)
                    Program.Log(string.Format("teclado {0}%  {1}", pct, online ? "conectado" : "offline"));

                kbState.Percent = pct;
                kbState.Charging = false;
                kbState.Stale = false;
                kbState.Note = null;
                EvaluateAlert(kbState);
            }
            catch (Exception ex)
            {
                Program.Log("erro no teclado: " + ex.Message);
                keyboard.Close();
                kbState.Stale = true;
            }
        }

        void EvaluateAlert(DeviceState d)
        {
            var latch = latches[d];
            if (d.Charging) { latch.Level = int.MaxValue; return; }

            int level = CurrentLevel(d.Percent);
            if (level == 0) { latch.Level = int.MaxValue; return; }

            bool deeper = level < latch.Level;
            bool repeat = cfg.RepeatMinutes > 0 && level == latch.Level
                          && (DateTime.Now - latch.LastAlert).TotalMinutes >= cfg.RepeatMinutes;

            if (deeper || repeat)
            {
                latch.Level = level;
                latch.LastAlert = DateTime.Now;
                Alert(d, d.Percent, false);
            }
        }

        // Menor limiar que a carga atual ja cruzou (0 = nenhum).
        int CurrentLevel(int pct)
        {
            int best = 0;
            foreach (int t in cfg.Thresholds)
                if (pct <= t && (best == 0 || t < best)) best = t;
            return best;
        }

        void Alert(DeviceState d, int pct, bool isTest)
        {
            string titulo = pct <= 10 ? "Bateria CRITICA" : "Bateria baixa";
            string corpo = string.Format("{0} com {1}% de carga. Hora de carregar.",
                d == kbState ? "Teclado GAMAKAY" : "Mouse Dareu", pct);
            Color cor = pct <= 10 ? Color.FromArgb(232, 62, 62) : Color.FromArgb(240, 170, 50);

            Program.Log((isTest ? "[teste] " : "") + "aviso: " + d.Name + " em " + pct + "%");
            if (cfg.Toast) Notifier.Toast(titulo, corpo, isTest ? "teste" : "monitor de bateria");
            if (cfg.Overlay) Notifier.Overlay(titulo, corpo, cor, cfg.OverlaySeconds);
            try { Console.Beep(880, 180); } catch { }
        }

        void UpdateTray()
        {
            // o icone mostra o dispositivo mais critico
            var worst = new[] { mouseState, kbState }
                        .Where(d => d.Percent >= 0)
                        .OrderBy(d => d.Percent)
                        .FirstOrDefault();

            var old = tray.Icon;
            tray.Icon = worst == null
                ? Program.MakeTrayIcon(-1, false, true)
                : Program.MakeTrayIcon(worst.Percent, worst.Charging, worst.Stale);
            if (old != null) old.Dispose();

            string txt = Describe(mouseState) + Environment.NewLine + Describe(kbState);
            if (txt.Length > 127) txt = txt.Substring(0, 127);
            tray.Text = txt;
        }

        static string Describe(DeviceState d)
        {
            if (d.Percent < 0) return d.Name + ": " + (d.Note ?? "sem leitura");
            string s = d.Name + ": " + d.Percent + "%";
            if (d.Charging) s += " (carregando)";
            else if (d.Stale) s += " (" + (d.Note ?? "ultimo valor") + ")";
            return s;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                if (widget != null && !widget.IsDisposed) { widget.Close(); widget.Dispose(); }
                tray.Visible = false;
                tray.Dispose();
                mouse.Dispose();
                if (keyboard != null) keyboard.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
