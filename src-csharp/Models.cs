using System.Globalization;
using System.Text.Json;

namespace RmsCollector;

internal sealed record Reading(bool Ok = false, int Code = -1, string Error = "unavailable", string Raw = "");
internal sealed record OperatingReading(string Type, Reading Result, long? Seconds);

internal sealed class RobotSample
{
    public Reading Position { get; set; } = new();
    public Reading Torque { get; set; } = new();
    public Reading Alarm { get; set; } = new();
    public Reading State { get; set; } = new();
    public Reading Job { get; set; } = new();
    public Reading Io { get; set; } = new();
    public Reading AlarmHistory { get; set; } = new();
    public double[] Pulses { get; set; } = new double[6];
    public double[] Torques { get; set; } = new double[6];
    public List<OperatingReading> OperatingReadings { get; } = [];
    public int? ActiveAlarmCount { get; set; }
    public object? StateData { get; set; }
    public object? JobData { get; set; }
    public object? IoData { get; set; }
    public object? AlarmHistoryData { get; set; }
    public object? SystemInfoData { get; set; }
    public object[] ActiveAlarms { get; set; } = [];
}

internal static class ApiPayloads
{
    public const string TelemetryEndpoint = "/api/v1/collector/telemetry";
    public const string OperatingEndpoint = "/api/v1/collector/operating-times";
    public const string AlarmEndpoint = "/api/v1/collector/alarms/snapshots";
    public const string ConnectionEndpoint = "/api/v1/collector/connections";
    public static bool EndpointAllowed(string endpoint) => endpoint is TelemetryEndpoint or OperatingEndpoint or AlarmEndpoint or ConnectionEndpoint;
    public static string Timestamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public static string Json(object value) => JsonSerializer.Serialize(value);
    public static string Text(string? value) => (value ?? "").Replace("\0", "", StringComparison.Ordinal);
    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    public static object Availability(Reading r) => Map(("read_ok", r.Ok), ("status_code", r.Code), ("message", r.Error), ("raw", r.Ok ? r.Raw : null));
    private static object Axes(double[] values)
    {
        if (values.Length < 6 || values.Take(6).Any(x => !double.IsFinite(x))) throw new InvalidDataException("Invalid axis values");
        string[] axes = ["S", "L", "U", "R", "B", "T"];
        return Enumerable.Range(0, 6).ToDictionary(i => axes[i], i => values[i]);
    }
    public static object Metadata(RobotSample s, bool mock, Device d) => Map(
        ("simulated", mock), ("torque_unit", "sdk_raw"), ("position_unit", "pulses"), ("group", d.Group),
        ("controller_state", s.StateData), ("job", s.JobData), ("system_info", s.SystemInfoData),
        ("io_values", s.IoData), ("alarm_history", s.AlarmHistoryData),
        ("state_read", Availability(s.State)), ("job_read", Availability(s.Job)),
        ("io_read", Availability(s.Io)), ("alarm_history_read", Availability(s.AlarmHistory)));
    public static string Telemetry(Device d, string session, long seq, string at, RobotSample s, bool mock) => Json(Map(
        ("robot_id", d.Id), ("collector_session_id", session), ("sequence", seq), ("sampled_at", at),
        ("position_ok", s.Position.Ok), ("position_status_code", s.Position.Code), ("position_status_message", s.Position.Error),
        ("position_sampled_at", s.Position.Ok ? at : null), ("position_pulses", s.Position.Ok ? Axes(s.Pulses) : null),
        ("torque_ok", s.Torque.Ok), ("torque_status_code", s.Torque.Code), ("torque_status_message", s.Torque.Error),
        ("torque_sampled_at", s.Torque.Ok ? at : null), ("torque_axes", s.Torque.Ok ? Axes(s.Torques) : null),
        ("torque_unit", "sdk_raw"), ("raw_payload", Metadata(s, mock, d))));
    public static string Operating(Device d, string at, OperatingReading r, bool mock) => Json(Map(
        ("robot_id", d.Id), ("time_type", r.Type), ("sampled_at", at), ("read_ok", r.Result.Ok),
        ("status_code", r.Result.Code), ("status_message", r.Result.Error), ("value_raw", r.Result.Ok ? r.Result.Raw : null),
        ("value_seconds", r.Result.Ok ? r.Seconds : null),
        ("raw_payload", Map(("simulated", mock), ("duration_unit", "seconds"),
            ("duration_source", "YMConnect TimeData.elapsedTime Hours/Minutes/Seconds")))));
    public static string Alarms(Device d, string at, RobotSample s, bool mock) => Json(Map(
        ("robot_id", d.Id), ("sampled_at", at), ("read_ok", s.Alarm.Ok), ("status_code", s.Alarm.Code),
        ("status_message", s.Alarm.Error), ("sdk_text", s.Alarm.Ok ? s.Alarm.Raw : null),
        ("active_alarm_count", s.Alarm.Ok ? s.ActiveAlarmCount : null), ("parsed_alarms", s.Alarm.Ok ? s.ActiveAlarms : null),
        ("raw_payload", Map(("simulated", mock), ("active_alarms", s.Alarm.Ok ? s.ActiveAlarms : null),
            ("alarm_history", s.AlarmHistoryData), ("alarm_history_read", Availability(s.AlarmHistory))))));
    public static string Connection(Device d, string at, string state, bool mock) => Json(Map(
        ("robot_id", d.Id), ("state", state), ("occurred_at", at), ("ip_address", d.Ip),
        ("message", mock ? "MOCK TEST — NOT ACTUAL ROBOT" : "YMConnect collector status"),
        ("raw_payload", Map(("simulated", mock), ("control_group", d.Group)))));
}
