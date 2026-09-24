using System.ComponentModel;

using SysResSpy.Sampling;

namespace SysResSpy.WinUI
{
    /// <summary>
    /// One row in the process list. Implements INotifyPropertyChanged so the ListView
    /// refreshes only the changed cells (CPU%/memory) while keeping the same item
    /// reference — which preserves scroll position and selection across updates.
    /// </summary>
    public sealed class ProcessRow : INotifyPropertyChanged
    {
        private string _name;
        private string _pidText;
        private float _cpu;
        private long _workingSet;
        private float _cpuPeak;
        private long _workingSetPeak;

        public string Key { get; }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
        }

        public string PidText
        {
            get => _pidText;
            set { if (_pidText != value) { _pidText = value; OnPropertyChanged(nameof(PidText)); } }
        }

        public string CpuText { get; private set; }

        public float Cpu
        {
            get => _cpu;
            set
            {
                if (_cpu != value)
                {
                    _cpu = value;
                    CpuText = value.ToString("0.0");
                    OnPropertyChanged(nameof(Cpu));
                    OnPropertyChanged(nameof(CpuText));
                }
            }
        }

        public string MemText { get; private set; }

        public long WorkingSet
        {
            get => _workingSet;
            set
            {
                if (_workingSet != value)
                {
                    _workingSet = value;
                    MemText = Format.Bytes(value);
                    OnPropertyChanged(nameof(WorkingSet));
                    OnPropertyChanged(nameof(MemText));
                }
            }
        }

        public ProcessRow(string key, ProcessSnapshot snap)
        {
            Key = key;
            CpuText = "0.0";
            MemText = "0 B";
            CpuPeakText = "0.0";
            MemPeakText = "0 B";
            Apply(snap);
        }

        public void Apply(ProcessSnapshot snap)
        {
            Name = snap.Name;
            Cpu = snap.CpuPercent;
            WorkingSet = snap.WorkingSet;
            CpuPeak = snap.CpuPeak;
            WorkingSetPeak = snap.WorkingSetPeak;
        }

        public float CpuPeak
        {
            get => _cpuPeak;
            set
            {
                if (_cpuPeak != value)
                {
                    _cpuPeak = value;
                    CpuPeakText = value.ToString("0.0");
                    OnPropertyChanged(nameof(CpuPeak));
                    OnPropertyChanged(nameof(CpuPeakText));
                }
            }
        }

        public string CpuPeakText { get; private set; }

        public long WorkingSetPeak
        {
            get => _workingSetPeak;
            set
            {
                if (_workingSetPeak != value)
                {
                    _workingSetPeak = value;
                    MemPeakText = Format.Bytes(value);
                    OnPropertyChanged(nameof(WorkingSetPeak));
                    OnPropertyChanged(nameof(MemPeakText));
                }
            }
        }

        public string MemPeakText { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}