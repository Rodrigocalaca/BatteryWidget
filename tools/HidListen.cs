using System;
using System.IO;
using System.Threading;

public static class HidListen
{
    public static ushort VidOverride = 0x260D;

    public static void Listen(string substr, int inLen, int seconds)
    {
        var threads = new System.Collections.Generic.List<Thread>();
        foreach (var path in HidDump.Paths(VidOverride))
        {
            if (path.IndexOf(substr, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var p = path;
            var t = new Thread(() => Pump(p, inLen, seconds));
            t.IsBackground = true;
            t.Start();
            threads.Add(t);
        }
        foreach (var t in threads) t.Join();
    }

    static void Pump(string path, int inLen, int seconds)
    {
        IntPtr h = HidDump.CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero); // FILE_FLAG_OVERLAPPED
        if (h.ToInt64() == -1) { Console.WriteLine("[open fail] " + Shorten(path)); return; }
        var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, true), FileAccess.Read, inLen, true);
        var buf = new byte[inLen];
        var deadline = DateTime.Now.AddSeconds(seconds);
        Console.WriteLine("[listening] " + Shorten(path) + " len=" + inLen);

        IAsyncResult ar = null;
        while (DateTime.Now < deadline)
        {
            try
            {
                // mantem UMA leitura pendente por vez; nunca abandona uma leitura em curso
                if (ar == null) ar = fs.BeginRead(buf, 0, inLen, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(500)) continue;
                int n = fs.EndRead(ar);
                ar = null;
                if (n > 0)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + Shorten(path) + "  " + Hex(buf, n));
                    Console.Out.Flush();
                }
            }
            catch (Exception e) { Console.WriteLine("[err] " + Shorten(path) + " " + e.Message); return; }
        }
    }

    public static string Hex(byte[] b, int n)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString();
    }

    static string Shorten(string p)
    {
        int i = p.IndexOf("col"); int j = p.IndexOf("pid_");
        string pid = j >= 0 ? p.Substring(j, 8) : "?";
        return pid + "/" + (i >= 0 ? p.Substring(i, 5) : "mi00");
    }
}
