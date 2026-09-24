using System.ComponentModel;
using System.Windows.Forms;

namespace SysResSpy.UI
{
    internal static class ControlExtensions
    {
        public static void DoubleBuffered(this Control control)
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(control, true);
        }
    }
}