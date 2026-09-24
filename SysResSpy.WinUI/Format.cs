using System;

namespace SysResSpy.WinUI
{
    internal static class Format
    {
        public static string Bytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? v.ToString("0") + " " + units[u]
                          : v.ToString("0.0") + " " + units[u];
        }

        /// <summary>Format a plain number, keeping an integer like 10 intact (avoids
        /// trailing-zero trimming bugs) and showing one decimal for non-integers.</summary>
        public static string Number(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.0001) return Math.Round(v).ToString("0");
            return v.ToString("0.#");
        }
    }
}