using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using SysResSpy.Sampling;

namespace SysResSpy.UI
{
    public sealed class MainForm : Form
    {
        private readonly ProcessSampler _sampler;
        private ListView _lstProcesses;
        private readonly Dictionary<string, ListViewItem> _itemsByKey = new Dictionary<string, ListViewItem>();
        private readonly Dictionary<string, ProcessSnapshot> _snapByKey = new Dictionary<string, ProcessSnapshot>();
        private int _sortColumn;          // 0=名称, 1=PID, 2=CPU%, 3=内存
        private bool _sortAscending = true;
        private string[] _colTitles;
        private readonly RowComparer _comparer;
        private CheckBox _chkGroup;
        private bool _groupByName;
        private HistoryChart _cpuChart;
        private HistoryChart _memChart;
        private System.Windows.Forms.Timer _uiTimer;
        private Label _statusBar;

        private sealed class RowComparer : System.Collections.IComparer
        {
            private readonly MainForm _f;
            public RowComparer(MainForm f) { _f = f; }
            public int Compare(object x, object y)
            {
                var a = (ListViewItem)x; var b = (ListViewItem)y;
                if (_f._snapByKey.TryGetValue((string)a.Tag, out var sa)
                    && _f._snapByKey.TryGetValue((string)b.Tag, out var sb))
                {
                    int r = _f.CompareSnapshots(sa, sb);
                    return _f._sortAscending ? r : -r;
                }
                return string.CompareOrdinal(a.Text, b.Text);
            }
        }

        public MainForm()
        {
            Text = "SysResSpy - 系统进程资源监控";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 460);
            Font = new Font("Segoe UI", 9f);
            _comparer = new RowComparer(this);

            BuildLayout();

            _sampler = new ProcessSampler();
            _sampler.SamplesUpdated += OnSamplesUpdated;
            _sampler.Start();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _uiTimer.Tick += (s, e) => RefreshUi();
            _uiTimer.Start();

            RefreshUi();
            UpdateColumnHeaders();
        }

        private void BuildLayout()
        {
            // Left: process list panel
            var leftPanel = new Panel { Dock = DockStyle.Fill };
            _lstProcesses = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                GridLines = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(22, 22, 30),
                ForeColor = Color.FromArgb(220, 220, 230),
                Font = new Font("Consolas", 9f)
            };
            _lstProcesses.Columns.Add("进程", 128);
            _lstProcesses.Columns.Add("PID", 54);
            _lstProcesses.Columns.Add("CPU%", 58, HorizontalAlignment.Right);
            _lstProcesses.Columns.Add("内存", 66, HorizontalAlignment.Right);
            _colTitles = new string[] { "进程", "PID", "CPU%", "内存" };
            // We sort manually via ApplySort() (native ListView.Sort mis-orders on the
            // second click of a column), so never install a ListViewItemSorter.
            _lstProcesses.ColumnClick += (s, e) =>
            {
                if (e.Column == _sortColumn) _sortAscending = !_sortAscending;
                else { _sortColumn = e.Column; _sortAscending = true; }
                UpdateColumnHeaders();
                ApplySort();
            };
            _lstProcesses.SelectedIndexChanged += (s, e) => UpdateCharts();

            var leftHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(30, 30, 40)
            };
            var headerTitle = new Label
            {
                Text = " 进程列表",
                Dock = DockStyle.Left,
                Width = 90,
                Height = 28,
                Padding = new Padding(6, 6, 0, 0),
                ForeColor = Color.FromArgb(170, 170, 185)
            };
            _chkGroup = new CheckBox
            {
                Text = "按名称分组",
                Dock = DockStyle.Right,
                Width = 110,
                Height = 28,
                Checked = false,
                ForeColor = Color.FromArgb(170, 170, 185),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _chkGroup.CheckedChanged += (s, e) =>
            {
                _groupByName = _chkGroup.Checked;
                _itemsByKey.Clear();
                _lstProcesses.Items.Clear();
                SelectFirst();
            };
            // grouping defaults OFF; user can tick the box to merge same-named processes
            leftHeader.Controls.Add(headerTitle);
            leftHeader.Controls.Add(_chkGroup);

            leftPanel.Resize += (s, e) => { /* keep list anchored */ };
            leftPanel.Controls.Add(_lstProcesses);
            leftPanel.Controls.Add(leftHeader);

            // Right: two charts. Use simple Dock layout (no nested SplitContainer,
            // which failed to lay out child Dock=Fill controls). Last-added is
            // docked first by WinForms, so add the Fill chart first.
            _cpuChart = new HistoryChart { Title = "CPU 占用历史", Unit = "%" };
            _memChart = new HistoryChart { Title = "内存占用历史", BytesMode = true };

            var rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 24) };
            _memChart.Dock = DockStyle.Fill;    // added first -> filled last -> takes remainder
            _cpuChart.Dock = DockStyle.Top;      // added last  -> docked first -> pinned to top
            rightPanel.Controls.Add(_memChart);
            rightPanel.Controls.Add(_cpuChart);

            rightPanel.Resize += (s, e) => { _cpuChart.Height = rightPanel.Height / 2; };

            // Main split: left list vs right charts
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(40, 40, 50)
            };
            mainSplit.Panel1.Controls.Add(leftPanel);
            mainSplit.Panel2.Controls.Add(rightPanel);
            // Do NOT set SplitterDistance/MinSize here (Width is still 0; the
            // MinSize setter re-validates SplitterDistance and would throw).
            // Configure both once the form has real bounds.
            Shown += (s, e) =>
            {
                mainSplit.Panel1MinSize = 320;
                mainSplit.Panel2MinSize = 260;
                if (mainSplit.Width > 100) mainSplit.SplitterDistance = (int)(mainSplit.Width * 0.5);
            };
            Resize += (s, e) => { if (mainSplit.Width > 100 && mainSplit.SplitterDistance < mainSplit.Panel1MinSize + 1) mainSplit.SplitterDistance = (int)(mainSplit.Width * 0.5); };

            _statusBar = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(150, 150, 165),
                BackColor = Color.FromArgb(30, 30, 40)
            };

            var root = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 24) };
            root.Controls.Add(mainSplit);
            root.Controls.Add(_statusBar);

            ClientSize = new Size(960, 560);
            Controls.Add(root);

            // avoid splitter jump from header on refresh
            _lstProcesses.DoubleBuffered();
        }

        private void OnSamplesUpdated(ProcessSampler sampler)
        {
            // UI thread-safe: capture into synced list here (already under sampler lock).
        }

        private void RefreshUi()
        {
            // Determine selected key to preserve selection across updates.
            string selectedKey = null;
            if (_lstProcesses.SelectedItems.Count > 0)
            {
                selectedKey = (string)_lstProcesses.SelectedItems[0].Tag;
            }

            var procs = _groupByName ? _sampler.GetProcessesGrouped() : _sampler.GetProcesses();

            // Incremental update: mutate/reuse existing items instead of clearing
            // and rebuilding the list, so scroll position and drag are preserved.
            var liveKeys = new HashSet<string>();
            _lstProcesses.BeginUpdate();
            try
            {
                foreach (ProcessSnapshot p in procs)
                {
                    string key = KeyOf(p);
                    liveKeys.Add(key);
                    int count = CountOf(p);
                    string pidText = _groupByName
                        ? (count > 1 ? "×" + count : p.Id.ToString())
                        : p.Id.ToString();
                    if (_itemsByKey.TryGetValue(key, out ListViewItem item))
                    {
                        // Update text in place; if only values changed the item keeps
                        // its index so the scrollbar is not disturbed.
                        item.SubItems[0].Text = p.Name;
                        item.SubItems[1].Text = pidText;
                        item.SubItems[2].Text = p.CpuPercent.ToString("0.0");
                        item.SubItems[3].Text = HistoryChart.FormatBytes(p.WorkingSet);
                        _snapByKey[key] = p;
                    }
                    else
                    {
                        var it = new ListViewItem(p.Name);
                        it.SubItems.Add(pidText);
                        it.SubItems.Add(p.CpuPercent.ToString("0.0"));
                        it.SubItems.Add(HistoryChart.FormatBytes(p.WorkingSet));
                        it.Tag = key;
                        it.ForeColor = Color.FromArgb(220, 220, 230);
                        _itemsByKey[key] = it;
                        _snapByKey[key] = p;
                        _lstProcesses.Items.Insert(InsertIndexFor(p), it);
                    }
                }

                // Remove items whose process(es) have since exited / renamed.
                var gone = new List<string>();
                foreach (string k in _itemsByKey.Keys) if (!liveKeys.Contains(k)) gone.Add(k);
                foreach (string k in gone)
                {
                    if (_itemsByKey.TryGetValue(k, out var dead)) _lstProcesses.Items.Remove(dead);
                    _itemsByKey.Remove(k);
                    _snapByKey.Remove(k);
                }

                if (selectedKey != null && _itemsByKey.TryGetValue(selectedKey, out var sel))
                {
                    if (!sel.Selected) sel.Selected = true;
                }

                _statusBar.Text = $"{procs.Count} 项  |  " + (_groupByName ? "按名称分组" : "按进程") +
                    $"  |  采样间隔 1s  |  历史窗口 {ProcessHistory.Capacity} 秒";
            }
            finally
            {
                _lstProcesses.EndUpdate();
            }

            if (selectedKey == null || !_itemsByKey.ContainsKey(selectedKey))
            {
                _cpuChart.ClearData();
                _memChart.ClearData();
                _cpuChart.Title = "CPU 占用历史";
                _memChart.Title = "内存占用历史";
                SelectFirst();
            }
            if (_lstProcesses.SelectedItems.Count > 0)
            {
                UpdateCharts();
            }
        }

        private string KeyOf(ProcessSnapshot p) => _groupByName ? "N|" + p.Name.ToLowerInvariant() : "P|" + p.Id;

        private int CountOf(ProcessSnapshot p)
        {
            // Count instances sharing this name in grouped mode.
            if (!_groupByName) return 1;
            int n = 0;
            foreach (var pp in _sampler.GetProcesses())
                if (string.Equals(pp.Name, p.Name, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private void SelectFirst()
        {
            if (_lstProcesses.Items.Count == 0) return;
            _lstProcesses.Items[0].Selected = true;
        }

        /// <summary>Find the insertion index for a new item per the current sort column/order.</summary>
        private int InsertIndexFor(ProcessSnapshot p)
        {
            int lo = 0, hi = _lstProcesses.Items.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var it = _lstProcesses.Items[mid];
                int cmp = 0;
                if (_snapByKey.TryGetValue((string)it.Tag, out var s)) cmp = CompareSnapshots(s, p);
                else cmp = string.CompareOrdinal(it.Text, p.Name);
                if (cmp <= 0) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private int CompareSnapshots(ProcessSnapshot a, ProcessSnapshot b)
        {
            int r;
            switch (_sortColumn)
            {
                case 1: r = a.Id.CompareTo(b.Id); break;
                case 2: r = a.CpuPercent.CompareTo(b.CpuPercent); break;
                case 3: r = a.WorkingSet.CompareTo(b.WorkingSet); break;
                default: r = string.CompareOrdinal(a.Name, b.Name); break;
            }
            return _sortAscending ? r : -r;
        }

        /// <summary>Refresh column header text, appending an up/down arrow to the active sort column.</summary>
        private void UpdateColumnHeaders()
        {
            if (_colTitles == null || _lstProcesses.Columns.Count != _colTitles.Length) return;
            for (int i = 0; i < _colTitles.Length; i++)
            {
                string t = _colTitles[i];
                if (i == _sortColumn) t += _sortAscending ? "  ▲" : "  ▼";
                _lstProcesses.Columns[i].Text = t;
            }
        }

        private void UpdateCharts()
        {
            if (_lstProcesses.SelectedItems.Count == 0) return;
            string key = (string)_lstProcesses.SelectedItems[0].Tag;
            if (string.IsNullOrEmpty(key)) return;
            int pipe = key.IndexOf('|');
            if (pipe < 0) return;
            string payload = key.Substring(pipe + 1);

            ProcessHistory h = null;
            bool ok = _groupByName
                ? _sampler.TryGetHistoryByName(FindGroupedName(payload), out h)
                : (int.TryParse(payload, out int pid) && _sampler.TryGetHistory(pid, out h));
            if (ok && h != null)
            {
                string label = _groupByName
                    ? $"名称 {h.Name}   (合计)"
                    : $"{h.Name}  (PID {h.Id})";
                _cpuChart.Title = $"CPU 占用历史  -  {label}";
                _memChart.Title = $"内存占用历史  -  {label}";
                _cpuChart.SetData(h);
                _memChart.SetData(h);
            }
        }

        private string FindGroupedName(string lowerName)
        {
            foreach (var pp in _sampler.GetProcessesGrouped())
                if (pp.Name.ToLowerInvariant() == lowerName) return pp.Name;
            return lowerName;
        }

        /// <summary>
        /// Sort the visible rows ourselves (reassemble the ListView item order) instead of
        /// relying on ListView.Sort(), whose native impl fails to reverse direction on the
        /// second click of the same column.
        /// </summary>
        private void ApplySort()
        {
            var keys = new List<string>(_snapByKey.Keys);
            // CompareSnapshots already applies _sortAscending, so do NOT invert here.
            keys.Sort((x, y) =>
            {
                _snapByKey.TryGetValue(x, out var a);
                _snapByKey.TryGetValue(y, out var b);
                return CompareSnapshots(a, b);
            });

            _lstProcesses.BeginUpdate();
            try { _lstProcesses.Items.Clear(); }
            finally { }
            try
            {
                foreach (string k in keys)
                    if (_itemsByKey.TryGetValue(k, out var it))
                        _lstProcesses.Items.Add(it);
            }
            finally { _lstProcesses.EndUpdate(); }

            UpdateCharts();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _uiTimer.Stop();
            _sampler.SamplesUpdated -= OnSamplesUpdated;
            _sampler.Dispose();
            base.OnFormClosing(e);
        }
    }
}