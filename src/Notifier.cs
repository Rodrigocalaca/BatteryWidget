using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DareuBattery
{
    // Duas vias de aviso, porque uma so nao basta:
    //  1) toast nativo do Windows com scenario "reminder" (fica fixo ate ser dispensado
    //     e permanece na Central de Acoes);
    //  2) um painel proprio sempre-no-topo, que aparece mesmo quando o Assistente de Foco
    //     suprime banners (jogo ou app em tela cheia) - esse e o aviso de prioridade.
    public static class Notifier
    {
        public const string AUMID = "Dareu.A955.BatteryMonitor";
        const string APP_NAME = "Bateria do Mouse Dareu";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        public static void Register(string iconPath)
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(AUMID);
                using (var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\AppUserModelId\" + AUMID))
                {
                    if (k == null) return;
                    k.SetValue("DisplayName", APP_NAME, RegistryValueKind.String);
                    if (!string.IsNullOrEmpty(iconPath)) k.SetValue("IconUri", iconPath, RegistryValueKind.String);
                    k.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static bool Toast(string title, string body, string footer)
        {
            try
            {
                string xml =
                    "<toast scenario='reminder' launch='dareu-battery'>" +
                      "<visual><binding template='ToastGeneric'>" +
                        "<text>" + Escape(title) + "</text>" +
                        "<text>" + Escape(body) + "</text>" +
                        "<text placement='attribution'>" + Escape(footer) + "</text>" +
                      "</binding></visual>" +
                      "<actions>" +
                        "<action content='OK' arguments='dismiss' activationType='background'/>" +
                      "</actions>" +
                      "<audio src='ms-winsoundevent:Notification.Looping.Alarm2' loop='false'/>" +
                    "</toast>";

                var xmlType = Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data, ContentType=WindowsRuntime");
                var toastType = Type.GetType("Windows.UI.Notifications.ToastNotification, Windows.UI, ContentType=WindowsRuntime");
                var mgrType = Type.GetType("Windows.UI.Notifications.ToastNotificationManager, Windows.UI, ContentType=WindowsRuntime");
                if (xmlType == null || toastType == null || mgrType == null) return false;

                var doc = Activator.CreateInstance(xmlType);
                xmlType.GetMethod("LoadXml").Invoke(doc, new object[] { xml });

                var toast = Activator.CreateInstance(toastType, doc);
                var notifier = mgrType.GetMethod("CreateToastNotifier", new[] { typeof(string) })
                                      .Invoke(null, new object[] { AUMID });
                notifier.GetType().GetMethod("Show").Invoke(notifier, new[] { toast });
                return true;
            }
            catch { return false; }
        }

        // Painel sempre-no-topo. Nao rouba foco (WS_EX_NOACTIVATE), entao nao atrapalha jogo.
        public static void Overlay(string title, string body, Color accent, int seconds)
        {
            try
            {
                var f = new OverlayForm(title, body, accent, seconds);
                f.ShowTopMostNoActivate();
            }
            catch { }
        }

        class OverlayForm : Form
        {
            const int WS_EX_TOPMOST = 0x00000008, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
            readonly Timer life = new Timer();

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            public OverlayForm(string title, string body, Color accent, int seconds)
            {
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(24, 24, 27);
                Size = new Size(430, 118);

                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);

                Controls.Add(new Panel { Size = new Size(6, Height), Location = new Point(0, 0), BackColor = accent });

                Controls.Add(new Label
                {
                    Text = title,
                    Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                    ForeColor = Color.White,
                    AutoSize = true,
                    Location = new Point(24, 20)
                });

                Controls.Add(new Label
                {
                    Text = body,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = Color.FromArgb(198, 198, 204),
                    AutoSize = true,
                    MaximumSize = new Size(Width - 48, 0),
                    Location = new Point(24, 54)
                });

                var hint = new Label
                {
                    Text = "clique para dispensar",
                    Font = new Font("Segoe UI", 7.5f),
                    ForeColor = Color.FromArgb(120, 120, 128),
                    AutoSize = true,
                    Location = new Point(24, Height - 24)
                };
                Controls.Add(hint);

                foreach (Control c in Controls) c.Click += (s, e) => Close();
                Click += (s, e) => Close();

                life.Interval = Math.Max(3, seconds) * 1000;
                life.Tick += (s, e) => { life.Stop(); Close(); };
            }

            public void ShowTopMostNoActivate()
            {
                Show();
                life.Start();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                life.Dispose();
                base.OnFormClosed(e);
            }
        }
    }
}
