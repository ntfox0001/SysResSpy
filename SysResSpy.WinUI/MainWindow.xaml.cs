using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

using SysResSpy.Sampling;

namespace SysResSpy.WinUI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ProcessSampler _sampler = new ProcessSampler();
        private readonly DispatcherQueueTimer _uiTimer;
        private readonly Dictionary<string, ProcessRow> _rowsByKey = new Dictionary<string, ProcessRow>();
        private string _selectedKey;
        private int _sortColumn;      // 0=名称, 1=PID, 2=CPU%, 3=内存, 4=CPU峰值, 5=内存峰值
        private bool _sortAscending = true;
        private bool _groupByName = true;

        public MainWindow()
        {
            InitializeComponent();
            Title = "SysResSpy - 系统进程资源监控";
            if (AppWindow != null)
            {
                try { AppWindow.Resize(new SizeInt32(1280, 640)); } catch { }
            }

            CpuChart.Title = "CPU 占用历史";
            CpuChart.BytesMode = false;
            MemChart.Title = "内存占用历史";
            MemChart.BytesMode = true;

            _sampler.Start();

            _uiTimer = DispatcherQueue.CreateTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(400);
            _uiTimer.IsRepeating = true;
            _uiTimer.Tick += (s, e) => RefreshUi();
            _uiTimer.Start();

            // Enable grouping by default. Setting IsChecked here (after all members
            // are constructed) fires OnGroupToggled, which performs the initial refresh.
            GroupToggle.IsChecked = true;

            RefreshUi();
            UpdateHeaders();
        }

        private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        {
        }

        private void OnGroupToggled(object sender, RoutedEventArgs e)
        {
            _groupByName = GroupToggle.IsChecked == true;
            _rowsByKey.Clear();
            ProcessList.Items.Clear();
            RefreshUi();
        }

        private void OnSortName(object sender, RoutedEventArgs e) => SortBy(0);
        private void OnSortPid(object sender, RoutedEventArgs e) => SortBy(1);
        private void OnSortCpu(object sender, RoutedEventArgs e) => SortBy(2);
        private void OnSortMem(object sender, RoutedEventArgs e) => SortBy(3);
        private void OnSortCpuPeak(object sender, RoutedEventArgs e) => SortBy(4);
        private void OnSortMemPeak(object sender, RoutedEventArgs e) => SortBy(5);

        private void SortBy(int col)
        {
            if (col == _sortColumn) _sortAscending = !_sortAscending;
            else { _sortColumn = col; _sortAscending = false; } // first click: high -> low
            UpdateHeaders();
            ReorderItems();
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessList.SelectedItem is ProcessRow row)
                _selectedKey = row.Key;
            UpdateCharts();
        }

        private async void OnAskDoubao(object sender, RoutedEventArgs e)
        {
            var row = ProcessList.SelectedItem as ProcessRow;
            if (row == null)
            {
                StatusText.Text = "请先在列表中选择一个进程，再点“问豆包”。";
                return;
            }

            var procs = _groupByName ? _sampler.GetProcessesGrouped() : _sampler.GetProcesses();
            ProcessSnapshot snap = null;
            foreach (var p in procs)
                if (string.Equals(p.Name, row.Name, StringComparison.OrdinalIgnoreCase)) { snap = p; break; }

            var sb = new StringBuilder();
            if (snap != null)
            {
                sb.Append("请分析以下 Windows 进程的资源占用情况，告诉我它是否正常、可能是干什么的、若异常该如何处理：").AppendLine();
                sb.Append("  进程名：").Append(snap.Name).AppendLine();
                if (_groupByName) sb.AppendLine("  （已按名称分组，合并了同名进程的合计占用）");
                else sb.Append("  PID：").Append(snap.Id).AppendLine();
                sb.Append("  当前 CPU：").Append(snap.CpuPercent.ToString("0.0")).Append("%，CPU 峰值：").Append(snap.CpuPeak.ToString("0.0")).Append('%').AppendLine();
                sb.Append("  当前内存：").Append(Format.Bytes(snap.WorkingSet)).Append("，内存峰值：").Append(Format.Bytes(snap.WorkingSetPeak)).AppendLine();
                if (_groupByName)
                {
                    int n = 0;
                    foreach (var pp in _sampler.GetProcesses())
                        if (string.Equals(pp.Name, snap.Name, StringComparison.OrdinalIgnoreCase)) n++;
                    if (n > 1) sb.Append("  共 ").Append(n).Append(" 个实例").AppendLine();
                }
            }
            else
            {
                sb.Append("请分析以下 Windows 进程：").Append(row.Name).AppendLine();
                sb.Append("当前 CPU：").Append(row.Cpu.ToString("0.0")).Append("%，内存：").Append(Format.Bytes(row.WorkingSet)).AppendLine();
            }
            sb.AppendLine().Append("请用两三句话回答：这个进程大致是做什么的、占用是否正常、是否需要处理。");

            string prompt = sb.ToString();
            StatusText.Text = "正在连接豆包…";
            string result = await Task.Run(() => DoubaoHelper.SendToDoubao(prompt));
            StatusText.Text = result;
        }

        private void RefreshUi()
        {
            var procs = _groupByName ? _sampler.GetProcessesGrouped() : _sampler.GetProcesses();

            if (ProcessList.SelectedItem is ProcessRow cur)
                _selectedKey = cur.Key;

            var liveKeys = new HashSet<string>();
            foreach (ProcessSnapshot p in procs)
            {
                string key = KeyOf(p);
                liveKeys.Add(key);
                if (_rowsByKey.TryGetValue(key, out ProcessRow row))
                {
                    row.PidText = PidTextOf(p);
                    row.Apply(p);
                }
                else
                {
                    row = new ProcessRow(key, p) { PidText = PidTextOf(p) };
                    _rowsByKey[key] = row;
                    ProcessList.Items.Add(row);
                }
            }

            var gone = new List<string>();
            foreach (string k in _rowsByKey.Keys)
                if (!liveKeys.Contains(k)) gone.Add(k);
            foreach (string k in gone)
            {
                _rowsByKey.Remove(k);
                for (int i = ProcessList.Items.Count - 1; i >= 0; i--)
                    if (ProcessList.Items[i] is ProcessRow r && r.Key == k)
                        ProcessList.Items.RemoveAt(i);
            }

            if (_selectedKey != null)
            {
                foreach (object o in ProcessList.Items)
                    if (o is ProcessRow r && r.Key == _selectedKey) { ProcessList.SelectedItem = r; break; }
            }
            else if (ProcessList.Items.Count > 0)
                ProcessList.SelectedIndex = 0;

            UpdateCharts();

            string mode = _groupByName ? "按名称分组" : "按进程(PID)";
            StatusText.Text = $"{procs.Count} 项  |  {mode}   |  采样间隔 1s   |  历史窗口 {ProcessHistory.Capacity} 秒";
        }

        /// <summary>Apply the current sort order by moving rows within the ObservableCollection.</summary>
        private void ReorderItems()
        {
            // Capture current selection key so selection survives the reorder.
            if (ProcessList.SelectedItem is ProcessRow cur) _selectedKey = cur.Key;

            var rows = new List<ProcessRow>();
            foreach (object o in ProcessList.Items)
                if (o is ProcessRow r) rows.Add(r);
            rows.Sort((a, b) => CompareRows(a, b));

            // Best-effort in-place move so no items are destroyed (scroll intact).
            for (int i = 0; i < rows.Count; i++)
            {
                int curIdx = IndexOf(rows[i]);
                if (curIdx < 0 || curIdx == i) continue;
                ProcessList.Items.RemoveAt(curIdx);
                ProcessList.Items.Insert(i, rows[i]);
            }

            if (_selectedKey != null)
            {
                foreach (object o in ProcessList.Items)
                    if (o is ProcessRow r && r.Key == _selectedKey) { ProcessList.SelectedItem = r; break; }
            }
        }

        private int IndexOf(ProcessRow r)
        {
            for (int i = 0; i < ProcessList.Items.Count; i++)
                if (ProcessList.Items[i] is ProcessRow x && ReferenceEquals(x, r)) return i;
            return -1;
        }

        private int CompareRows(ProcessRow a, ProcessRow b)
        {
            int r;
            switch (_sortColumn)
            {
                case 1: r = string.CompareOrdinal(a.PidText, b.PidText); break;
                case 2: r = a.Cpu.CompareTo(b.Cpu); break;
                case 3: r = a.WorkingSet.CompareTo(b.WorkingSet); break;
                case 4: r = a.CpuPeak.CompareTo(b.CpuPeak); break;
                case 5: r = a.WorkingSetPeak.CompareTo(b.WorkingSetPeak); break;
                default: r = string.CompareOrdinal(a.Name, b.Name); break;
            }
            return _sortAscending ? r : -r;
        }

        private void UpdateHeaders()
        {
            TxtName.Text = "进程"  + (_sortColumn == 0 ? (_sortAscending ? "  ▲" : "  ▼") : "");
            TxtPid.Text  = "PID"   + (_sortColumn == 1 ? (_sortAscending ? "  ▲" : "  ▼") : "");
            TxtCpu.Text  = "CPU%"  + (_sortColumn == 2 ? (_sortAscending ? "  ▲" : "  ▼") : "");
            TxtMem.Text  = "内存"  + (_sortColumn == 3 ? (_sortAscending ? "  ▲" : "  ▼") : "");
            TxtCpuPeak.Text = "CPU峰值" + (_sortColumn == 4 ? (_sortAscending ? "  ▲" : "  ▼") : "");
            TxtMemPeak.Text = "内存峰值" + (_sortColumn == 5 ? (_sortAscending ? "  ▲" : "  ▼") : "");
        }

        private void UpdateCharts()
        {
            ProcessRow sel = ProcessList.SelectedItem as ProcessRow;
            if (sel == null)
            {
                CpuChart.ClearData();
                MemChart.ClearData();
                return;
            }

            ProcessHistory h = FetchHistory(sel);
            if (h != null && h.Count > 0)
            {
                string label = _groupByName ? $"名称 {h.Name} (合计)" : $"{h.Name}  (PID {h.Id})";
                CpuChart.Title = $"CPU 占用历史 - {label}";
                MemChart.Title = $"内存占用历史 - {label}";
                CpuChart.SetData(h);
                MemChart.SetData(h);
            }
            else
            {
                CpuChart.ClearData();
                MemChart.ClearData();
            }
        }

        private ProcessHistory FetchHistory(ProcessRow row)
        {
            string payload = PayloadOf(row.Key);
            if (_groupByName)
                return _sampler.TryGetHistoryByName(FindGroupedName(payload), out ProcessHistory h) ? h : null;
            else
                return int.TryParse(payload, out int pid) && _sampler.TryGetHistory(pid, out ProcessHistory hp) ? hp : null;
        }

        private string KeyOf(ProcessSnapshot p) => _groupByName ? "N|" + p.Name.ToLowerInvariant() : "P|" + p.Id;
        private string PayloadOf(string key) { int i = key.IndexOf('|'); return i < 0 ? key : key.Substring(i + 1); }

        private string PidTextOf(ProcessSnapshot p)
        {
            if (!_groupByName) return p.Id.ToString();
            int n = 0;
            foreach (ProcessSnapshot pp in _sampler.GetProcesses())
                if (string.Equals(pp.Name, p.Name, StringComparison.OrdinalIgnoreCase)) n++;
            return n > 1 ? "×" + n : p.Id.ToString();
        }

        private string FindGroupedName(string lowerName)
        {
            foreach (ProcessSnapshot pp in _sampler.GetProcessesGrouped())
                if (pp.Name.ToLowerInvariant() == lowerName) return pp.Name;
            return lowerName;
        }
    }
}