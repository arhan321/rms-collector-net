using System.Net;
using System.Text.Json;
using RmsCollector;

static class Tests
{
    private static int passed;
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task Throws(Func<Task> work, string message)
    {
        try { await work(); } catch { return; }
        throw new Exception("Expected failure: " + message);
    }
    static Task Throws(Action work, string message) => Throws(() => { work(); return Task.CompletedTask; }, message);
    static async Task Test(string name, Func<Task> work)
    {
        await work();
        Console.WriteLine("PASS " + name);
        passed++;
    }

    static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "rms-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CollectorConfig Config(string name = "default", string extra = "")
            {
                string path = Path.Combine(root, name + ".conf");
                File.WriteAllText(path, $"api_base=http://127.0.0.1:8080\nspool_dir={root.Replace('\\', '/')}/{name}\nrobot=1,192.168.255.1,R1,10;10010\n" + extra);
                return CollectorConfig.Load(path);
            }
            var config = Config();
            var device = config.Devices[0];
            using var mock = new MockReader(device);
            mock.Connect();
            var sample = mock.Collect(0, true, true, true);
            const string at = "2026-11-08T01:02:03.123Z";
            const string session = "1abcb350-fd84-45f5-9193-4cecb3180391";
            string telemetry = ApiPayloads.Telemetry(device, session, 0, at, sample, true);

            await Test("CLI blocks ambiguous modes and accidental mock POST", async () =>
            {
                await Throws(() => CliOptions.Parse(["--mock"]), "unapproved mock write");
                await Throws(() => CliOptions.Parse(["--mock", "--real", "--dry-run"]), "two modes");
                await Throws(() => CliOptions.Parse(["--robot-id"]), "missing option value");
                await Throws(() => CliOptions.Parse(["--mock", "--probe", "--dry-run"]), "probe with read mode");
                Assert(CliOptions.Parse(["--mock", "--dry-run"]).Mock, "mock dry run should work");
            });
            await Test("Config preserves DOJO format and validates identities and intervals", async () =>
            {
                Assert(device.Io.SequenceEqual([10, 10010]), "IO parse mismatch");
                await Throws(() => Config("duplicate", "robot=1,192.168.255.2,R1\n"), "duplicate robot");
                await Throws(() => Config("rate", "telemetry_ms=0\n"), "invalid polling interval");
                await Throws(() => Config("group", "robot=2,192.168.255.2,R2\n"), "unsupported group");
                Assert(Config("comment", "// sample comment\n").Devices.Count == 1, "legacy sample comment");
                await Throws(() => Config("unknown", "invalid_key=1\n"), "typo in config");
            });
            await Test("Telemetry matches Laravel fields, UTC, SLURBT and simulated evidence", () =>
            {
                using var j = JsonDocument.Parse(telemetry);
                var o = j.RootElement;
                Assert(o.GetProperty("collector_session_id").GetString() == session && o.GetProperty("sequence").GetInt64() == 0, "idempotency keys");
                Assert(o.GetProperty("sampled_at").GetString() == at, "timezone timestamp");
                Assert(o.GetProperty("position_pulses").EnumerateObject().Select(x => x.Name).SequenceEqual(["S", "L", "U", "R", "B", "T"]), "axes");
                Assert(o.GetProperty("torque_unit").GetString() == "sdk_raw", "no invented torque conversion");
                var raw = o.GetProperty("raw_payload");
                Assert(raw.GetProperty("simulated").GetBoolean(), "mock must be labeled");
                Assert(!raw.GetProperty("controller_state").GetProperty("servo_on").GetBoolean(), "servo state");
                return Task.CompletedTask;
            });
            await Test("Missing reads stay null and zero alarms stay zero", () =>
            {
                sample.Position = new(false, 99, "failed");
                using var t = JsonDocument.Parse(ApiPayloads.Telemetry(device, session, 0, at, sample, true));
                Assert(t.RootElement.GetProperty("position_pulses").ValueKind == JsonValueKind.Null, "missing pulses became zero");
                using var a = JsonDocument.Parse(ApiPayloads.Alarms(device, at, sample, true));
                Assert(a.RootElement.GetProperty("active_alarm_count").GetInt32() == 0, "zero count lost");
                sample.Position = new(true, 0, "");
                return Task.CompletedTask;
            });
            await Test("All four operating counters preserve integer seconds and diagnostic text", () =>
            {
                Assert(sample.OperatingReadings.Count == 4, "missing counters");
                foreach (var op in sample.OperatingReadings)
                {
                    using var j = JsonDocument.Parse(ApiPayloads.Operating(device, at, op, true));
                    Assert(j.RootElement.GetProperty("value_seconds").GetInt64() == op.Seconds, "integer counter");
                    Assert(j.RootElement.GetProperty("value_raw").GetString()!.Contains("Hours :"), "backend parser input");
                }
                return Task.CompletedTask;
            });
            await Test("UTF8 JSON preserves Japanese text and strips embedded NUL", () =>
            {
                using var j = JsonDocument.Parse(ApiPayloads.Json(new { text = ApiPayloads.Text("警報\0ロボット") }));
                Assert(j.RootElement.GetProperty("text").GetString() == "警報ロボット", "SDK text corrupt");
                return Task.CompletedTask;
            });
            await Test("Outage queues, backlog stays FIFO, replay sends the exact body once", async () =>
            {
                var c = Config("offline");
                var requests = new List<string>();
                int status = 503;
                using var tx = new Dispatcher(c, device, false, "test-token", new Handler(async (req, ct) =>
                {
                    Assert(req.Headers.Authorization?.ToString() == "Bearer test-token", "authentication header");
                    requests.Add(await req.Content!.ReadAsStringAsync(ct));
                    return new((HttpStatusCode)status) { Content = new StringContent("{}") };
                }));
                await tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, default);
                string second = ApiPayloads.Telemetry(device, session, 1, at, sample, true);
                await tx.PostAsync(ApiPayloads.TelemetryEndpoint, second, default);
                Assert(requests.Count == 1 && Directory.GetFiles(tx.QueueDirectory, "*.queue").Length == 2, "backlog overtaken");
                status = 201;
                await tx.FlushAsync(default);
                Assert(requests.Skip(1).SequenceEqual([telemetry, second]), "out of order or rewritten payload");
                Assert(Directory.GetFiles(tx.QueueDirectory, "*.queue").Length == 0, "acknowledged queue retained");
            });
            await Test("Permanent POST rejection is preserved and stops collection", async () =>
            {
                using var tx = new Dispatcher(Config("rejected"), device, false, "t", Handler.Status(401));
                await Throws(() => tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, default), "permanent rejection");
                Assert(Directory.GetFiles(Path.Combine(tx.QueueDirectory, "rejected"), "*.queue").Length == 1, "rejected body lost");
            });
            await Test("Legacy two-line queue replays and quarantines old HTTP422 payloads", async () =>
            {
                using var tx = new Dispatcher(Config("legacy"), device, false, "t", Handler.Status(422));
                string path = Path.Combine(tx.QueueDirectory, "1792553210411295_0.queue");
                File.WriteAllText(path, ApiPayloads.TelemetryEndpoint + "\n" + telemetry + "\n");
                await tx.FlushAsync(default);
                Assert(!File.Exists(path) && File.Exists(Path.Combine(tx.QueueDirectory, "rejected", Path.GetFileName(path))), "legacy quarantine failed");
            });
            await Test("Corrupt or cross-robot queues never reach HTTP", async () =>
            {
                int sent = 0;
                using var tx = new Dispatcher(Config("invalid"), device, false, "t", new Handler((req, ct) =>
                { sent++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
                string path = Path.Combine(tx.QueueDirectory, "invalid.queue");
                File.WriteAllText(path, "http://other-server/unsafe\n" + telemetry + "\n");
                await Throws(() => tx.FlushAsync(default), "unsafe endpoint");
                File.WriteAllText(path, ApiPayloads.TelemetryEndpoint + "\n{\"robot_id\":2}\n");
                await Throws(() => tx.FlushAsync(default), "wrong robot");
                Assert(sent == 0 && File.Exists(path), "invalid queue sent or discarded");
            });
            await Test("Cancellation retains a request for replay and queue is single-owner", async () =>
            {
                var c = Config("cancel");
                using var tx = new Dispatcher(c, device, false, "t", new Handler((req, ct) => Task.FromCanceled<HttpResponseMessage>(ct)));
                await Throws(() => { using var other = new Dispatcher(c, device, false, "t"); }, "two collectors sharing spool");
                using var stop = new CancellationTokenSource(); stop.Cancel();
                await Throws(() => tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, stop.Token), "cancelled post");
                Assert(Directory.GetFiles(tx.QueueDirectory, "*.queue").Length == 1, "cancelled sample lost");
            });
            await Test("Queue capacity stops instead of deleting old samples", async () =>
            {
                using var tx = new Dispatcher(Config("full", "spool_max=100\n"), device, false, "t", Handler.Status(503));
                for (int i = 0; i < 100; i++) await tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, default);
                await Throws(() => tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, default), "full queue");
                Assert(Directory.GetFiles(tx.QueueDirectory, "*.queue").Length == 100, "old samples were dropped");
            });
            await Test("Dry-run never creates a spool or invokes HTTP", async () =>
            {
                var c = Config("dry");
                using var tx = new Dispatcher(c, device, true, "", new Handler((req, ct) => throw new Exception("network in dry-run")));
                await tx.PostAsync(ApiPayloads.TelemetryEndpoint, telemetry, default);
                await tx.FlushAsync(default);
                Assert(!Directory.Exists(c.SpoolDirectory), "dry-run wrote to disk");
            });
            await Test("Three bad reads disconnect and next connection recovers", async () =>
            {
                var reader = new FlakyReader();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                var opts = CliOptions.Parse(["--mock", "--dry-run"]);
                await CollectorRunner.RunRobotAsync(Config("reconnect", "telemetry_ms=1000\n"), device, opts, "", stop.Token, _ => reader);
                Assert(reader.Connections >= 2 && reader.Disconnects >= 1 && reader.SawRecovery, "reconnection state machine");
            });
            Console.WriteLine($"PASS {passed} tests");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
        public static Handler Status(int code) => new((req, ct) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("{}") }));
    }
    private sealed class FlakyReader : IRobotReader
    {
        private int cycle;
        public int Connections, Disconnects;
        public bool SawRecovery;
        public bool Connected { get; private set; }
        public bool Connect() { Connections++; return Connected = true; }
        public void Disconnect() { Disconnects++; Connected = false; }
        public void Dispose() => Disconnect();
        public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
        {
            cycle++;
            if (cycle is >= 2 and <= 4) return new RobotSample();
            if (cycle > 4) SawRecovery = true;
            return new() { Position = new(true, 0, "") };
        }
    }
}
