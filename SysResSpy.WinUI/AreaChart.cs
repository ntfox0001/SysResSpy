using System;
using System.Collections.Generic;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

using SysResSpy.Sampling;

namespace SysResSpy.WinUI
{
    /// <summary>
    /// Lightweight area chart for a process's resource history. Derives from Canvas
    /// and redraws gridlines, a filled area + line, and a current/peak value overlay
    /// whenever the data or size changes. Text colors adapt to the active theme.
    /// </summary>
    public sealed class AreaChart : Canvas
    {
        private ProcessHistory _history;
        private readonly TextBlock _titleText;
        private readonly TextBlock _badgeText;
        private string _title;
        private bool _bytesMode;
        private const int GridLines = 5;

        private Brush _titleBrush;
        private Brush _labelBrush;
        private Color _gridColor;
        private Color _axisColor;

        // Reserved height at the top for title + current/peak text, so the plot
        // never overlaps the labels.
        private const int HeaderHeight = 48;

        public string Title
        {
            get => _title;
            set { _title = value; _titleText.Text = value ?? ""; }
        }

        public bool BytesMode
        {
            get => _bytesMode;
            set { _bytesMode = value; Draw(); }
        }

        public AreaChart()
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            IsHitTestVisible = false;
            SizeChanged += (s, e) => Draw();
            ActualThemeChanged += (s, e) => Draw();

            ApplyThemeColors();

            _titleText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _titleBrush,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetLeft(_titleText, 8);
            SetTop(_titleText, 4);
            Children.Add(_titleText);

            _badgeText = new TextBlock
            {
                FontSize = 11,
                Foreground = _labelBrush,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetLeft(_badgeText, 8);
            SetTop(_badgeText, 26);
            _badgeText.Text = "当前 -    峰值 -";
            Children.Add(_badgeText);
        }

        public void SetData(ProcessHistory h)
        {
            _history = h;
            Draw();
        }

        public void ClearData()
        {
            _history = null;
            _badgeText.Text = "当前 -    峰值 -";
            Draw();
        }

        public void Invalidate() => Draw();

        private void ApplyThemeColors()
        {
            // Use the system theme text brushes so chart text matches the rest of
            // the UI exactly and follows light/dark automatically.
            _titleBrush = ThemeBrush("TextFillColorPrimaryBrush", Color.FromArgb(235, 30, 30, 38));
            _labelBrush = ThemeBrush("TextFillColorSecondaryBrush", Color.FromArgb(215, 95, 95, 108));

            bool dark = (ActualTheme == ElementTheme.Dark);
            _gridColor = dark
                ? Color.FromArgb(40, 255, 255, 255)
                : Color.FromArgb(35, 0, 0, 0);
            _axisColor = dark
                ? Color.FromArgb(70, 255, 255, 255)
                : Color.FromArgb(90, 0, 0, 0);
        }

        private static Brush ThemeBrush(string key, Color fallback)
        {
            if (Application.Current?.Resources != null
                && Application.Current.Resources.TryGetValue(key, out object v)
                && v is Brush b)
                return b;
            return new SolidColorBrush(fallback);
        }

        /// <summary>Regenerate all visuals by clearing and repopulating the canvas.</summary>
        private void Draw()
        {
            Children.Clear();

            ApplyThemeColors();
            _titleText.Foreground = _titleBrush;
            _badgeText.Foreground = _labelBrush;

            // Re-add the persistent text overlays first.
            Children.Add(_titleText);
            Children.Add(_badgeText);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 10 || h <= 10) return;

            const int leftPad = 56, bottomPad = 20, rightPad = 6;
            double plotLeft = leftPad;
            double plotTop = HeaderHeight;
            double plotW = w - leftPad - rightPad;
            double plotH = h - HeaderHeight - bottomPad;
            if (plotW < 20 || plotH < 20) return;

            // Border
            Children.Add(MakeRect(plotLeft, plotTop, plotW, plotH, _axisColor, 0));

            // Collect values (oldest-first).
            var values = new List<double>();
            if (_history != null && _history.Count > 0)
            {
                for (int n = 0; n < _history.Count; n++)
                {
                    int idx = (_history.Head - 1 - n + ProcessHistory.Capacity * 2) % ProcessHistory.Capacity;
                    values.Add(_bytesMode ? _history.WorkingSet[idx] : _history.Cpu[idx]);
                }
                values.Reverse();
            }

            double dataMax = 1;
            foreach (double v in values) if (v > dataMax) dataMax = v;
            if (dataMax <= 0) dataMax = 1;
            double yMax = NiceCeil(dataMax);
            double yRange = yMax > 0 ? yMax : 1;

            // Gridlines + y labels
            for (int i = 0; i <= GridLines; i++)
            {
                double frac = (double)i / GridLines;
                double y = plotTop + plotH - frac * plotH;
                var line = new Line
                {
                    X1 = plotLeft, Y1 = y, X2 = plotLeft + plotW, Y2 = y,
                    Stroke = new SolidColorBrush(_gridColor),
                    StrokeThickness = 1
                };
                Children.Add(line);
                double val = (1 - frac) * yRange;
                string txt = _bytesMode ? Format.Bytes(val) : Format.Number(val);
                var lbl = new TextBlock
                {
                    FontSize = 10,
                    Foreground = _labelBrush,
                    Text = txt,
                    IsHitTestVisible = false
                };
                SetLeft(lbl, 6);
                SetTop(lbl, y - 6);
                Children.Add(lbl);
            }

            // Plot area + line + current/peak overlay
            if (values.Count >= 2)
            {
                double step = plotW / (double)(values.Count - 1);
                var pts = new Point[values.Count];
                for (int i = 0; i < values.Count; i++)
                {
                    double v = values[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
                    if (v < 0) v = 0;
                    double y = plotTop + plotH - (v / yRange) * plotH;
                    pts[i] = new Point(plotLeft + i * step, Clamp(y, plotTop, plotTop + plotH));
                }

                // filled area
                var geo = new PathGeometry();
                var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
                for (int i = 1; i < pts.Length; i++) fig.Segments.Add(new LineSegment { Point = pts[i] });
                fig.Segments.Add(new LineSegment { Point = new Point(pts[pts.Length - 1].X, plotTop + plotH) });
                fig.Segments.Add(new LineSegment { Point = new Point(pts[0].X, plotTop + plotH) });
                geo.Figures.Add(fig);
                Children.Add(new Path { Data = geo, Fill = new SolidColorBrush(Color.FromArgb(48, 124, 108, 240)), StrokeThickness = 0 });

                // line
                var lineGeo = new PathGeometry();
                var lineFig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
                for (int i = 1; i < pts.Length; i++) lineFig.Segments.Add(new LineSegment { Point = pts[i] });
                lineGeo.Figures.Add(lineFig);
                Children.Add(new Path { Data = lineGeo, Stroke = new SolidColorBrush(Color.FromArgb(235, 124, 108, 240)), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round });

                double cur = values[values.Count - 1];
                double peak = values[0];
                foreach (double v in values) if (v > peak) peak = v;
                _badgeText.Text = _bytesMode
                    ? "当前 " + Format.Bytes(cur) + "    峰值 " + Format.Bytes(peak)
                    : "当前 " + cur.ToString("0.0") + "%    峰值 " + peak.ToString("0.0") + "%";
            }
            else if (values.Count == 1)
            {
                double v = values[0];
                if (v < 0) v = 0;
                double y = plotTop + plotH - (v / yRange) * plotH;
                double x = plotLeft + plotW / 2.0;
                var dot = new Ellipse
                {
                    Width = 5, Height = 5,
                    Fill = new SolidColorBrush(Color.FromArgb(235, 124, 108, 240))
                };
                SetLeft(dot, x - 2.5); SetTop(dot, y - 2.5);
                Children.Add(dot);
                _badgeText.Text = _bytesMode ? "当前 " + Format.Bytes(v) : "当前 " + v.ToString("0.0") + "%";
            }
            else
            {
                _badgeText.Text = "等待采样数据...";
            }
        }

        private static Rectangle MakeRect(double x, double y, double w, double h, Color stroke, double radius)
        {
            var r = new Rectangle
            {
                Width = w, Height = h,
                RadiusX = radius, RadiusY = radius,
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 1,
                Fill = null
            };
            SetLeft(r, x); SetTop(r, y);
            r.IsHitTestVisible = false;
            return r;
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static double NiceCeil(double v)
        {
            if (v <= 1) return 1;
            if (v <= 5) return 5;
            if (v <= 10) return 10;
            if (v <= 25) return 25;
            if (v <= 50) return 50;
            if (v <= 100) return 100;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double norm = v / mag;
            double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
            return nice * mag;
        }
    }
}