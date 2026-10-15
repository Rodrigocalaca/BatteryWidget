using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class HidDump
{
    const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string DevicePath; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage; public byte ReportID; [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
        public byte Reserved; public ushort BitSize, ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public int UnitsExp, Units, LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort U1, U2, U3, U4, U5, U6;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, ref SP_DEVICE_INTERFACE_DETAIL_DATA detail, int size, ref int req, IntPtr devInfo);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr h, StringBuilder b, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint create, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    public static List<string> Paths(ushort vid)
    {
        var result = new List<string>();
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        var did = new SP_DEVICE_INTERFACE_DATA(); did.cbSize = Marshal.SizeOf(did);
        for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
        {
            var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
            detail.cbSize = IntPtr.Size == 8 ? 8 : 5;
            int req = 0;
            if (!SetupDiGetDeviceInterfaceDetail(set, ref did, ref detail, Marshal.SizeOf(detail), ref req, IntPtr.Zero)) continue;
            if (vid != 0 && detail.DevicePath.IndexOf(string.Format("vid_{0:x4}", vid), StringComparison.OrdinalIgnoreCase) < 0) continue;
            result.Add(detail.DevicePath);
        }
        SetupDiDestroyDeviceInfoList(set);
        return result;
    }

    public static void Report(ushort vid)
    {
        foreach (var path in Paths(vid))
        {
            IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            bool rw = h.ToInt64() != -1;
            if (!rw) h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.ToInt64() == -1) { Console.WriteLine("[cannot open] " + path); continue; }

            var attr = new HIDD_ATTRIBUTES(); attr.Size = Marshal.SizeOf(attr);
            HidD_GetAttributes(h, ref attr);
            var sb = new StringBuilder(256); HidD_GetProductString(h, sb, 256);

            IntPtr pp;
            if (HidD_GetPreparsedData(h, out pp))
            {
                HIDP_CAPS caps; HidP_GetCaps(pp, out caps);
                Console.WriteLine("=== " + path);
                Console.WriteLine(string.Format("    product='{0}' vid={1:X4} pid={2:X4} ver={3:X4} rw={4}", sb, attr.VendorID, attr.ProductID, attr.VersionNumber, rw));
                Console.WriteLine(string.Format("    top-level usagePage=0x{0:X2} usage=0x{1:X2}  inLen={2} outLen={3} featLen={4}",
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
                for (int type = 0; type < 3; type++)
                {
                    ushort n = type == 0 ? caps.NumberInputValueCaps : type == 1 ? caps.NumberOutputValueCaps : caps.NumberFeatureValueCaps;
                    if (n == 0) continue;
                    var vc = new HIDP_VALUE_CAPS[n];
                    if (HidP_GetValueCaps(type, vc, ref n, pp) != 0x110000) continue;
                    string tn = type == 0 ? "INPUT" : type == 1 ? "OUTPUT" : "FEATURE";
                    for (int k = 0; k < n; k++)
                        Console.WriteLine(string.Format("    {0} reportId=0x{1:X2} usagePage=0x{2:X2} usage=0x{3:X2} bitSize={4} count={5} lmin={6} lmax={7}",
                            tn, vc[k].ReportID, vc[k].UsagePage, vc[k].IsRange ? vc[k].U1 : vc[k].U1, vc[k].BitSize, vc[k].ReportCount, vc[k].LogicalMin, vc[k].LogicalMax));
                }
                HidD_FreePreparsedData(pp);
            }
            CloseHandle(h);
        }
    }
}
