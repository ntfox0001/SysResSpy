using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SysResSpy.Sampling
{
    /// <summary>A single sampled snapshot of one process.</summary>
    public sealed class ProcessSnapshot
    {
        public int Id;
        public string Name;
        public float CpuPercent;   // 0..100 (sum across cores view: % of a single core)
        public long WorkingSet;    // bytes
        public float CpuPeak;      // highest CpuPercent seen since process/group appeared
        public long WorkingSetPeak; // highest WorkingSet seen since process/group appeared
    }

    /// <summary>Ring-buffered history for one process.</summary>
    public sealed class ProcessHistory
    {
        public const int Capacity = 600; // max samples kept (600 * intervalMs)

        public int Id;
        public string Name;
        public readonly float[] Cpu = new float[Capacity];
        public readonly long[] WorkingSet = new long[Capacity];
        public readonly long[] Ticks = new long[Capacity]; // Environment.TickCount64 per sample
        public int Count;     // number of valid samples
        public int Head;      // index of next write
        public bool Active = true;
        public long StartTick = Environment.TickCount64;
        public float CpuPeak;
        public long WorkingSetPeak;
    }

    /// <summary>
    /// Low-overhead process monitor. Samples CPU from TotalProcessorTime deltas
    /// (no PerformanceCounter WMI hammering) and memory from WorkingSet64.
    /// Runs on a background timer so UI stays responsive.
    /// </summary>
    public sealed class ProcessSampler : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<int, ProcessHistory> _history = new Dictionary<int, ProcessHistory>();
        private readonly Dictionary<string, ProcessHistory> _historyByName = new Dictionary<string, ProcessHistory>();
        private readonly Dictionary<int, TimeSpan> _prevCpuTotal = new Dictionary<int, TimeSpan>();
        private readonly Dictionary<int, long> _prevWallMs = new Dictionary<int, long>();

        private Timer _timer;
        private int _intervalMs = 1000;
        private volatile bool _started;
        private long _lastTickMs;

        public event Action<ProcessSampler> SamplesUpdated;

        public int IntervalMs { get => _intervalMs; set { _intervalMs = Math.Max(250, value); } }

        public ProcessSampler()
        {
            _timer = new Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _lastTickMs = Environment.TickCount64;
            _timer.Change(0, _intervalMs);
        }

        public void Stop()
        {
            _started = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Dispose()
        {
            Stop();
            _timer.Dispose();
        }

        /// <summary>Reset history for every process (keeps the sampler running).</summary>
        public void ResetHistory()
        {
            lock (_lock)
            {
                _history.Clear();
                _prevCpuTotal.Clear();
                _prevWallMs.Clear();
                _lastTickMs = Environment.TickCount64;
            }
        }

        private void OnTick()
        {
            try { SampleOnce(); }
            catch { /* never let the timer die */ }
        }

        private void SampleOnce()
        {
            long nowMs = Environment.TickCount64;
            float dtSec = (nowMs - _lastTickMs) / 1000f;
            _lastTickMs = nowMs;
            if (dtSec <= 0) dtSec = 0.001f;

            int coreCount = Math.Max(1, Environment.ProcessorCount);

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return; }

            lock (_lock)
            {
                // Purge entries for processes that no longer exist to avoid stale memory.
                if (procs.Length != 0)
                {
                    var stale = new List<int>();
                    foreach (int pid in _history.Keys)
                    {
                        bool seen = false;
                        foreach (Process p in procs) { if (p.Id == pid) { seen = true; break; } }
                        if (!seen) stale.Add(pid);
                    }
                    foreach (int pid in stale) { _history.Remove(pid); _prevCpuTotal.Remove(pid); _prevWallMs.Remove(pid); }
                }

                // Per-name aggregation for this tick (sum CPU% and memory across
                // processes sharing the same name, e.g. multiple Battle.net.exe).
                var aggCpu = new Dictionary<string, float>();
                var aggWs = new Dictionary<string, long>();
                var aggPids = new Dictionary<string, int>();

                foreach (Process p in procs)
                {
                    try
                    {
                        int pid;
                        string name;
                        TimeSpan cpuTotal;
                        long ws;
                        using (p)
                        {
                            pid = p.Id;
                            name = p.ProcessName;
                            cpuTotal = p.TotalProcessorTime;
                            ws = p.WorkingSet64;
                        }

                        float cpuPct = 0f;
                        if (_prevCpuTotal.TryGetValue(pid, out TimeSpan prevTotal)
                            && _prevWallMs.TryGetValue(pid, out long prevWall))
                        {
                            TimeSpan delta = cpuTotal - prevTotal;
                            long wallDelta = nowMs - prevWall;
                            if (wallDelta > 0 && delta.Ticks >= 0)
                            {
                                // Percent of ONE core (100% means the process used
                                // one full core in the window; can exceed 100 for
                                // multi-threaded processes, matching Task Manager).
                                cpuPct = (float)(delta.TotalSeconds / (wallDelta / 1000.0) * 100.0);
                                if (cpuPct < 0) cpuPct = 0;
                                if (cpuPct > 100f * coreCount) cpuPct = 100f * coreCount; // safety clamp
                            }
                        }
                        // For the very first sample data is 0, which reads as "starting" — fine.
                        _prevCpuTotal[pid] = cpuTotal;
                        _prevWallMs[pid] = nowMs;

                        if (!_history.TryGetValue(pid, out ProcessHistory h))
                        {
                            h = new ProcessHistory { Id = pid, Name = name, StartTick = nowMs };
                            _history[pid] = h;
                        }
                        h.Name = name;
                        h.Active = true;
                        h.Cpu[h.Head] = cpuPct;
                        h.WorkingSet[h.Head] = ws;
                        h.Ticks[h.Head] = nowMs;
                        h.Head = (h.Head + 1) % ProcessHistory.Capacity;
                        if (h.Count < ProcessHistory.Capacity) h.Count++;
                        if (cpuPct > h.CpuPeak) h.CpuPeak = cpuPct;
                        if (ws > h.WorkingSetPeak) h.WorkingSetPeak = ws;

                        // Aggregate into name buckets.
                        if (aggCpu.TryGetValue(name, out float c)) aggCpu[name] = c + cpuPct; else aggCpu[name] = cpuPct;
                        if (aggWs.TryGetValue(name, out long m)) aggWs[name] = m + ws; else aggWs[name] = ws;
                        aggPids[name] = pid;
                    }
                    catch
                    {
                        // Process exited between enumeration and use, or access denied.
                    }
                }

                // Write one combined sample per active name to the by-name history.
                foreach (KeyValuePair<string, float> kv in aggCpu)
                {
                    string name = kv.Key;
                    if (!_historyByName.TryGetValue(name, out ProcessHistory nh))
                    {
                        nh = new ProcessHistory { Name = name, Id = aggPids[name], StartTick = nowMs };
                        _historyByName[name] = nh;
                    }
                    nh.Name = name;
                    nh.Active = true;
                    nh.Cpu[nh.Head] = kv.Value;
                    nh.WorkingSet[nh.Head] = aggWs[name];
                    nh.Ticks[nh.Head] = nowMs;
                    nh.Head = (nh.Head + 1) % ProcessHistory.Capacity;
                    if (nh.Count < ProcessHistory.Capacity) nh.Count++;
                    if (kv.Value > nh.CpuPeak) nh.CpuPeak = kv.Value;
                    if (aggWs[name] > nh.WorkingSetPeak) nh.WorkingSetPeak = aggWs[name];
                }
                // Remove by-name entries whose processes have all exited, and mark empty ones inactive.
                var deadNames = new List<string>();
                foreach (KeyValuePair<string, ProcessHistory> kv in _historyByName)
                {
                    if (!aggCpu.ContainsKey(kv.Key)) { deadNames.Add(kv.Key); }
                    else { kv.Value.Active = aggPids.ContainsKey(kv.Key); }
                }
                foreach (string name in deadNames) _historyByName.Remove(name);
            }

            SamplesUpdated?.Invoke(this);
        }

        /// <summary>Active processes with their latest sample, sorted by name.</summary>
        public List<ProcessSnapshot> GetProcesses()
        {
            var list = new List<ProcessSnapshot>();
            lock (_lock)
            {
                foreach (ProcessHistory h in _history.Values)
                {
                    int i = h.Count > 0 ? (h.Head - 1 + ProcessHistory.Capacity) % ProcessHistory.Capacity : 0;
                    list.Add(new ProcessSnapshot
                    {
                        Id = h.Id,
                        Name = h.Name,
                        CpuPercent = h.Cpu[i],
                        WorkingSet = h.WorkingSet[i],
                        CpuPeak = h.CpuPeak,
                        WorkingSetPeak = h.WorkingSetPeak
                    });
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>
        /// Returns a copy of a process's history (oldest first) so the UI thread
        /// can render without holding the lock.
        /// </summary>
        public bool TryGetHistory(int pid, out ProcessHistory clone)
        {
            clone = null;
            lock (_lock)
            {
                if (_history.TryGetValue(pid, out ProcessHistory h))
                {
                    clone = new ProcessHistory { Id = h.Id, Name = h.Name, Count = h.Count, Head = h.Head, Active = h.Active, StartTick = h.StartTick };
                    if (h.Count > 0)
                    {
                        // Deep-copy arrays; Head+Count are preserved so the renderer
                        // can walk oldest-first from the head pointer.
                        Array.Copy(h.Cpu, clone.Cpu, h.Cpu.Length);
                        Array.Copy(h.WorkingSet, clone.WorkingSet, h.WorkingSet.Length);
                        Array.Copy(h.Ticks, clone.Ticks, h.Ticks.Length);
                    }
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Processes grouped by name: CPU% and memory are summed across all
        /// processes sharing a name, so e.g. four Battle.net engines show as one.
        /// </summary>
        public List<ProcessSnapshot> GetProcessesGrouped()
        {
            var list = new List<ProcessSnapshot>();
            lock (_lock)
            {
                foreach (ProcessHistory h in _historyByName.Values)
                {
                    int i = h.Count > 0 ? (h.Head - 1 + ProcessHistory.Capacity) % ProcessHistory.Capacity : 0;
                    list.Add(new ProcessSnapshot
                    {
                        Id = h.Id,
                        Name = h.Name,
                        CpuPercent = h.Cpu[i],
                        WorkingSet = h.WorkingSet[i],
                        CpuPeak = h.CpuPeak,
                        WorkingSetPeak = h.WorkingSetPeak
                    });
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>Copy of a name-grouped history (oldest-first) for the UI thread.</summary>
        public bool TryGetHistoryByName(string name, out ProcessHistory clone)
        {
            clone = null;
            lock (_lock)
            {
                if (_historyByName.TryGetValue(name, out ProcessHistory h))
                {
                    clone = new ProcessHistory { Id = h.Id, Name = h.Name, Count = h.Count, Head = h.Head, Active = h.Active, StartTick = h.StartTick };
                    if (h.Count > 0)
                    {
                        Array.Copy(h.Cpu, clone.Cpu, h.Cpu.Length);
                        Array.Copy(h.WorkingSet, clone.WorkingSet, h.WorkingSet.Length);
                        Array.Copy(h.Ticks, clone.Ticks, h.Ticks.Length);
                    }
                    return true;
                }
                return false;
            }
        }
    }
}