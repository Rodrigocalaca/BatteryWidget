using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Ferramenta generica de HID para exploracao:
//   HidTool get    <vid> <trecho-do-path> <reportId> <len>
//   HidTool set    <vid> <trecho-do-path> <bytes em hex separados por espaco>
//   HidTool listen <vid> <trecho-do-path> <len> <segundos>
//   HidTool poll   <vid> <trecho-do-path> <reportId> <len> <segundos>   (mostra so mudancas)
class HidTool
{
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(IntPtr h, byte[] b, int len);
    [DllImport("kernel32.dll")] static extern int GetLastError();

    static IntPtr Open(ushort vid, string sub, bool write)
    {
        foreach (var p in HidDump.Paths(vid))
        {
            if (p.IndexOf(sub, StringComparison.OrdinalIgnoreCase) < 0) continue;
            uint acc = write ? 0xC0000000 : 0x80000000;
            IntPtr h = HidDump.CreateFile(p, acc, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.ToInt64() == -1 && write) h = HidDump.CreateFile(p, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.ToInt64() == -1) h = HidDump.CreateFile(p, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.ToInt64() != -1) { Console.WriteLine("[path] " + p); return h; }
            Console.WriteLine("[falhou abrir] " + p + " err=" + GetLastError());
        }
        return (IntPtr)(-1);
    }

    static byte[] ParseHex(string[] args, int from)
    {
        var list = new List<byte>();
        for (int i = from; i < args.Length; i++)
            foreach (var tok in args[i].Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(byte.Parse(tok.Replace("0x", ""), NumberStyles.HexNumber));
        return list.ToArray();
    }

    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.WriteLine("uso: get|set|listen|poll <vid hex> <path> ..."); return 1; }
        string op = a[0].ToLowerInvariant();
        ushort vid = ushort.Parse(a[1].Replace("0x", ""), NumberStyles.HexNumber);
        string sub = a[2];

        if (op == "get")
        {
            byte id = byte.Parse(a[3].Replace("0x", ""), NumberStyles.HexNumber);
            int len = int.Parse(a[4]);
            IntPtr h = Open(vid, sub, true);
            if (h.ToInt64() == -1) { Console.WriteLine("dispositivo nao encontrado"); return 2; }
            var b = new byte[len]; b[0] = id;
            bool ok = HidD_GetFeature(h, b, len);
            Console.WriteLine(ok ? HidListen.Hex(b, len) : "GetFeature falhou err=" + GetLastError());
            HidDump.CloseHandle(h);
            return ok ? 0 : 3;
        }

        if (op == "set")
        {
            var payload = ParseHex(a, 3);
            IntPtr h = Open(vid, sub, true);
            if (h.ToInt64() == -1) { Console.WriteLine("dispositivo nao encontrado"); return 2; }
            bool ok = HidD_SetFeature(h, payload, payload.Length);
            Console.WriteLine("SetFeature(" + payload.Length + "B) -> " + ok + (ok ? "" : " err=" + GetLastError()));
            HidDump.CloseHandle(h);
            return ok ? 0 : 3;
        }

        if (op == "listen")
        {
            int len = int.Parse(a[3]);
            int secs = int.Parse(a[4]);
            HidListen.VidOverride = vid;
            HidListen.Listen(sub, len, secs);
            return 0;
        }

        if (op == "poll")
        {
            byte id = byte.Parse(a[3].Replace("0x", ""), NumberStyles.HexNumber);
            int len = int.Parse(a[4]);
            int secs = int.Parse(a[5]);
            IntPtr h = Open(vid, sub, true);
            if (h.ToInt64() == -1) { Console.WriteLine("dispositivo nao encontrado"); return 2; }
            string last = "";
            var deadline = DateTime.Now.AddSeconds(secs);
            while (DateTime.Now < deadline)
            {
                var b = new byte[len]; b[0] = id;
                string cur = HidD_GetFeature(h, b, len) ? HidListen.Hex(b, len) : "falhou";
                if (cur != last) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + cur); last = cur; }
                Thread.Sleep(a.Length > 6 ? int.Parse(a[6]) : 250);
            }
            HidDump.CloseHandle(h);
            return 0;
        }

        Console.WriteLine("operacao desconhecida: " + op);
        return 1;
    }
}
