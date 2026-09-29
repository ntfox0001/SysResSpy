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
        public float CpuPercent;     // 0..100 (% of a single core); 0 if the process is closed
        public long WorkingSet;      // bytes; 0 if the process is closed
        public float CpuPeak;        // peak within the configured peak window
        public long WorkingSetPeak;  // peak within the configured peak window
        public bool Active;          // false => process existed earlier but has exited (retained)
        public int Gen;              // generation for name grouping (resets on restart of the same name)
    }

    /// <summary>Ring-buffered history for one process.</summary>
    public sealed class ProcessHistory
    {
        public const int Capacity = 3600; // max samples kept (3600 * intervalMs = 1 hour)

        public int Id;
        public string Name;
        public int Gen;            // generation: same name restarted => +1, new independent entry
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
        private readonly Dictionary<string, int> _genByName = new Dictionary<string, int>(); // current live generation per name
        private readonly Dictionary<int, TimeSpan> _prevCpuTotal = new Dictionary<int, TimeSpan>();
        private readonly Dictionary<int, long> _prevWallMs = new Dictionary<int, long>();

        private Timer _timer;
        private int _intervalMs = 1000;
        private volatile bool _started;
        private long _lastTickMs;
        private long _peakWindowMs;   // 0 => unlimited (peak over entire retained history)

        public event Action<ProcessSampler> SamplesUpdated;

        public int IntervalMs { get => _intervalMs; set { _intervalMs = Math.Max(250, value); } }

        /// <summary>Window over which the CPU/memory peaks are computed. 0 = unlimited.</summary>
        public long PeakWindowMs
        {
            get => _peakWindowMs;
            set
            {
                var clamped = Math.Max(0L, value);
                lock (_lock) _peakWindowMs = clamped;
            }
        }

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

        /// <summary>Composite key for a named group at a given generation.</summary>
        private static string ByKey(string name, int gen) => name + "\u0001" + gen.ToString();

        /// <summary>
        /// Recompute the CPU/working-set peaks for <paramref name="h"/> by scanning
        /// its ring buffer within the current peak window (oldest allowed = now -
        /// PeakWindowMs, or all samples if the window is unlimited).
        /// </summary>
        private void UpdatePeak(ProcessHistory h, long nowMs)
        {
            long window = _peakWindowMs;
            long oldest = window > 0 ? nowMs - window : long.MinValue;
            float cpuMax = 0f;
            long wsMax = 0L;
            long allowed = oldest;
            int n = Math.Min(h.Count, ProcessHistory.Capacity);
            int idx = (h.Head - 1 + ProcessHistory.Capacity) % ProcessHistory.Capacity;
            // Ring buffer holds samples newest-to-oldest; walk back while within
            // the window. Since Count never exceeds Capacity, a full loop is safe.
            for (int k = 0; k < n; k++)
            {
                long t = h.Ticks[idx];
                if (t >= allowed)
                {
                    float c = h.Cpu[idx];
                    if (c > cpuMax) cpuMax = c;
                    long ws = h.WorkingSet[idx];
                    if (ws > wsMax) wsMax = ws;
                }
                idx = (idx - 1 + ProcessHistory.Capacity) % ProcessHistory.Capacity;
            }
            h.CpuPeak = cpuMax;
            h.WorkingSetPeak = wsMax;
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
                // Retain history for processes that no longer exist (mark them
                // inactive) so the UI can show a "(closed)" row. Only drop CPU
                // deltas we no longer need.
                if (procs.Length != 0)
                {
                    var present = new HashSet<int>();
                    foreach (Process p in procs) present.Add(p.Id);
                    foreach (int pid in _history.Keys)
                        if (!present.Contains(pid))
                        {
                            if (_history.TryGetValue(pid, out ProcessHistory hx)) hx.Active = false;
                            _prevCpuTotal.Remove(pid);
                            _prevWallMs.Remove(pid);
                        }
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
                        UpdatePeak(h, nowMs);

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
                foreach (string name in aggCpu.Keys)
                    if (!_genByName.ContainsKey(name)) _genByName[name] = 0;

                foreach (KeyValuePair<string, float> kv in aggCpu)
                {
                    string name = kv.Key;
                    int gen = _genByName[name];
                    string key = ByKey(name, gen);
                    if (!_historyByName.TryGetValue(key, out ProcessHistory nh))
                    {
                        nh = new ProcessHistory { Name = name, Gen = gen, Id = aggPids[name], StartTick = nowMs };
                        _historyByName[key] = nh;
                    }
                    nh.Name = name;
                    nh.Active = true;
                    nh.Cpu[nh.Head] = kv.Value;
                    nh.WorkingSet[nh.Head] = aggWs[name];
                    nh.Ticks[nh.Head] = nowMs;
                    nh.Head = (nh.Head + 1) % ProcessHistory.Capacity;
                    if (nh.Count < ProcessHistory.Capacity) nh.Count++;
                    UpdatePeak(nh, nowMs);
                    if (nh.Id != aggPids[name]) nh.Id = aggPids[name];
                }
                // Mark name-groups that lost all their processes as inactive AND bump
                // their generation, so the same name restarting later shows up as a
                // brand-new entry (fresh curve + peak) instead of resuming the old one.
                foreach (string name in new List<string>(_genByName.Keys))
                {
                    if (aggCpu.ContainsKey(name)) continue;
                    int gen = _genByName[name];
                    if (_historyByName.TryGetValue(ByKey(name, gen), out ProcessHistory hx))
                        hx.Active = false;
                    _genByName[name] = gen + 1;
                }
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
                    bool active = h.Active;
                    list.Add(new ProcessSnapshot
                    {
                        Id = h.Id,
                        Name = h.Name,
                        CpuPercent = active ? h.Cpu[i] : 0f,
                        WorkingSet = active ? h.WorkingSet[i] : 0L,
                        CpuPeak = h.CpuPeak,
                        WorkingSetPeak = h.WorkingSetPeak,
                        Active = active
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
                    bool active = h.Active;
                    list.Add(new ProcessSnapshot
                    {
                        Id = h.Id,
                        Name = h.Name,
                        CpuPercent = active ? h.Cpu[i] : 0f,
                        WorkingSet = active ? h.WorkingSet[i] : 0L,
                        CpuPeak = h.CpuPeak,
                        WorkingSetPeak = h.WorkingSetPeak,
                        Active = active,
                        Gen = h.Gen
                    });
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>Copy of a name-grouped history (oldest-first) for the UI thread.</summary>
        public bool TryGetHistoryByName(string name, int gen, out ProcessHistory clone)
        {
            clone = null;
            lock (_lock)
            {
                if (_historyByName.TryGetValue(ByKey(name, gen), out ProcessHistory h))
                {
                    clone = new ProcessHistory { Id = h.Id, Name = h.Name, Gen = h.Gen, Count = h.Count, Head = h.Head, Active = h.Active, StartTick = h.StartTick };
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