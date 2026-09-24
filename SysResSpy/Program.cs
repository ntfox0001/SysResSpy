using System;
using System.Windows.Forms;

using SysResSpy.UI;

namespace SysResSpy
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(@"d:\SysResSpy\crash.log", DateTime.Now.ToString("HH:mm:ss.fff") + " " + ex + Environment.NewLine); } catch { }
                throw;
            }
        }
    }
}