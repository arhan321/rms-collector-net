using System.Diagnostics;
using System.Text;

namespace RmsCollector;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static void Write(string text) { lock (Sync) Console.WriteLine(text); }
}

internal static class CollectorRunner
{
    public static async Task RunRobotAsync(CollectorConfig config, Device device, CliOptions options, string token,
        CancellationToken cancellation, Func<Device, IRobotReader>? readerFactory = null)
    {
        using var reader = readerFactory?.Invoke(device) ?? (options.Mock ? (IRobotReader)new MockReader(device) : new YmConnectReader(device));
        using var tx = new Dispatcher(config, device, options.DryRun, token);
        string session = Guid.NewGuid().ToString();
        long sequence = 0;
        bool announced = false, failed = false;
        int badSamples = 0;
        TimeSpan nextOperating = TimeSpan.Zero, nextAlarm = TimeSpan.Zero, nextExtras = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        AppLog.Write($"[RMS] robot_id={device.Id} ip={device.Ip} {(options.Mock ? "MOCK" : "REAL")} started");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TimeSpan start = clock.Elapsed;
                if (!reader.Connected && !reader.Connect())
                {
                    if (options.Once) throw new IOException($"YMConnect could not connect to robot {device.Id} at {device.Ip}");
                    if (announced)
                    {
                        await tx.PostAsync(ApiPayloads.ConnectionEndpoint, ApiPayloads.Connection(device, ApiPayloads.Timestamp(), "offline", options.Mock), cancellation);
                        announced = false;
                    }
                    await tx.FlushAsync(cancellation);
                    await WaitUntilAsync(clock, start + TimeSpan.FromMilliseconds(config.TelemetryMs), cancellation);
                    continue;
                }
                TimeSpan now = clock.Elapsed;
                bool operating = now >= nextOperating, alarms = now >= nextAlarm, extras = now >= nextExtras;
                var sample = reader.Collect(sequence, extras, operating, alarms);
                bool good = sample.Position.Ok || sample.Torque.Ok;
                string at = ApiPayloads.Timestamp();
                if (good)
                {
                    badSamples = 0;
                    if (!announced)
                    {
                        await tx.PostAsync(ApiPayloads.ConnectionEndpoint, ApiPayloads.Connection(device, at, "connected", options.Mock), cancellation);
                        announced = true;
                    }
                }
                else if (++badSamples >= 3)
                {
                    if (announced) await tx.PostAsync(ApiPayloads.ConnectionEndpoint, ApiPayloads.Connection(device, at, "offline", options.Mock), cancellation);
                    announced = false;
                    reader.Disconnect();
                    badSamples = 0;
                    AppLog.Write($"[YMConnect {device.Id}] disconnected after 3 consecutive cycles with no position or torque data");
                }
                if (options.Once && !good) throw new IOException($"No position or torque read succeeded for robot {device.Id}");
                if (good) await tx.PostAsync(ApiPayloads.TelemetryEndpoint, ApiPayloads.Telemetry(device, session, sequence, at, sample, options.Mock), cancellation);
                if (operating)
                {
                    foreach (var counter in sample.OperatingReadings)
                        await tx.PostAsync(ApiPayloads.OperatingEndpoint, ApiPayloads.Operating(device, ApiPayloads.Timestamp(), counter, options.Mock), cancellation);
                    nextOperating = now + TimeSpan.FromSeconds(config.OperatingSeconds);
                }
                if (alarms)
                {
                    await tx.PostAsync(ApiPayloads.AlarmEndpoint, ApiPayloads.Alarms(device, ApiPayloads.Timestamp(), sample, options.Mock), cancellation);
                    nextAlarm = now + TimeSpan.FromSeconds(config.AlarmSeconds);
                }
                if (extras)
                {
                    nextExtras = now + TimeSpan.FromSeconds(config.ExtrasSeconds);
                    if (!sample.State.Ok || !sample.Job.Ok || !sample.Io.Ok)
                        AppLog.Write($"[SDK {device.Id}] extras: {sample.State.Error}; {sample.Job.Error}; {sample.Io.Error}");
                }
                await tx.FlushAsync(cancellation);
                if (options.Once) break;
                sequence++;
                await WaitUntilAsync(clock, start + TimeSpan.FromMilliseconds(config.TelemetryMs), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { failed = true; throw; }
        finally
        {
            if (announced && !options.Once && !failed)
            {
                // Graceful Ctrl+C should preserve a final stopped record even if the API is offline.
                try { await tx.PostAsync(ApiPayloads.ConnectionEndpoint, ApiPayloads.Connection(device, ApiPayloads.Timestamp(), "stopped", options.Mock), CancellationToken.None); }
                catch (Exception e) { AppLog.Write($"[STOP {device.Id}] {e.Message}"); }
            }
        }
    }

    private static Task WaitUntilAsync(Stopwatch clock, TimeSpan target, CancellationToken cancellation)
    {
        TimeSpan remaining = target - clock.Elapsed;
        return remaining > TimeSpan.Zero ? Task.Delay(remaining, cancellation) : Task.CompletedTask;
    }
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("rms_collector --config config/collector.conf --mock|--real [--once] [--dry-run] [--robot-id N] [--allow-mock-post]\n" +
                    "rms_collector --probe [--config config/collector.conf]\n" +
                    "rms_collector --check-sdk\n\n" +
                    "Mock POST is blocked unless explicitly allowed for a DEMO robot. REAL is read-only and requires Windows x64.\n" +
                    "Relative config/token/spool paths are resolved from your working directory. Run from the project/publish folder.");
                return 0;
            }
            if (options.CheckSdk)
            {
                YmConnectReader.CheckSdk();
                AppLog.Write("[SDK] Matching Windows x64 managed/native DLLs load successfully. No robot connection was made.");
                return 0;
            }
            var config = CollectorConfig.Load(options.ConfigPath);
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                if (options.Probe)
                {
                    using var api = new ApiClient(config, "");
                    var result = await api.SendAsync("/up", null, stop.Token);
                    AppLog.Write($"[PROBE] GET {config.ApiBase}/up => {result.Summary}");
                    return result.Error is null && result.Status is >= 200 and < 400 ? 0 : 1;
                }
                var devices = config.Devices.Where(d => options.RobotId == 0 || d.Id == options.RobotId).ToArray();
                if (devices.Length == 0) throw new ArgumentException("Selected robot_id not in config");
                if (options.Real) YmConnectReader.CheckSdk();
                string token = options.DryRun ? "" : config.ReadToken();
                var tasks = devices.Select(device => Task.Run(async () =>
                {
                    try { await CollectorRunner.RunRobotAsync(config, device, options, token, stop.Token); }
                    catch { stop.Cancel(); throw; }
                }, CancellationToken.None)).ToArray();
                await Task.WhenAll(tasks);
                AppLog.Write("[RMS] Done");
                return 0;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException) { AppLog.Write("[RMS] Stopped"); return 0; }
        catch (Exception e) { Console.Error.WriteLine($"[ERROR] {e.Message}"); return 1; }
    }
}
