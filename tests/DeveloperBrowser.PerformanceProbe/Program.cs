using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (args.Length == 0 || !int.TryParse(args[0], out var targetId))
{
    Console.WriteLine("Usage: PerformanceProbe <DevBrowser PID> [duration seconds: 1200] [port: 8765]");
    Console.WriteLine("Records local diagnostics only. Does not click, navigate, or modify DevBrowser.");
    return;
}
var duration = args.Length > 1 ? int.Parse(args[1]) : 1200;
var port = args.Length > 2 ? int.Parse(args[2]) : 8765;
if (duration <= 0 || port is < 1024 or > 65535) throw new ArgumentException("Invalid duration or port.");
using var target = Process.GetProcessById(targetId);
var executable = target.MainModule?.FileName;
var output = Path.GetFullPath(Path.Combine("artifacts", "performance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + targetId));
Directory.CreateDirectory(output);
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
var requestCount = 0L;
var server = ServeAsync();
await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(new
{
    startedUtc = DateTime.UtcNow, targetId, executable,
    version = executable is null ? null : FileVersionInfo.GetVersionInfo(executable).ProductVersion,
    duration, port, processors = Environment.ProcessorCount, os = Environment.OSVersion.ToString(),
    note = "CPU percent is normalized to total machine logical processors. Window probe is WM_NULL, not click or renderer latency."
}, new JsonSerializerOptions { WriteIndented = true }));
using var samples = new StreamWriter(Path.Combine(output, "samples.csv"));
samples.WriteLine("utc,elapsed_seconds,phase,app_cpu_percent,children_cpu_percent,app_private_mb,children_private_mb,process_count,window_probe,window_probe_ms,served_requests,unreadable_processes");
var previous = new Dictionary<(int Id, long Start), (double Cpu, double Time)>();
var clock = Stopwatch.StartNew();
var failures = 0;
var readings = 0;
var exitReason = "duration completed";
Console.WriteLine($"Output: {output}\nOpen these in three DevBrowser tabs:");
foreach (var tab in new[] { "one", "two", "three" }) Console.WriteLine($"http://127.0.0.1:{port}/{tab}");
Console.WriteLine("Suggested phases: 0–2 min active/inspector closed; 2–5 active/inspector open; 5–15 idle; 15–20 resume.");
Console.WriteLine("Switch tabs, use sidebar items, and open/close tabs during active phases. Ctrl+C ends and saves the report.");
try
{
    while (!cancel.IsCancellationRequested)
    {
        target.Refresh();
        if (target.HasExited) { exitReason = "target process exited"; break; }
        var elapsed = clock.Elapsed.TotalSeconds;
        var phase = elapsed < 120 ? "active-closed" : elapsed < 300 ? "active-open" : elapsed < 900 ? "idle" : "resume";
        var ids = Native.Descendants(targetId);
        double appCpu = 0, childrenCpu = 0, appMemory = 0, childMemory = 0;
        var unavailable = 0;
        var seen = new HashSet<(int, long)>();
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                var key = (id, process.StartTime.ToUniversalTime().Ticks);
                var cpu = process.TotalProcessorTime.TotalSeconds;
                var memory = process.PrivateMemorySize64 / 1048576d;
                var percent = previous.TryGetValue(key, out var prior) && elapsed > prior.Time
                    ? Math.Max(0, cpu - prior.Cpu) / (elapsed - prior.Time) / Environment.ProcessorCount * 100 : 0;
                previous[key] = (cpu, elapsed);
                seen.Add(key);
                if (id == targetId) { appCpu = percent; appMemory = memory; }
                else { childrenCpu += percent; childMemory += memory; }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { unavailable++; }
        }
        foreach (var key in previous.Keys.Where(key => !seen.Contains(key)).ToArray()) previous.Remove(key);
        var window = target.MainWindowHandle;
        var probe = Stopwatch.StartNew();
        var state = window == IntPtr.Zero ? "no-window" : Native.SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 500, out _) == IntPtr.Zero ? "timeout-or-error" : "responding";
        probe.Stop();
        if (state == "timeout-or-error") failures++;
        readings++;
        samples.WriteLine(FormattableString.Invariant($"{DateTime.UtcNow:O},{elapsed:F2},{phase},{appCpu:F2},{childrenCpu:F2},{appMemory:F2},{childMemory:F2},{ids.Count},{state},{probe.Elapsed.TotalMilliseconds:F2},{Interlocked.Read(ref requestCount)},{unavailable}"));
        samples.Flush();
        Console.WriteLine($"{elapsed,6:F0}s {phase,-14} app {appCpu,5:F1}% / {appMemory,7:F0} MB; children {childrenCpu,5:F1}% / {childMemory,7:F0} MB; {state}");
        await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token);
    }
}
catch (OperationCanceledException) { exitReason = clock.Elapsed.TotalSeconds < duration - 1 ? "cancelled by user" : "duration completed"; }
finally
{
    cancel.Cancel();
    listener.Stop();
    await server;
    await File.WriteAllTextAsync(Path.Combine(output, "report.md"), $"""
        # DevBrowser performance observation

        - Executable: {executable}
        - Duration: {clock.Elapsed.TotalSeconds:F1} seconds
        - End reason: {exitReason}
        - Samples: {readings}
        - Window probes that timed out or failed: {failures}
        - Local HTTP requests served: {requestCount}

        Inspect `samples.csv` for application versus child-process CPU, private memory,
        and window responsiveness. Phase labels describe the suggested schedule, not
        verified user actions. The first observation of each process has no CPU baseline.
        Processes that exit between samples are not included in CPU totals.

        A successful window probe does not prove sidebar, new-tab, or page responsiveness.
        A failed probe is a symptom, not a root-cause diagnosis. No dumps or page contents
        are collected. Browser timer throttling may reduce background request rates.

        ## Operator observations

        Add exact elapsed time, tab, action, visible result, and estimated delay here.
        Record whether the inspector was open and whether the machine slept or locked.
        """);
    Console.WriteLine($"Saved {output}");
}

async Task ServeAsync()
{
    try
    {
        while (!cancel.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancel.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                // One request per connection; no persistent clients or unbounded request bodies.
                for (var count = 0; count < 64; count++)
                    if (string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) break;
                var path = line?.Split(' ').ElementAtOrDefault(1) ?? "/";
                Interlocked.Increment(ref requestCount);
                var api = path.StartsWith("/api/", StringComparison.Ordinal);
                var body = api ? "{\"ok\":true,\"payload\":\"" + new string('x', 2048) + "\"}" : Page;
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(api ? "application/json" : "text/html; charset=utf-8")}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        }
    }
    catch (Exception exception) when (cancel.IsCancellationRequested && exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
}

partial class Program
{
    private const string Page = """
        <!doctype html><html><meta charset="utf-8"><title>DevBrowser performance workload</title>
        <style>body{font:18px system-ui;max-width:760px;margin:60px auto;background:#17202d;color:#eef}button{padding:12px;margin:8px}pre{white-space:pre-wrap}</style>
        <h1>Local network workload</h1><p id="tab"></p>
        <p>This page makes one 2 KB request per second. Background browser throttling may slow it down.</p>
        <button onclick="burst()">Make 20 requests</button><button onclick="paused=!paused">Pause / resume polling</button>
        <p id="status"></p><pre id="result"></pre>
        <script>
        let completed=0,failed=0,paused=false;
        document.getElementById('tab').textContent='Tab: '+location.pathname;
        async function request(){try{const r=await fetch('/api/poll?tab='+encodeURIComponent(location.pathname)+'&n='+completed);await r.json();completed++}catch{failed++}
        document.getElementById('status').textContent=`Completed ${completed}; failed ${failed}; visibility ${document.visibilityState}`;}
        async function poll(){if(!paused)await request();setTimeout(poll,1000)}poll();
        async function burst(){for(let i=0;i<20;i++)await request();document.getElementById('result').textContent='Burst completed at '+new Date().toISOString()}
        </script></html>
        """;
}

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    internal static HashSet<int> Descendants(int root)
    {
        var result = new HashSet<int> { root };
        var parents = new List<(int Id, int Parent)>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = string.Empty };
            if (Process32FirstW(snapshot, ref entry))
                do { parents.Add(((int)entry.Id, (int)entry.Parent)); } while (Process32NextW(snapshot, ref entry));
            bool changed;
            do
            {
                changed = false;
                foreach (var process in parents)
                    if (result.Contains(process.Parent)) changed |= result.Add(process.Id);
            } while (changed);
            return result;
        }
        finally { CloseHandle(snapshot); }
    }
}
