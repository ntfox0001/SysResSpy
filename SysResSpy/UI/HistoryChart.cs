using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using SysResSpy.Sampling;

namespace SysResSpy.UI
{
    /// <summary>
    /// Lightweight GDI+ line chart that renders a process's resource history.
    /// Auto-scales the Y axis to the data min/max and renders a filled area + line.
    /// </summary>
    public sealed class HistoryChart : Control
    {
        private ProcessHistory _history;
        private readonly Pen _linePen;
        private readonly Brush _fillBrush;
        private readonly SolidBrush _textBrush;
        private readonly SolidBrush _gridBrush;
        private readonly Font _uiFont;

        public string Unit { get; set; }              // "%" or bytes-based formatting toggle
        public bool BytesMode { get; set; }           // true => memory chart
        public string Title { get; set; }
        public Color LineColor { get; set; }

        public HistoryChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(18, 18, 24);
            LineColor = Color.FromArgb(80, 200, 255);
            _linePen = new Pen(LineColor, 1.6f);
            _fillBrush = new SolidBrush(Color.FromArgb(40, 80, 200, 255));
            _textBrush = new SolidBrush(Color.FromArgb(210, 210, 220));
            _gridBrush = new SolidBrush(Color.FromArgb(38, 38, 48));
            _uiFont = new Font("Segoe UI", 9f);
            ForeColor = Color.FromArgb(210, 210, 220);
            ResizeRedraw = true;
        }

        public void SetData(ProcessHistory h) { _history = h; Invalidate(); }
        public void ClearData() { _history = null; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int leftPad = 58, topPad = 26, bottomPad = 22, rightPad = 4;
            Rectangle plot = new Rectangle(leftPad, topPad, Width - leftPad - rightPad, Height - topPad - bottomPad);
            if (plot.Width < 20 || plot.Height < 20) return;

            // Border
            using (var border = new Pen(Color.FromArgb(50, 50, 60)))
            {
                g.DrawRectangle(border, plot);
            }

            var values = new List<float>();
            if (_history != null && _history.Count > 0)
            {
                for (int n = 0; n < _history.Count; n++)
                {
                    int idx = (_history.Head - 1 - n + ProcessHistory.Capacity * 2) % ProcessHistory.Capacity;
                    values.Add(BytesMode ? _history.WorkingSet[idx] : _history.Cpu[idx]);
                }
                values.Reverse(); // oldest-first
            }

            // Compute scale
            float dataMax = 1;
            if (values.Count > 0)
            {
                dataMax = float.NegativeInfinity;
                foreach (float v in values) if (v > dataMax) dataMax = v;
                if (dataMax <= 0) dataMax = 1;
            }

            float yMax = NiceCeil(dataMax);
            float yMin = 0;
            float yRange = yMax - yMin;

            // Grid + y labels
            const int gridLines = 5;
            for (int i = 0; i <= gridLines; i++)
            {
                float frac = (float)i / gridLines;
                float y = plot.Bottom - frac * plot.Height;
                g.FillRectangle(_gridBrush, plot.Left, y, plot.Width, 1);
                double val = yMin + (1 - frac) * yRange;
                string text = BytesMode ? FormatBytes(val) : val.ToString("0");
                g.DrawString(text, _uiFont, _textBrush, plot.Left - 4, y - 8, new StringFormat { Alignment = StringAlignment.Far });
            }

            // Title
            if (!string.IsNullOrEmpty(Title))
            {
                g.DrawString(Title, _uiFont, _textBrush, plot.Left, 6);
            }

            // Plot line + area
            if (values.Count >= 2)
            {
                float x = plot.Left;
                float step = plot.Width / (float)Math.Max(1, values.Count - 1);
                var pts = new PointF[values.Count];
                for (int i = 0; i < values.Count; i++)
                {
                    float v = values[i];
                    if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
                    float y = plot.Bottom - (v - yMin) / yRange * plot.Height;
                    pts[i] = new PointF(plot.Left + i * step, Clamp(y, plot.Top, plot.Bottom));
                }

                // filled area
                var areaPts = new List<PointF> { new PointF(pts[0].X, plot.Bottom) };
                areaPts.AddRange(pts);
                areaPts.Add(new PointF(pts[pts.Length - 1].X, plot.Bottom));
                g.FillPolygon(_fillBrush, areaPts.ToArray());
                g.DrawLines(_linePen, pts);
            }
            else if (values.Count == 1)
            {
                float y = plot.Bottom - (values[0] - yMin) / yRange * plot.Height;
                float x = plot.Left + plot.Width / 2f;
                g.DrawRectangle(_linePen, x - 1, y - 1, 2, 2);
            }
            else
            {
                g.DrawString("等待采样数据...", _uiFont, _textBrush, plot.Left + 8, plot.Top + 8);
            }

            // current value badge
            if (values.Count > 0)
            {
                float cur = values[values.Count - 1];
                string curText = BytesMode ? FormatBytes(cur) : cur.ToString("0.0") + "%";
                g.DrawString("当前 " + curText, _uiFont, _textBrush, plot.Left + 6, plot.Top + 6);
                g.DrawString("峰值 " + (BytesMode ? FormatBytes(dataMax) : dataMax.ToString("0.0") + "%"),
                    _uiFont, _textBrush, plot.Right - 100, plot.Top + 6);
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static float NiceCeil(float v)
        {
            if (v <= 1) return 1;
            if (v <= 5) return 5;
            if (v <= 10) return 10;
            if (v <= 25) return 25;
            if (v <= 50) return 50;
            if (v <= 100) return 100;
            float mag = (float)Math.Pow(10, Math.Floor(Math.Log10(v)));
            float norm = v / mag;
            float nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
            return nice * mag;
        }

        public static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? v.ToString("0") + " " + units[u]
                          : v.ToString("0.0") + " " + units[u];
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _linePen.Dispose();
                _fillBrush.Dispose();
                _textBrush.Dispose();
                _gridBrush.Dispose();
                _uiFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}