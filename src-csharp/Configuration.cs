using System.Globalization;

namespace RmsCollector;

internal sealed record Device(int Id, string Ip, string Group, int[] Io);

internal sealed class CollectorConfig
{
    public string ApiBase { get; private set; } = "http://127.0.0.1:8080";
    public string TokenFile { get; private set; } = "./collector.token";
    public string SpoolDirectory { get; private set; } = "./spool";
    public int TelemetryMs { get; private set; } = 5000;
    public int OperatingSeconds { get; private set; } = 60;
    public int AlarmSeconds { get; private set; } = 30;
    public int ExtrasSeconds { get; private set; } = 10;
    public int HttpTimeoutSeconds { get; private set; } = 5;
    public int SpoolMax { get; private set; } = 20000;
    public List<Device> Devices { get; } = [];

    internal static int Integer(string key, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new InvalidDataException($"Invalid {key}: {value}");
        return n;
    }

    public static CollectorConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Missing config: {path}");
        var c = new CollectorConfig();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string source in File.ReadLines(path))
        {
            lineNumber++;
            string line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int equal = line.IndexOf('=');
            if (equal <= 0) throw new InvalidDataException($"Expected key=value at line {lineNumber}");
            string key = line[..equal].Trim(), value = line[(equal + 1)..].Trim();
            if (key != "robot" && !seenKeys.Add(key)) throw new InvalidDataException($"Duplicate config key: {key}");
            switch (key)
            {
                case "api_base": c.ApiBase = value.TrimEnd('/'); break;
                case "token_file": c.TokenFile = value; break;
                case "spool_dir": c.SpoolDirectory = value; break;
                case "telemetry_ms": c.TelemetryMs = Integer(key, value, 1000, 3600000); break;
                case "operating_every_s": c.OperatingSeconds = Integer(key, value, 5, 86400); break;
                case "alarms_every_s": c.AlarmSeconds = Integer(key, value, 5, 86400); break;
                case "extras_every_s": c.ExtrasSeconds = Integer(key, value, 5, 86400); break;
                case "http_timeout_s": c.HttpTimeoutSeconds = Integer(key, value, 1, 120); break;
                case "spool_max": c.SpoolMax = Integer(key, value, 100, 200000); break;
                case "robot": c.Devices.Add(ParseDevice(value)); break;
                default: throw new InvalidDataException($"Unknown config key: {key}");
            }
        }
        if (!Uri.TryCreate(c.ApiBase, UriKind.Absolute, out var api) ||
            (api.Scheme != "http" && api.Scheme != "https") || !string.IsNullOrEmpty(api.Query) ||
            !string.IsNullOrEmpty(api.Fragment) || !string.IsNullOrEmpty(api.UserInfo))
            throw new InvalidDataException("Use an http:// or https:// API base URL without credentials, query or fragment");
        if (string.IsNullOrWhiteSpace(c.TokenFile) || string.IsNullOrWhiteSpace(c.SpoolDirectory))
            throw new InvalidDataException("token_file and spool_dir must not be empty");
        if (c.Devices.Count is < 1 or > 8) throw new InvalidDataException("Configure 1 to 8 robots for this pilot; validate controller load before scaling");
        if (c.Devices.Select(d => d.Id).Distinct().Count() != c.Devices.Count ||
            c.Devices.Select(d => d.Ip).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Devices.Count)
            throw new InvalidDataException("Duplicate robot ID/IP");
        return c;
    }

    private static Device ParseDevice(string value)
    {
        string[] parts = value.Split(',').Select(s => s.Trim()).ToArray();
        if (parts.Length is < 2 or > 4) throw new InvalidDataException("robot requires id,ip[,group[,io;io]]");
        int id = Integer("robot_id", parts[0], 1, 1000000);
        if (parts[1].Length == 0) throw new InvalidDataException("Robot IP is empty");
        string group = parts.Length >= 3 ? parts[2] : "R1";
        if (group != "R1") throw new InvalidDataException("This pilot supports only control group R1");
        int[] io = parts.Length == 4 && parts[3].Length > 0
            ? parts[3].Split(';').Select(s => Integer("IO address", s.Trim(), 1, 99999)).ToArray() : [];
        if (io.Length > 24 || io.Distinct().Count() != io.Length) throw new InvalidDataException("Use at most 24 unique IO byte addresses per robot");
        return new(id, parts[1], group, io);
    }

    public string ReadToken()
    {
        if (!File.Exists(TokenFile)) throw new FileNotFoundException($"Missing collector token file: {TokenFile}");
        string token = (File.ReadLines(TokenFile).FirstOrDefault() ?? "").Trim();
        if (token.Length == 0 || token.Any(char.IsWhiteSpace) || token == "REPLACE_WITH_TOKEN_FROM_YOUR_LARAVEL_ENVIRONMENT")
            throw new InvalidDataException("Set collector.token to your actual Laravel collector token. Do not use the example placeholder.");
        return token;
    }
}

internal sealed record CliOptions(string ConfigPath, bool Mock, bool Real, bool Once, bool DryRun, bool Probe, bool AllowMockPost, int RobotId, bool Help, bool CheckSdk)
{
    public static CliOptions Parse(string[] args)
    {
        string path = "config/collector.conf";
        bool mock = false, real = false, once = false, dry = false, probe = false, allow = false, help = false, sdk = false;
        int selected = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {args[i - 1]}");
            switch (args[i])
            {
                case "--config": path = Value(); break;
                case "--robot-id": selected = CollectorConfig.Integer("robot-id", Value(), 1, 1000000); break;
                case "--mock": mock = true; break;
                case "--real": real = true; break;
                case "--once": once = true; break;
                case "--dry-run": dry = true; break;
                case "--probe": probe = true; break;
                case "--allow-mock-post": allow = true; break;
                case "--help": help = true; break;
                case "--check-sdk": sdk = true; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}");
            }
        }
        if (help || sdk) return new(path, mock, real, once, dry, probe, allow, selected, help, sdk);
        if (mock && real) throw new ArgumentException("Cannot use --mock and --real together");
        if (probe && (mock || real)) throw new ArgumentException("Use --probe separately from --mock or --real");
        if (!mock && !real && !probe) throw new ArgumentException("Choose --mock, --real, or --probe");
        if (mock && !dry && !allow) throw new ArgumentException("MOCK POST blocked. Use --dry-run, or explicitly --allow-mock-post with a DEMO robot ID only");
        return new(path, mock, real, once, dry, probe, allow, selected, help, sdk);
    }
}
