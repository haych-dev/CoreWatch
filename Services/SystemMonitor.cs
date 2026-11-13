using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CoreWatch.Services;

public sealed record DriveSample(string Name, string Label, string Format, long Total, long Free, DriveType Type)
{
    public long Used => Total - Free;
    public double UsedPct => Total > 0 ? 100.0 * Used / Total : 0;
}

public sealed class ProcSample
{
    public int Pid, ParentPid, Threads, Handles;
    public string Name = "";
    public double Cpu;
    public long PrivateWs, WorkingSet, Commit, GpuMem;
    public long CpuTime;
}

public sealed class Snapshot
{
    public double CpuTotal;
    public double[] CpuCores = [];
    public double CpuMhz;

    public ulong MemTotal, MemAvail, CommitTotal, CommitLimit, Cached, KernelPaged, KernelNonpaged;
    public ulong Standby, Modified, FreeZero;
    public bool HasMemLists;
    public double PageFilePct = -1;
    public int ProcessCount, ThreadCount, HandleCount;
    public ulong MemUsed => MemTotal - MemAvail;
    public double MemPct => MemTotal > 0 ? 100.0 * MemUsed / MemTotal : 0;

    public double NetDown, NetUp, DiskRead, DiskWrite, DiskActive;
    public string NetAdapter = "", NetAddress = "";
    public long NetLinkSpeed;

    public List<GpuSample> Gpus = [];
    public List<DriveSample> Drives = [];
    public List<ProcSample>? Processes;
    public TimeSpan Uptime;

    /// <summary>The GPU most people mean by "the GPU": the one with the most dedicated memory.</summary>
    public GpuSample? PrimaryGpu => Gpus.Count == 0 ? null : Gpus.MaxBy(g => g.DedicatedTotal);
}

public sealed class SystemInfo
{
    public string MachineName = Environment.MachineName;
    public string CpuName = "Processor";
    public int LogicalCores = Environment.ProcessorCount;
    public int PhysicalCores;
    public double BaseMhz;
    public string OsName = "Windows";
    public string OsVersion = "";
    public string Board = "";
    public string Bios = "";
    public ulong InstalledRam;
}

public sealed class History
{
    public const int Size = 120;
    public RingBuffer Cpu = new(Size), Mem = new(Size), NetDown = new(Size), NetUp = new(Size), DiskRead = new(Size), DiskWrite = new(Size);
    readonly Dictionary<string, (RingBuffer Util, RingBuffer Vram)> _gpu = new(StringComparer.OrdinalIgnoreCase);

    public (RingBuffer Util, RingBuffer Vram) Gpu(string luid)
    {
        if (!_gpu.TryGetValue(luid, out var h)) _gpu[luid] = h = (new RingBuffer(Size), new RingBuffer(Size));
        return h;
    }
}

/// <summary>Samples CPU, memory, GPU, disk and network on a background thread and publishes snapshots on the UI thread.</summary>
public sealed class SystemMonitor
{
    public static SystemMonitor Instance { get; } = new();

    public event Action<Snapshot>? Updated;
    public Snapshot? Latest { get; private set; }
    public History History { get; } = new();
    public SystemInfo Info { get; } = new();
    public volatile bool WantProcesses;

    readonly AutoResetEvent _wake = new(false);
    readonly GpuMonitor _gpu = new();
    Thread? _thread;
    volatile bool _stop;
    Dispatcher? _dispatcher;

    // CPU state
    long[] _prevIdle = [], _prevTotal = [];
    IntPtr _cpuBuf;
    PerformanceCounter? _cpuPerf;

    // Counters
    PerformanceCounter? _diskRead, _diskWrite, _diskIdle, _memModified, _memStandbyReserve, _memStandbyNormal, _memStandbyCore, _memFree, _pageFile;
    bool _countersReady;

    // Network state
    long _prevRx, _prevTx;
    bool _netPrimed;
    int _tick;
    string _netName = "", _netAddr = "";
    long _netSpeed;
    List<DriveSample> _drives = [];

    // Process state
    Dictionary<int, long> _prevProcCpu = new();
    long _prevProcStamp;

    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastStamp;

    public void Start(Dispatcher dispatcher)
    {
        if (_thread != null) return;
        _dispatcher = dispatcher;
        LoadStaticInfo();
        _thread = new Thread(Loop) { IsBackground = true, Name = "CoreWatch.Monitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        _gpu.Dispose();
    }

    /// <summary>Wakes the sampler early (used after settings changes or page switches).</summary>
    public void Poke() => _wake.Set();

    void Loop()
    {
        _gpu.Initialize();
        InitCounters();
        _lastStamp = _clock.ElapsedMilliseconds;
        Sample(); // prime deltas
        Thread.Sleep(250);

        while (!_stop)
        {
            try
            {
                var snap = Sample();
                _dispatcher?.BeginInvoke(DispatcherPriority.Background, () => Publish(snap));
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
            _wake.WaitOne(Math.Clamp(Settings.Current.RefreshMs, 250, 10000));
        }
    }

    void Publish(Snapshot s)
    {
        Latest = s;
        History.Cpu.Push(s.CpuTotal);
        History.Mem.Push(s.MemPct);
        History.NetDown.Push(s.NetDown);
        History.NetUp.Push(s.NetUp);
        History.DiskRead.Push(s.DiskRead);
        History.DiskWrite.Push(s.DiskWrite);
        foreach (var g in s.Gpus)
        {
            var h = History.Gpu(g.Luid);
            h.Util.Push(g.Utilization);
            h.Vram.Push(g.DedicatedPct);
        }
        Updated?.Invoke(s);
    }

    void LoadStaticInfo()
    {
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            Info.CpuName = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? Info.CpuName;
            Info.BaseMhz = Convert.ToDouble(cpu?.GetValue("~MHz") ?? 0);
        }
        catch { }
        try
        {
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = os?.GetValue("ProductName") as string ?? "Windows";
            var build = os?.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = os?.GetValue("UBR")?.ToString() ?? "";
            var display = os?.GetValue("DisplayVersion") as string ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            Info.OsName = name;
            Info.OsVersion = $"{display} (build {build}{(ubr.Length > 0 ? "." + ubr : "")})".Trim();
        }
        catch { }
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = bios?.GetValue("BaseBoardManufacturer") as string ?? "";
            var product = bios?.GetValue("BaseBoardProduct") as string ?? "";
            // "Gigabyte Technology Co., Ltd." -> "Gigabyte": the product name carries the useful detail
            string shortMaker = maker.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            Info.Board = $"{shortMaker} {product}".Trim();
            Info.Bios = $"{bios?.GetValue("BIOSVersion")} ({bios?.GetValue("BIOSReleaseDate")})";
        }
        catch { }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem)) Info.InstalledRam = mem.ullTotalPhys;
        if (Native.GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0) Info.InstalledRam = kb * 1024; // includes RAM reserved by hardware

        Info.PhysicalCores = Math.Max(1, Info.LogicalCores / 2);
        Task.Run(() =>
        {
            try
            {
                using var q = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
                int cores = 0;
                foreach (var o in q.Get()) cores += Convert.ToInt32(o["NumberOfCores"]);
                if (cores > 0) Info.PhysicalCores = cores;
            }
            catch { }
        });
    }

    static PerformanceCounter? TryCounter(string category, string counter, string? instance = null)
    {
        try
        {
            var c = instance == null ? new PerformanceCounter(category, counter, true) : new PerformanceCounter(category, counter, instance, true);
            c.NextValue();
            return c;
        }
        catch { return null; }
    }

    void InitCounters()
    {
        _cpuPerf = TryCounter("Processor Information", "% Processor Performance", "_Total");
        _diskRead = TryCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWrite = TryCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        _diskIdle = TryCounter("PhysicalDisk", "% Idle Time", "_Total");
        _memModified = TryCounter("Memory", "Modified Page List Bytes");
        _memStandbyReserve = TryCounter("Memory", "Standby Cache Reserve Bytes");
        _memStandbyNormal = TryCounter("Memory", "Standby Cache Normal Priority Bytes");
        _memStandbyCore = TryCounter("Memory", "Standby Cache Core Bytes");
        _memFree = TryCounter("Memory", "Free & Zero Page List Bytes");
        _pageFile = TryCounter("Paging File", "% Usage", "_Total");
        _countersReady = true;
    }

    static double Read(PerformanceCounter? c)
    {
        if (c == null) return 0;
        try { return c.NextValue(); } catch { return 0; }
    }

    Snapshot Sample()
    {
        long now = _clock.ElapsedMilliseconds;
        double dt = Math.Max(0.05, (now - _lastStamp) / 1000.0);
        _lastStamp = now;
        _tick++;

        var s = new Snapshot { Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) };
        SampleCpu(s);
        SampleMemory(s);
        SampleNetwork(s, dt);

        if (_countersReady)
        {
            s.DiskRead = Read(_diskRead);
            s.DiskWrite = Read(_diskWrite);
            s.DiskActive = _diskIdle == null ? 0 : Math.Clamp(100 - Read(_diskIdle), 0, 100);
        }

        if (_drives.Count == 0 || _tick % 5 == 0) _drives = ReadDrives();
        s.Drives = _drives;

        s.Gpus = _gpu.Sample(out var procGpuMem);
        if (WantProcesses) s.Processes = SampleProcesses(procGpuMem);
        return s;
    }

    void SampleCpu(Snapshot s)
    {
        int n = Environment.ProcessorCount;
        const int entry = 48;
        if (_cpuBuf == IntPtr.Zero)
        {
            _cpuBuf = Marshal.AllocHGlobal(entry * n);
            _prevIdle = new long[n];
            _prevTotal = new long[n];
        }

        s.CpuCores = new double[n];
        if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, _cpuBuf, entry * n, out int ret) != 0) return;
        int count = Math.Min(n, ret / entry);
        long sumIdle = 0, sumTotal = 0;
        for (int i = 0; i < count; i++)
        {
            long idle = Marshal.ReadInt64(_cpuBuf, i * entry);
            long kernel = Marshal.ReadInt64(_cpuBuf, i * entry + 8); // includes idle
            long user = Marshal.ReadInt64(_cpuBuf, i * entry + 16);
            long total = kernel + user;
            long dIdle = idle - _prevIdle[i], dTotal = total - _prevTotal[i];
            _prevIdle[i] = idle;
            _prevTotal[i] = total;
            s.CpuCores[i] = dTotal > 0 ? Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100) : 0;
            sumIdle += dIdle;
            sumTotal += dTotal;
        }
        s.CpuTotal = sumTotal > 0 ? Math.Clamp(100.0 * (sumTotal - sumIdle) / sumTotal, 0, 100) : 0;

        double perf = Read(_cpuPerf);
        s.CpuMhz = perf > 0 && Info.BaseMhz > 0 ? Info.BaseMhz * perf / 100.0 : Info.BaseMhz;
    }

    void SampleMemory(Snapshot s)
    {
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            s.MemTotal = mem.ullTotalPhys;
            s.MemAvail = mem.ullAvailPhys;
        }
        if (Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
        {
            ulong page = pi.PageSize;
            s.CommitTotal = pi.CommitTotal * page;
            s.CommitLimit = pi.CommitLimit * page;
            s.Cached = pi.SystemCache * page;
            s.KernelPaged = pi.KernelPaged * page;
            s.KernelNonpaged = pi.KernelNonpaged * page;
            s.ProcessCount = (int)pi.ProcessCount;
            s.ThreadCount = (int)pi.ThreadCount;
            s.HandleCount = (int)pi.HandleCount;
        }
        if (_countersReady && _memStandbyNormal != null)
        {
            s.Modified = (ulong)Read(_memModified);
            s.Standby = (ulong)(Read(_memStandbyReserve) + Read(_memStandbyNormal) + Read(_memStandbyCore));
            s.FreeZero = (ulong)Read(_memFree);
            s.HasMemLists = s.Standby + s.FreeZero > 0;
        }
        s.PageFilePct = _countersReady && _pageFile != null ? Math.Clamp(Read(_pageFile), 0, 100) : -1;
    }

    void SampleNetwork(Snapshot s, double dt)
    {
        try
        {
            long rx = 0, tx = 0;
            NetworkInterface? primary = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = nic.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
                if (primary == null && (_tick % 20 == 1 || _netName.Length == 0) && nic.GetIPProperties().GatewayAddresses.Count > 0) primary = nic;
            }
            if (primary != null)
            {
                _netName = primary.Name;
                _netSpeed = primary.Speed;
                _netAddr = primary.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
            }
            if (_netPrimed)
            {
                s.NetDown = Math.Max(0, (rx - _prevRx) / dt);
                s.NetUp = Math.Max(0, (tx - _prevTx) / dt);
            }
            _prevRx = rx;
            _prevTx = tx;
            _netPrimed = true;
        }
        catch { }
        s.NetAdapter = _netName;
        s.NetAddress = _netAddr;
        s.NetLinkSpeed = _netSpeed;
    }

    static List<DriveSample> ReadDrives()
    {
        var list = new List<DriveSample>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.Network) continue;
                list.Add(new DriveSample(d.Name, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace, d.DriveType));
            }
            catch { }
        }
        return list;
    }

    // SYSTEM_PROCESS_INFORMATION field offsets (x64)
    const int OffNext = 0, OffThreads = 4, OffPrivateWs = 8, OffUserTime = 40, OffKernelTime = 48, OffNameLen = 56, OffNameBuf = 64,
        OffPid = 80, OffParent = 88, OffHandles = 96, OffWorkingSet = 144, OffPrivatePages = 200;

    IntPtr _procBuf;
    int _procBufSize = 1 << 20;

    List<ProcSample> SampleProcesses(Dictionary<int, long> gpuMem)
    {
        var list = new List<ProcSample>(400);
        if (_procBuf == IntPtr.Zero) _procBuf = Marshal.AllocHGlobal(_procBufSize);

        int status;
        while ((status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _procBuf, _procBufSize, out int needed)) == Native.STATUS_INFO_LENGTH_MISMATCH)
        {
            _procBufSize = Math.Max(_procBufSize * 2, needed + (1 << 16));
            _procBuf = Marshal.ReAllocHGlobal(_procBuf, _procBufSize);
        }
        if (status != 0) return list;

        long stamp = _clock.ElapsedTicks;
        double elapsed100ns = _prevProcStamp == 0 ? 0 : (stamp - _prevProcStamp) * 1e7 / Stopwatch.Frequency;
        _prevProcStamp = stamp;
        int cores = Environment.ProcessorCount;
        var cpuNow = new Dictionary<int, long>(400);

        IntPtr p = _procBuf;
        while (true)
        {
            int pid = (int)Marshal.ReadInt64(p, OffPid);
            if (pid != 0)
            {
                int nameLen = (ushort)Marshal.ReadInt16(p, OffNameLen);
                IntPtr nameBuf = Marshal.ReadIntPtr(p, OffNameBuf);
                string name = nameBuf != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(nameBuf, nameLen / 2) : "System";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

                long cpuTime = Marshal.ReadInt64(p, OffUserTime) + Marshal.ReadInt64(p, OffKernelTime);
                cpuNow[pid] = cpuTime;
                double cpu = 0;
                if (elapsed100ns > 0 && _prevProcCpu.TryGetValue(pid, out long prev) && cpuTime >= prev)
                    cpu = Math.Clamp(100.0 * (cpuTime - prev) / (elapsed100ns * cores), 0, 100);

                gpuMem.TryGetValue(pid, out long gm);
                list.Add(new ProcSample
                {
                    Pid = pid,
                    ParentPid = (int)Marshal.ReadInt64(p, OffParent),
                    Name = name,
                    Threads = Marshal.ReadInt32(p, OffThreads),
                    Handles = Marshal.ReadInt32(p, OffHandles),
                    PrivateWs = Marshal.ReadInt64(p, OffPrivateWs),
                    WorkingSet = Marshal.ReadInt64(p, OffWorkingSet),
                    Commit = Marshal.ReadInt64(p, OffPrivatePages),
                    Cpu = cpu,
                    CpuTime = cpuTime,
                    GpuMem = gm,
                });
            }
            int next = Marshal.ReadInt32(p, OffNext);
            if (next == 0) break;
            p += next;
        }
        _prevProcCpu = cpuNow;
        return list;
    }
}
