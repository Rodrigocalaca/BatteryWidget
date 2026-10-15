using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DareuBattery
{
    // Leitura de bateria do mouse Dareu via protocolo HID proprietario.
    //
    //   comando  (feature report, usage page FF02):
    //       08 <cmd> 00 00 00 00 ...zeros...  <chk>
    //   resposta (input report,  usage page FF01):
    //       09 <cmd> <status> <addrHi> <addrLo> <len> <dados...> <chk>
    //
    // Em ambos, a soma dos 17 bytes modulo 256 vale 0x55.
    // status 0 = ok, 1 = indisponivel.  O campo <len> vai ZERADO na requisicao:
    // quem preenche o comprimento e o dispositivo, na resposta.
    public class DareuHid : IDisposable
    {
        public const ushort VID = 0x260D;
        public const byte CMD_BATTERY = 0x04;   // resposta: len 2, dado[0] = porcentagem
        public const byte CMD_LINK = 0x03;      // resposta: len 1, dado[0] = 1 ativo no 2.4G / 0 fora do radio

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int REPORT_LEN = 17;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid guid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string DevicePath; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NIB, NIV, NID, NOB, NOV, NOD, NFB, NFV, NFD;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA a);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA a, ref SP_DEVICE_INTERFACE_DETAIL_DATA d, int sz, ref int req, IntPtr i);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint cr, uint fl, IntPtr t);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        IntPtr cmdHandle = IntPtr.Zero;
        FileStream replyStream;
        IAsyncResult pending;
        readonly byte[] replyBuf = new byte[REPORT_LEN];

        public string DevicePath { get; private set; }
        public bool IsOpen { get { return cmdHandle != IntPtr.Zero && replyStream != null; } }

        // Agrupa as colecoes HID por instancia fisica e devolve os pares comando/resposta.
        // Identificamos pelas capacidades (FF02 com feature de 17B / FF01 com input de 17B)
        // em vez do numero da colecao, para funcionar em outros modelos da marca.
        static List<string[]> FindCandidates()
        {
            var cmd = new Dictionary<string, string>();
            var reply = new Dictionary<string, string>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);

            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                var det = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                det.cbSize = IntPtr.Size == 8 ? 8 : 5;
                int req = 0;
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, ref det, Marshal.SizeOf(det), ref req, IntPtr.Zero)) continue;
                string path = det.DevicePath;
                if (path.IndexOf("vid_260d", StringComparison.OrdinalIgnoreCase) < 0) continue;

                IntPtr h = CreateFile(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.ToInt64() == -1) continue;
                IntPtr pp;
                if (HidD_GetPreparsedData(h, out pp))
                {
                    HIDP_CAPS caps;
                    if (HidP_GetCaps(pp, out caps) == 0x110000)
                    {
                        string key = InstanceKey(path);
                        if (caps.UsagePage == 0xFF02 && caps.FeatureLen == REPORT_LEN) cmd[key] = path;
                        else if (caps.UsagePage == 0xFF01 && caps.InputLen == REPORT_LEN) reply[key] = path;
                    }
                    HidD_FreePreparsedData(pp);
                }
                CloseHandle(h);
            }
            SetupDiDestroyDeviceInfoList(set);

            var pairs = new List<string[]>();
            foreach (var kv in cmd)
                if (reply.ContainsKey(kv.Key)) pairs.Add(new[] { kv.Value, reply[kv.Key] });
            return pairs;
        }

        // Identificador da instancia fisica: pid + id do dispositivo, sem o sufixo da colecao.
        static string InstanceKey(string path)
        {
            var parts = path.Split('#');
            string pid = "";
            if (parts.Length > 1)
                foreach (var tok in parts[1].Split('&'))
                    if (tok.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) pid = tok;

            string inst = parts.Length > 2 ? parts[2] : "";
            int last = inst.LastIndexOf('&');
            if (last > 0) inst = inst.Substring(0, last);
            return pid + "|" + inst;
        }

        // Abre o primeiro dongle que realmente responder a uma consulta de bateria.
        public bool Open()
        {
            Close();
            foreach (var pair in FindCandidates())
            {
                if (!OpenPair(pair[0], pair[1])) continue;
                if (Query(CMD_BATTERY) != null) { DevicePath = pair[0]; return true; }
                Close();
            }
            return false;
        }

        bool OpenPair(string cmdPath, string replyPath)
        {
            cmdHandle = CreateFile(cmdPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (cmdHandle.ToInt64() == -1) { cmdHandle = IntPtr.Zero; return false; }

            IntPtr rh = CreateFile(replyPath, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (rh.ToInt64() == -1) { Close(); return false; }
            replyStream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(rh, true), FileAccess.Read, REPORT_LEN, true);
            return true;
        }

        static byte[] BuildCommand(byte cmd)
        {
            var b = new byte[REPORT_LEN];
            b[0] = 0x08; b[1] = cmd;
            int sum = 0;
            for (int i = 0; i < REPORT_LEN - 1; i++) sum += b[i];
            b[REPORT_LEN - 1] = (byte)((0x55 - sum) & 0xFF);
            return b;
        }

        static bool ChecksumOk(byte[] b)
        {
            int sum = 0;
            foreach (var x in b) sum += x;
            return (sum & 0xFF) == 0x55;
        }

        // Envia um comando e devolve os bytes de dados da resposta, ou null se falhar.
        public byte[] Query(byte cmd) { return Query(cmd, 2500); }

        public byte[] Query(byte cmd, int timeoutMs)
        {
            if (!IsOpen) return null;
            try
            {
                // a leitura precisa estar pendente ANTES do envio, senao a resposta se perde
                if (pending == null) pending = replyStream.BeginRead(replyBuf, 0, REPORT_LEN, null, null);
                if (!HidD_SetFeature(cmdHandle, BuildCommand(cmd), REPORT_LEN)) return null;

                var limit = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < limit)
                {
                    // nunca abandonar uma leitura em curso: ela consumiria o pacote sem ninguem ler
                    if (!pending.AsyncWaitHandle.WaitOne(200)) continue;

                    int n = replyStream.EndRead(pending);
                    var pkt = (byte[])replyBuf.Clone();
                    pending = replyStream.BeginRead(replyBuf, 0, REPORT_LEN, null, null);

                    if (n < REPORT_LEN || pkt[0] != 0x09 || pkt[1] != cmd) continue; // push de outro tipo
                    if (!ChecksumOk(pkt)) continue;
                    if (pkt[2] != 0x00) return null;                                 // indisponivel
                    int len = pkt[5];
                    if (len <= 0 || 6 + len > REPORT_LEN) return null;
                    var data = new byte[len];
                    Array.Copy(pkt, 6, data, 0, len);
                    return data;
                }
            }
            catch { Close(); }
            return null;
        }

        // Porcentagem de 0 a 100, ou -1 se indisponivel.
        public int ReadPercent()
        {
            var d = Query(CMD_BATTERY);
            if (d == null || d.Length < 1) return -1;
            int pct = d[0];
            return (pct >= 0 && pct <= 100) ? pct : -1;
        }

        // Estado do enlace 2.4G: 1 = mouse ativo no radio, 0 = fora do radio
        // (dormindo, desligado, ou em modo com fio), -1 = sem resposta.
        //
        // Atencao: este byte NAO indica carga. Ele vale 0 tanto com o cabo plugado
        // quanto com o mouse dormindo - confundir os dois gerava "carregando" falso.
        public int ReadLinkState()
        {
            var d = Query(CMD_LINK);
            if (d == null || d.Length < 1) return -1;
            return d[0] == 0x01 ? 1 : 0;
        }

        // Carga pelo cabo: ao plugar, o mouse enumera uma segunda interface USB
        // (alem do dongle 2.4G). Se existe outra instancia da marca presente, ele esta no cabo.
        // Comparar por PID, nao por instancia: um mesmo dongle expoe mi_00 e mi_01 com
        // tokens de instancia diferentes, o que daria falso positivo.
        public static bool CablePresent(string activePath)
        {
            if (string.IsNullOrEmpty(activePath)) return false;
            string mine = PidOf(activePath);
            if (mine == null) return false;
            foreach (var p in AllVendorPaths())
            {
                string pid = PidOf(p);
                if (pid != null && pid != mine) return true;
            }
            return false;
        }

        static string PidOf(string path)
        {
            int i = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            return (i < 0 || i + 8 > path.Length) ? null : path.Substring(i, 8).ToLowerInvariant();
        }

        static List<string> AllVendorPaths()
        {
            var found = new List<string>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                var det = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                det.cbSize = IntPtr.Size == 8 ? 8 : 5;
                int req = 0;
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, ref det, Marshal.SizeOf(det), ref req, IntPtr.Zero)) continue;
                if (det.DevicePath.IndexOf("vid_260d", StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(det.DevicePath);
            }
            SetupDiDestroyDeviceInfoList(set);
            return found;
        }

        public void Close()
        {
            try { if (replyStream != null) replyStream.Dispose(); } catch { }
            replyStream = null;
            pending = null;
            if (cmdHandle != IntPtr.Zero && cmdHandle.ToInt64() != -1) CloseHandle(cmdHandle);
            cmdHandle = IntPtr.Zero;
        }

        public void Dispose() { Close(); }
    }
}
