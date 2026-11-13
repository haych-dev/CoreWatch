using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CoreWatch.Services;

public sealed class GpuSample
{
    public string Name = "";
    public string Luid = "";
    public uint VendorId;
    public ulong DedicatedTotal, SharedTotal, DedicatedUsed, SharedUsed;
    public double Utilization;
    public double? Temperature, FanPercent, PowerWatts;
    public string? DriverVersion;

    public string Vendor => VendorId switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", 0x5143 => "Qualcomm", _ => "GPU" };
    public double DedicatedPct => DedicatedTotal > 0 ? Math.Clamp(100.0 * DedicatedUsed / DedicatedTotal, 0, 100) : 0;
    public bool IsDiscrete => DedicatedTotal >= (1UL << 30);
}

/// <summary>
/// GPU telemetry: adapters come from DXGI, live usage from the Windows "GPU *" performance counters
/// (the same source Task Manager uses), and NVIDIA sensors from a long-running nvidia-smi loop.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    sealed class Adapter
    {
        public string Name = "", Luid = "";
        public uint VendorId;
        public ulong DedicatedTotal, SharedTotal;
        public int NvidiaIndex = -1;
        public string? DriverVersion;
    }

    readonly List<Adapter> _adapters = [];
    PerformanceCounterCategory? _catAdapterMem, _catEngine, _catProcMem;
    Dictionary<string, CounterSample> _prevEngine = new();

    Process? _smi;
    readonly object _smiLock = new();
    readonly Dictionary<int, (double? Temp, double? Fan, double? Power, string? Driver)> _smiData = new();

    public void Initialize()
    {
        try
        {
            var iid = typeof(Native.IDXGIFactory1).GUID;
            if (Native.CreateDXGIFactory1(in iid, out var factory) == 0 && factory != null)
            {
                int nvidia = 0;
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0 && adapter != null; i++)
                {
                    if (adapter.GetDesc1(out var d) != 0) continue;
                    if ((d.Flags & 2) != 0) continue; // software adapter
                    var a = new Adapter
                    {
                        Name = d.Description.Trim(),
                        Luid = $"0x{d.LuidHigh:X8}_0x{d.LuidLow:X8}",
                        VendorId = d.VendorId,
                        DedicatedTotal = d.DedicatedVideoMemory,
                        SharedTotal = d.SharedSystemMemory,
                    };
                    if (a.VendorId == 0x10DE) a.NvidiaIndex = nvidia++;
                    if (!_adapters.Any(x => x.Luid == a.Luid)) _adapters.Add(a);
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("DXGI: " + ex.Message); }

        try { if (PerformanceCounterCategory.Exists("GPU Adapter Memory")) _catAdapterMem = new PerformanceCounterCategory("GPU Adapter Memory"); } catch { }
        try { if (PerformanceCounterCategory.Exists("GPU Engine")) _catEngine = new PerformanceCounterCategory("GPU Engine"); } catch { }
        try { if (PerformanceCounterCategory.Exists("GPU Process Memory")) _catProcMem = new PerformanceCounterCategory("GPU Process Memory"); } catch { }

        Task.Run(LoadDriverVersions);
        if (_adapters.Any(a => a.NvidiaIndex >= 0)) StartNvidiaSmi();
    }

    void LoadDriverVersions()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var o in q.Get())
            {
                var name = (o["Name"] as string)?.Trim();
                var a = _adapters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (a != null) a.DriverVersion ??= o["DriverVersion"] as string;
            }
        }
        catch { }
    }

    void StartNvidiaSmi()
    {
        try
        {
            string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) exe = "nvidia-smi";
            var psi = new ProcessStartInfo(exe, "--query-gpu=index,temperature.gpu,fan.speed,power.draw,driver_version --format=csv,noheader,nounits -l 2")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            _smi = Process.Start(psi);
            if (_smi == null) return;
            _smi.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var parts = e.Data.Split(',');
                if (parts.Length < 5 || !int.TryParse(parts[0].Trim(), out int idx)) return;
                lock (_smiLock) _smiData[idx] = (Num(parts[1]), Num(parts[2]), Num(parts[3]), parts[4].Trim());
            };
            _smi.BeginOutputReadLine();
        }
        catch (Exception ex) { Debug.WriteLine("nvidia-smi: " + ex.Message); }

        static double? Num(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static string? LuidOf(string instance)
    {
        int i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && instance.Length >= i + 26 ? instance.Substring(i + 5, 21) : null;
    }

    static int PidOf(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return -1;
        int end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out int pid) ? pid : -1;
    }

    public List<GpuSample> Sample(out Dictionary<int, long> processMemory)
    {
        processMemory = new Dictionary<int, long>();
        var dedicated = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var shared = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var util = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_catAdapterMem != null)
            {
                var data = _catAdapterMem.ReadCategory();
                Collect(data, "Dedicated Usage", dedicated);
                Collect(data, "Shared Usage", shared);
            }
        }
        catch { }

        try
        {
            if (_catEngine != null)
            {
                var data = _catEngine.ReadCategory();
                if (data.Contains("Utilization Percentage"))
                {
                    var now = new Dictionary<string, CounterSample>(512);
                    var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry e in data["Utilization Percentage"]!)
                    {
                        var inst = (InstanceData)e.Value!;
                        string name = inst.InstanceName;
                        now[name] = inst.Sample;
                        if (!_prevEngine.TryGetValue(name, out var prev)) continue;
                        float v;
                        try { v = CounterSample.Calculate(prev, inst.Sample); } catch { continue; }
                        if (v <= 0 || float.IsNaN(v)) continue;
                        // Key = adapter + engine; usage of one engine is the sum over all processes using it.
                        int engIdx = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                        var luid = LuidOf(name);
                        if (luid == null || engIdx < 0) continue;
                        string key = luid + "|" + name[engIdx..];
                        perEngine[key] = perEngine.GetValueOrDefault(key) + v;
                    }
                    _prevEngine = now;
                    foreach (var (key, v) in perEngine)
                    {
                        string luid = key[..21];
                        if (v > util.GetValueOrDefault(luid)) util[luid] = v; // busiest engine, like Task Manager
                    }
                }
            }
        }
        catch { }

        try
        {
            if (_catProcMem != null)
            {
                var data = _catProcMem.ReadCategory();
                if (data.Contains("Dedicated Usage"))
                {
                    foreach (DictionaryEntry e in data["Dedicated Usage"]!)
                    {
                        var inst = (InstanceData)e.Value!;
                        int pid = PidOf(inst.InstanceName);
                        if (pid > 0 && inst.RawValue > 0) processMemory[pid] = processMemory.GetValueOrDefault(pid) + inst.RawValue;
                    }
                }
            }
        }
        catch { }

        var result = new List<GpuSample>(_adapters.Count);
        foreach (var a in _adapters)
        {
            var g = new GpuSample
            {
                Name = a.Name,
                Luid = a.Luid,
                VendorId = a.VendorId,
                DedicatedTotal = a.DedicatedTotal,
                SharedTotal = a.SharedTotal,
                DedicatedUsed = dedicated.GetValueOrDefault(a.Luid),
                SharedUsed = shared.GetValueOrDefault(a.Luid),
                Utilization = Math.Clamp(util.GetValueOrDefault(a.Luid), 0, 100),
                DriverVersion = a.DriverVersion,
            };
            if (a.NvidiaIndex >= 0)
            {
                lock (_smiLock)
                {
                    if (_smiData.TryGetValue(a.NvidiaIndex, out var d))
                    {
                        g.Temperature = d.Temp;
                        g.FanPercent = d.Fan;
                        g.PowerWatts = d.Power;
                        g.DriverVersion = d.Driver ?? g.DriverVersion;
                    }
                }
            }
            result.Add(g);
        }
        return result;

        static void Collect(InstanceDataCollectionCollection data, string counter, Dictionary<string, ulong> target)
        {
            if (!data.Contains(counter)) return;
            foreach (DictionaryEntry e in data[counter]!)
            {
                var inst = (InstanceData)e.Value!;
                var luid = LuidOf(inst.InstanceName);
                if (luid != null) target[luid] = target.GetValueOrDefault(luid) + (ulong)Math.Max(0, inst.RawValue);
            }
        }
    }

    public void Dispose()
    {
        try { if (_smi is { HasExited: false }) _smi.Kill(); } catch { }
    }
}
