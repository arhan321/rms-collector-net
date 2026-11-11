namespace RmsCollector;

/// <summary>
/// Optional, read-only YMConnect job evidence. Does NOT claim access to OEM JOB DIAGNOSIS.
/// Enable only with RMS_JOB_MONITOR_EVERY_SAMPLE=1; default collector behavior remains intact.
/// </summary>
internal static class JobMonitorOptions
{
    public static bool EverySample => string.Equals(
        Environment.GetEnvironmentVariable("RMS_JOB_MONITOR_EVERY_SAMPLE"), "1", StringComparison.Ordinal);
}

internal static class JobMonitorEvidence
{
    public static object FromSample(RobotSample sample, bool simulated)
    {
        // s.JobData is only non-null when a direct YMConnect job read succeeded.
        var job = sample.JobData as IReadOnlyDictionary<string, object?>;
        var state = sample.StateData as IReadOnlyDictionary<string, object?>;
        bool success = sample.Job.Ok && job is not null;

        return ApiPayloads.Map(
            ("schema_version", 1),
            ("source", simulated ? "mock" : "YMConnect.GetExecutingJobInformation"),
            ("read_ok", success),
            ("read_status", success ? "read_ok" : sample.Job.Code == -1 && sample.Job.Error == "unavailable"
                ? "not_polled" : "read_failed"),
            ("job_name", success ? Get(job, "name") : null),
            ("line", success ? Get(job, "line") : null),
            ("step", success ? Get(job, "step") : null),
            ("speed_override_percent", success ? Get(job, "speed_override_percent") : null),
            ("controller_running", state is not null ? Get(state, "running") : null),
            ("torque_read_ok", sample.Torque.Ok),
            ("oem_job_diagnosis_available", false),
            ("oem_job_diagnosis_note", "Other OEM Job Monitor metrics not exposed by verified collector APIs"));
    }

    private static object? Get(IReadOnlyDictionary<string, object?>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;
}
