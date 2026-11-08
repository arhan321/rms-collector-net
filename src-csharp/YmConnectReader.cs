using System.Globalization;
using System.Runtime.InteropServices;
using YMConnect;

namespace RmsCollector;

// Only read APIs are used here. The collector never commands motion, servo power,
// job execution, parameter changes or IO writes.
internal sealed class YmConnectReader(Device device) : IRobotReader
{
    private MotomanController? controller;
    public bool Connected => controller is not null;

    public static void CheckSdk()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("REAL requires Windows x64. Mock/probe can run on other platforms.");
        string native = Path.Combine(AppContext.BaseDirectory, "YMConnect.dll");
        if (!File.Exists(native)) throw new FileNotFoundException("YMConnect.dll is missing beside rms_collector.exe", native);
        try
        {
            nint handle = NativeLibrary.Load(native);
            NativeLibrary.Free(handle);
            _ = typeof(MotomanController).Assembly.GetName();
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("Cannot load the official Windows x64 YMConnect SDK. Install the supported Visual C++ v14 Redistributable x64 and keep the matching YMConnect.dll/YMConnect_CS.dll pair together. MSVC Build Tools are not needed.", e);
        }
    }

    public bool Connect()
    {
        Disconnect();
        var candidate = MotomanController.OpenConnection(device.Ip, out StatusInfo status);
        if (status.StatusCode != 0 || candidate is null)
        {
            candidate?.CloseConnection();
            AppLog.Write($"[YMConnect {device.Ip}] OPEN FAILED code={status.StatusCode}");
            return false;
        }
        controller = candidate;
        AppLog.Write($"[YMConnect] Connected {device.Ip}");
        return true;
    }

    public void Disconnect()
    {
        var previous = controller;
        controller = null;
        previous?.CloseConnection();
    }
    public void Dispose() => Disconnect();
    private static Reading Result(StatusInfo status, string failure, string raw = "") =>
        new(status.StatusCode == 0, status.StatusCode, status.StatusCode == 0 ? "" : failure, status.StatusCode == 0 ? raw : "");

    public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
    {
        var c = controller ?? throw new InvalidOperationException("Not connected to robot");
        var s = new RobotSample();
        var ps = c.ControlGroup.ReadPositionData(ControlGroupId.R1, CoordinateType.Pulse, 0, 0, out PositionData position);
        s.Position = Result(ps, "YMConnect position read failed");
        if (s.Position.Ok)
        {
            if (position.AxisData is not { Length: >= 6 } || position.AxisData.Take(6).Any(v => !double.IsFinite(v)))
                s.Position = new(false, -1, "Invalid position axis data from SDK");
            else s.Pulses = position.AxisData.Take(6).ToArray();
        }
        var ts = c.ControlGroup.ReadTorqueData(ControlGroupId.R1, out TorqueData torque);
        s.Torque = Result(ts, "YMConnect torque read failed");
        if (s.Torque.Ok)
        {
            if (torque.AxisData is not { Length: >= 6 }) s.Torque = new(false, -1, "Invalid torque axis data from SDK");
            else s.Torques = torque.AxisData.Take(6).Select(v => (double)v).ToArray();
        }
        if (operating) ReadOperating(c, s);
        if (alarms) ReadAlarms(c, s);

        // State must be refreshed at the telemetry rate, not only at extras rate.
        var stateStatus = c.Status.ReadState(out ControllerStateData state);
        s.State = Result(stateStatus, "YMConnect state read failed");
        if (s.State.Ok)
        {
            s.StateData = ApiPayloads.Map(("known", true), ("running", state.IsRunning), ("servo_on", state.IsServoOn),
                ("alarm", state.IsAlarming), ("hold", state.IsInHold), ("error", state.IsErroring),
                ("control_mode", Enum.IsDefined(state.ControlMode) ? state.ControlMode.ToString() : "Unknown"),
                ("cycle_mode", Enum.IsDefined(state.CycleMode) ? state.CycleMode.ToString() : "Unknown"));
            s.State = s.State with { Raw = ApiPayloads.Json(s.StateData) };
        }
        if (extras) ReadExtras(c, s);
        return s;
    }

    private static void ReadOperating(MotomanController c, RobotSample s)
    {
        foreach (TimeType type in new[] { TimeType.ControllerOnTime, TimeType.ServoPowerOnTimeRobot, TimeType.PlayBackTimeRobot, TimeType.MovingTimeRobot })
        {
            var status = c.Status.ReadOperatingTimes(ControlGroupId.R1, type, out TimeData data, ApplicationNumber.ApplicationOne);
            Reading result = Result(status, "Operating time read failed");
            long? seconds = null;
            if (result.Ok)
            {
                var elapsed = data.ElapsedTime;
                if (elapsed.Minutes >= 60 || elapsed.Seconds >= 60) result = new(false, -1, "YMConnect elapsed time components are out of range");
                else
                {
                    seconds = checked((long)elapsed.Hours * 3600 + elapsed.Minutes * 60L + elapsed.Seconds);
                    result = result with { Raw = FormattableString.Invariant($"Elapsed Time: {elapsed.Hours} Hours : {elapsed.Minutes} Minutes : {elapsed.Seconds} Seconds") };
                }
            }
            s.OperatingReadings.Add(new(type.ToString(), result, seconds));
        }
    }

    private static object AlarmObject(AlarmData alarm) => ApiPayloads.Map(
        ("alarm_code", alarm.Code.ToString(CultureInfo.InvariantCulture)), ("subcode", alarm.Subcode.ToString(CultureInfo.InvariantCulture)),
        ("alarm_name", ApiPayloads.Text(alarm.Name)), ("controller_time_raw", ApiPayloads.Text(alarm.Time)));

    private static void ReadAlarms(MotomanController c, RobotSample s)
    {
        var status = c.Faults.GetActiveAlarms(out ActiveAlarms active);
        s.Alarm = Result(status, "Active alarm read failed");
        if (s.Alarm.Ok)
        {
            if (active.Count < 0 || active.Count > (active.Alarms?.Length ?? 0))
                s.Alarm = new(false, -1, "Invalid active alarm count from SDK");
            else
            {
                s.ActiveAlarmCount = active.Count;
                s.ActiveAlarms = (active.Alarms ?? []).Take(active.Count).Select(AlarmObject).ToArray();
                string text = active.Count == 0 ? "No active alarms." : $"{active.Count} alarm{(active.Count == 1 ? "" : "s")} active.";
                foreach (var (a, i) in (active.Alarms ?? []).Take(active.Count).Select((a, i) => (a, i)))
                    text += $"\nAlarm {i + 1}: Code: {a.Code}\nSubcode: {a.Subcode}\nName: {ApiPayloads.Text(a.Name)}\nTime: {ApiPayloads.Text(a.Time)}";
                s.Alarm = s.Alarm with { Raw = text };
            }
        }
        var categories = new List<object>();
        bool allOk = true, anyOk = false;
        foreach (AlarmCategory category in Enum.GetValues<AlarmCategory>())
        {
            var result = c.Faults.GetAlarmHistory(category, 20, out AlarmHistory history);
            bool ok = result.StatusCode == 0;
            int count = ok ? Math.Min(Math.Max(history.NumberOfAlarmsFetched, 0), history.Alarms?.Length ?? 0) : 0;
            allOk &= ok;
            anyOk |= ok;
            categories.Add(ApiPayloads.Map(("category", category.ToString()), ("read_ok", ok), ("status_code", result.StatusCode),
                ("requested_count", 20), ("fetched_count", ok ? count : null),
                ("alarms", ok ? (history.Alarms ?? []).Take(count).Select(AlarmObject).ToArray() : Array.Empty<object>())));
        }
        s.AlarmHistoryData = ApiPayloads.Map(("requested_per_category", 20), ("categories", categories));
        s.AlarmHistory = new(allOk, allOk ? 0 : -1, allOk ? "" : anyOk ? "Some alarm history categories failed" : "All alarm history category reads failed",
            ApiPayloads.Json(s.AlarmHistoryData));
    }

    private void ReadExtras(MotomanController c, RobotSample s)
    {
        var info = new Dictionary<string, object?>();
        foreach (var (name, id) in new[] { ("robot_r1", SystemInfoId.R1), ("station_s1", SystemInfoId.S1), ("application_a1", SystemInfoId.A1) })
        {
            var status = c.Status.ReadSystemInformation(id, out SystemInfoData data);
            bool ok = status.StatusCode == 0;
            info[name] = ApiPayloads.Map(("read_ok", ok), ("status_code", status.StatusCode),
                ("model_name", ok ? ApiPayloads.Text(data.ModelName) : null), ("software_version", ok ? ApiPayloads.Text(data.SoftwareVersion) : null));
        }
        s.SystemInfoData = info;
        var jobStatus = c.Job.GetExecutingJobInformation(InformTaskNumber.Master, out JobData job);
        s.Job = Result(jobStatus, "YMConnect job read failed", jobStatus.StatusCode == 0 ? ApiPayloads.Text(job.Name) : "");
        if (s.Job.Ok)
            s.JobData = ApiPayloads.Map(("name", ApiPayloads.Text(job.Name)), ("line", job.Line), ("step", job.StepNumber),
                ("speed_override_percent", job.SpeedOverride), ("task", "Master"));

        if (device.Io.Length == 0) { s.Io = new(false, -1, "No IO addresses configured"); return; }
        var values = new Dictionary<string, object?>();
        bool allOk = true;
        foreach (int address in device.Io)
        {
            var status = c.IO.ReadByte((uint)address, out IOByteData value);
            bool ok = status.StatusCode == 0;
            allOk &= ok;
            values[address.ToString(CultureInfo.InvariantCulture)] = ApiPayloads.Map(("read_ok", ok), ("status_code", status.StatusCode),
                ("sdk_raw_text", ok ? Convert.ToString(value.ToByte(), 2).PadLeft(8, '0') : null));
        }
        s.IoData = values;
        s.Io = new(allOk, allOk ? 0 : -1, allOk ? "" : "Some IO address reads failed", ApiPayloads.Json(values));
    }
}
