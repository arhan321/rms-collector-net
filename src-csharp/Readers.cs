namespace RmsCollector;

internal interface IRobotReader : IDisposable
{
    bool Connected { get; }
    bool Connect();
    void Disconnect();
    RobotSample Collect(long sequence, bool extras, bool operating, bool alarms);
}

internal sealed class MockReader(Device device) : IRobotReader
{
    public bool Connected { get; private set; }
    public bool Connect() => Connected = true;
    public void Disconnect() => Connected = false;
    public void Dispose() => Disconnect();
    public RobotSample Collect(long sequence, bool extras, bool operating, bool alarms)
    {
        var s = new RobotSample { Position = new(true, 0, "", "mock positions"), Torque = new(true, 0, "", "mock torque") };
        for (int i = 0; i < 6; i++)
        {
            s.Pulses[i] = 100000 + i * 12000 + sequence * 5;
            s.Torques[i] = 9 + i * 1.1 + 0.4 * Math.Sin(sequence * 0.25 + i);
        }
        if (operating)
        {
            foreach (var (name, hours) in new[] { ("ControllerOnTime", 223), ("ServoPowerOnTimeRobot", 123), ("PlayBackTimeRobot", 97), ("MovingTimeRobot", 81) })
            {
                long seconds = hours * 3600L + sequence * 5;
                s.OperatingReadings.Add(new(name, new(true, 0, "", $"Elapsed Time: {seconds / 3600} Hours : {seconds / 60 % 60} Minutes : {seconds % 60} Seconds"), seconds));
            }
        }
        if (alarms)
        {
            s.Alarm = new(true, 0, "", "mock: no alarms");
            s.ActiveAlarmCount = 0;
            s.AlarmHistory = new(true, 0, "", "mock: empty");
            s.AlarmHistoryData = Array.Empty<object>();
        }
        s.State = new(true, 0, "", "SIMULATED");
        s.StateData = ApiPayloads.Map(("known", true), ("servo_on", false), ("running", false), ("alarm", false),
            ("hold", true), ("error", false), ("control_mode", "Teach"), ("cycle_mode", "Step"), ("source", "mock"));
        if (extras)
        {
            s.Job = new(true, 0, "", "SIMULATED");
            s.JobData = ApiPayloads.Map(("name", "MOCK_JOB"), ("line", 2), ("step", sequence % 50), ("speed_override_percent", 50), ("source", "mock"));
            s.SystemInfoData = ApiPayloads.Map(("simulated", true));
            s.Io = new(true, 0, "", "SIMULATED");
            s.IoData = device.Io.ToDictionary(a => a.ToString(System.Globalization.CultureInfo.InvariantCulture), a => (sequence + a) % 256);
        }
        return s;
    }
}
