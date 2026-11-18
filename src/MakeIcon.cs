using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
internal static class MakeIcon
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    static void Main()
    {
        using (var bitmap = new Bitmap(64, 64))
        {
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(Color.FromArgb(24, 85, 67))) g.FillEllipse(brush, 1, 1, 62, 62);
                using (var pen = new Pen(Color.White, 4))
                { pen.StartCap = pen.EndCap = LineCap.Round; g.DrawLine(pen, 23, 21, 46, 21); g.DrawLine(pen, 23, 32, 46, 32); g.DrawLine(pen, 23, 43, 38, 43); }
                using (var brush = new SolidBrush(Color.FromArgb(166, 226, 199))) g.FillPolygon(brush, new PointF[] { new PointF(11, 25), new PointF(19, 32), new PointF(11, 39) });
            }
            IntPtr handle = bitmap.GetHicon();
            try { using (var icon = Icon.FromHandle(handle)) using (var file = File.Create("Sufler.ico")) icon.Save(file); }
            finally { DestroyIcon(handle); }
        }
    }
}
