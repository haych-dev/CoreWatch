using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CoreWatch.Services;

public sealed record AdapterInfo(string Id, string Name, string Description, string Kind, long Speed, string Mac,
    string IPv4, string IPv6, string Gateway, List<string> Dns, bool DnsAutomatic, bool HasGateway);

public sealed record ConnectionRow(string Process, int Pid, string Protocol, string Local, int LocalPort, string Remote, int RemotePort, string State)
{
    public string RemoteText => Remote.Contains(':') ? $"[{Remote}]:{RemotePort}" : $"{Remote}:{RemotePort}";
}

public sealed record ListenerRow(string Process, int Pid, string Protocol, int Port, string Address, string Scope);

public sealed record NetAppRow(string Process, int Connections, int Listening, string Remotes);

public sealed record DnsPreset(string Name, string[] Servers, string Note)
{
    public string Tip => $"{string.Join(" and ", Servers)}\n{Note}";
}

public static class NetworkService
{
    public static readonly DnsPreset[] DnsPresets =
    [
        new("Cloudflare", ["1.1.1.1", "1.0.0.1"], "Fast, with a no-logging policy"),
        new("Google", ["8.8.8.8", "8.8.4.4"], "Widely used and reliable"),
        new("Quad9", ["9.9.9.9", "149.112.112.112"], "Blocks known malicious sites"),
    ];

    /// <summary>What each adapter's DNS setting was before CoreWatch changed it.</summary>
    static TweakBackup DnsBackup { get; } = new(Path.Combine(Settings.Folder, "dns-backup.json"));

    // ───────── Adapters ─────────
    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var ip = nic.GetIPProperties();
                string v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
                string v6 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)?.Address.ToString() ?? "";
                string gateway = ip.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()
                                 ?? ip.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "";
                // Windows also lists packet filters and schedulers as adapters; only real ones have an address.
                if (v4.Length == 0 && v6.Length == 0 && gateway.Length == 0) continue;
                string mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                string kind = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                    NetworkInterfaceType.Ppp => "Dial-up or VPN",
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
                    _ => nic.NetworkInterfaceType.ToString(),
                };
                list.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, kind, nic.Speed, mac, v4, v6, gateway,
                    ip.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList(),
                    DnsIsAutomatic(nic.Id), gateway.Length > 0));
            }
            catch { }
        }
        return list.OrderByDescending(a => a.HasGateway).ThenBy(a => a.Name).ToList();
    }

    static bool DnsIsAutomatic(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            return string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch { return true; }
    }

    public static string SpeedText(long bitsPerSecond) => bitsPerSecond <= 0 ? "Unknown"
        : bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.#} Gbps" : $"{bitsPerSecond / 1e6:0.#} Mbps";

    // ───────── DNS switcher ─────────
    public static bool HasDnsBackup(AdapterInfo adapter) => DnsBackup.Peek(adapter.Id) != null;

    /// <summary>Sets the adapter's IPv4 DNS servers, or back to automatic when <paramref name="servers"/> is null. Returns an error message, or null.</summary>
    public static string? SetDns(AdapterInfo adapter, string[]? servers, bool remember = true)
    {
        try
        {
            if (remember) DnsBackup.Remember(adapter.Id, adapter.DnsAutomatic ? "auto" : string.Join(",", adapter.Dns));
            string name = adapter.Name.Replace("\"", "");
            if (servers == null || servers.Length == 0)
                Tool.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{name}\" source=dhcp", mustSucceed: true);
            else
            {
                Tool.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no", mustSucceed: true);
                for (int i = 1; i < servers.Length; i++)
                    Tool.Run("netsh.exe", $"interface ipv4 add dnsservers name=\"{name}\" address={servers[i]} index={i + 1} validate=no", mustSucceed: true);
            }
            DnsBackup.Save();
            try { Tool.Run("ipconfig.exe", "/flushdns"); } catch { }
            return null;
        }
        catch (UnauthorizedAccessException) { return "Windows refused the change. Restart CoreWatch as administrator and try again."; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Puts the DNS setting back to what it was before CoreWatch first changed it.</summary>
    public static string? RestoreDns(AdapterInfo adapter)
    {
        if (DnsBackup.Peek(adapter.Id) is not { } saved) return "There is nothing saved to restore.";
        string? error = SetDns(adapter, saved == "auto" ? null : saved.Split(',', StringSplitOptions.RemoveEmptyEntries), remember: false);
        if (error == null) { DnsBackup.Forget(adapter.Id); DnsBackup.Save(); }
        return error;
    }

    // ───────── Connections and listening ports ─────────
    const int AF_INET = 2, AF_INET6 = 23, TcpOwnerPidAll = 5, UdpOwnerPid = 1;

    static readonly string[] TcpStates =
        ["", "Closed", "Listening", "Connecting", "Connecting", "Connected", "Closing", "Closing", "Closing", "Closing", "Closing", "Closing", "Closing"];

    delegate uint TableCall(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    /// <summary>Calls one of the iphlpapi table functions and walks its fixed-size rows.</summary>
    static void ReadTable(TableCall call, int family, int tableClass, int rowSize, Action<IntPtr> row)
    {
        int size = 0;
        call(IntPtr.Zero, ref size, false, family, tableClass, 0);
        if (size <= 0) return;
        IntPtr buffer = Marshal.AllocHGlobal(size + 4096);
        try
        {
            size += 4096;
            if (call(buffer, ref size, false, family, tableClass, 0) != 0) return;
            int count = Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++) row(buffer + 4 + i * rowSize);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static int Port(IntPtr p, int offset) => (Marshal.ReadByte(p, offset) << 8) | Marshal.ReadByte(p, offset + 1);

    static string Address(IntPtr p, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(p + offset, bytes, 0, length);
        return new IPAddress(bytes).ToString();
    }

    static string Scope(string address) => address is "0.0.0.0" or "::" ? "Any network"
        : address is "127.0.0.1" or "::1" || address.StartsWith("127.") ? "This PC only" : address;

    public static (List<ConnectionRow> Connections, List<ListenerRow> Listeners) Sockets()
    {
        var names = new Dictionary<int, string> { [0] = "System Idle", [4] = "System" };
        foreach (var p in Process.GetProcesses())
        {
            try { names.TryAdd(p.Id, p.ProcessName); } catch { }
            p.Dispose();
        }
        string Name(int pid) => names.GetValueOrDefault(pid, $"PID {pid}");

        var connections = new List<ConnectionRow>();
        var listeners = new List<ListenerRow>();
        try
        {
            ReadTable(Native.GetExtendedTcpTable, AF_INET, TcpOwnerPidAll, 24, p =>
            {
                int state = Marshal.ReadInt32(p), pid = Marshal.ReadInt32(p, 20);
                string local = Address(p, 4, 4);
                if (state == 2) listeners.Add(new ListenerRow(Name(pid), pid, "TCP", Port(p, 8), local, Scope(local)));
                else connections.Add(new ConnectionRow(Name(pid), pid, "TCP", local, Port(p, 8), Address(p, 12, 4), Port(p, 16), TcpStates[Math.Clamp(state, 0, 12)]));
            });
            ReadTable(Native.GetExtendedTcpTable, AF_INET6, TcpOwnerPidAll, 56, p =>
            {
                int state = Marshal.ReadInt32(p, 48), pid = Marshal.ReadInt32(p, 52);
                string local = Address(p, 0, 16);
                if (state == 2) listeners.Add(new ListenerRow(Name(pid), pid, "TCP", Port(p, 20), local, Scope(local)));
                else connections.Add(new ConnectionRow(Name(pid), pid, "TCP", local, Port(p, 20), Address(p, 24, 16), Port(p, 44), TcpStates[Math.Clamp(state, 0, 12)]));
            });
            ReadTable(Native.GetExtendedUdpTable, AF_INET, UdpOwnerPid, 12, p =>
            {
                int pid = Marshal.ReadInt32(p, 8);
                string local = Address(p, 0, 4);
                listeners.Add(new ListenerRow(Name(pid), pid, "UDP", Port(p, 4), local, Scope(local)));
            });
            ReadTable(Native.GetExtendedUdpTable, AF_INET6, UdpOwnerPid, 28, p =>
            {
                int pid = Marshal.ReadInt32(p, 24);
                string local = Address(p, 0, 16);
                listeners.Add(new ListenerRow(Name(pid), pid, "UDP", Port(p, 20), local, Scope(local)));
            });
        }
        catch { }

        // The same port is usually open on both IPv4 and IPv6: show it once.
        listeners = listeners
            .GroupBy(l => (l.Pid, l.Protocol, l.Port, l.Scope is "Any network" or "This PC only" ? l.Scope : l.Address))
            .Select(g => g.First())
            .OrderBy(l => l.Port).ThenBy(l => l.Protocol).ToList();
        connections = connections.OrderBy(c => c.Process, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Remote).ToList();
        return (connections, listeners);
    }

    static bool IsLoopback(string address) => address is "::1" || address.StartsWith("127.");

    /// <summary>Per app: how many connections it holds, how many ports it listens on, and where it is connected to.</summary>
    public static List<NetAppRow> ByApp(List<ConnectionRow> connections, List<ListenerRow> listeners)
    {
        var rows = new List<NetAppRow>();
        var listening = listeners.GroupBy(l => l.Process, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in connections.Where(c => c.State == "Connected").GroupBy(c => c.Process, StringComparer.OrdinalIgnoreCase))
        {
            var remotes = group.Where(c => !IsLoopback(c.Remote)).Select(c => c.Remote).Distinct().ToList();
            string text = remotes.Count == 0 ? "Only talking to other apps on this PC"
                : string.Join(", ", remotes.Take(4)) + (remotes.Count > 4 ? $" and {remotes.Count - 4} more" : "");
            rows.Add(new NetAppRow(group.Key, group.Count(), listening.GetValueOrDefault(group.Key), text));
        }
        return rows.OrderByDescending(r => r.Connections).ToList();
    }
}
