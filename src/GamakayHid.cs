using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DareuBattery
{
    // Leitura de bateria do teclado GAMAKAY (VID 0x3151).
    //
    // O dongle mantem um feature report de 65 bytes residente na colecao vendor
    // (usage page 0xFFFF, usage 0x02) com o status do enlace 2.4G:
    //
    //     00 <slot> <bateria%> 00 00 01 <online> 01 ...
    //
    // Confirmado cruzando tres fontes: o byte 2 valia 0x2D (45); o servidor gRPC do
    // proprio SDK da marca (iot_driver) devolvia Status24{battery:45,isOnline:true};
    // e o icone do app oficial estava no bucket "battery_40".
    //
    // Diferente do mouse Dareu, aqui nao enviamos nada: so leitura.
    public class GamakayHid : IDisposable
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 3, OPEN_EXISTING = 3;

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
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint cr, uint fl, IntPtr t);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        readonly ushort vid;
        readonly int reportLen;
        readonly int percentByte;
        IntPtr handle = IntPtr.Zero;

        public string DevicePath { get; private set; }
        public bool IsOpen { get { return handle != IntPtr.Zero; } }

        public GamakayHid(ushort vid, int reportLen, int percentByte)
        {
            this.vid = vid;
            this.reportLen = reportLen;
            this.percentByte = percentByte;
        }

        static List<string> VendorPaths(ushort vid, int featureLen)
        {
            var found = new List<string>();
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
            string tag = string.Format("vid_{0:x4}", vid);

            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
            {
                var det = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                det.cbSize = IntPtr.Size == 8 ? 8 : 5;
                int req = 0;
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, ref det, Marshal.SizeOf(det), ref req, IntPtr.Zero)) continue;
                if (det.DevicePath.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;

                IntPtr h = CreateFile(det.DevicePath, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.ToInt64() == -1) continue;
                IntPtr pp;
                if (HidD_GetPreparsedData(h, out pp))
                {
                    HIDP_CAPS caps;
                    if (HidP_GetCaps(pp, out caps) == 0x110000 &&
                        caps.UsagePage == 0xFFFF && caps.FeatureLen == featureLen)
                        found.Add(det.DevicePath);
                    HidD_FreePreparsedData(pp);
                }
                CloseHandle(h);
            }
            SetupDiDestroyDeviceInfoList(set);
            return found;
        }

        public bool Open()
        {
            Close();
            foreach (var p in VendorPaths(vid, reportLen))
            {
                IntPtr h = CreateFile(p, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.ToInt64() == -1) h = CreateFile(p, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.ToInt64() == -1) continue;
                handle = h;
                DevicePath = p;
                return true;
            }
            return false;
        }

        // Porcentagem 0..100, ou -1 se o dongle ainda nao publicou status.
        public int ReadPercent(out bool online)
        {
            online = false;
            if (!IsOpen) return -1;
            var buf = new byte[reportLen];
            buf[0] = 0x00;
            if (!HidD_GetFeature(handle, buf, reportLen)) { Close(); return -1; }

            // quadro valido: slot 0/1, porcentagem plausivel e o marcador 0x01 no byte 5
            if (buf[1] > 1 || buf[5] != 0x01) return -1;
            int pct = buf[percentByte];
            if (pct <= 0 || pct > 100) return -1;
            online = buf[6] == 0x01 || buf[7] == 0x01;
            return pct;
        }

        public void Close()
        {
            if (handle != IntPtr.Zero && handle.ToInt64() != -1) CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        public void Dispose() { Close(); }
    }
}
