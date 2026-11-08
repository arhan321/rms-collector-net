using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RmsCollector;

internal sealed record HttpResult(int Status, string Body, string? Error = null)
{
    public bool Ok => Error is null && Status is >= 200 and < 300;
    public bool Fatal => Error is null && Status is >= 400 and < 500 && Status is not (408 or 425 or 429);
    public string Summary => Error ?? Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ApiClient : IDisposable
{
    private readonly HttpClient client;
    private readonly string apiBase;
    private readonly string token;
    public ApiClient(CollectorConfig config, string bearer, HttpMessageHandler? handler = null)
    {
        apiBase = config.ApiBase;
        token = bearer;
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3), UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds), MaxResponseContentBufferSize = 64 * 1024 };
    }
    public async Task<HttpResult> SendAsync(string endpoint, string? json, CancellationToken cancellation)
    {
        if (json is not null && !ApiPayloads.EndpointAllowed(endpoint)) throw new InvalidDataException("Unsafe endpoint");
        if (json is null && endpoint != "/up") throw new InvalidDataException("Unsafe probe endpoint");
        using var request = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, apiBase + endpoint);
        request.Headers.Accept.Add(new("application/json"));
        if (json is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        try
        {
            // ResponseContentRead keeps the request timeout active while receiving the body.
            // Responses are expected to be small JSON; never log bearer tokens.
            using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return new((int)response.StatusCode, body[..Math.Min(body.Length, 4096)]);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new(0, "", "HTTP timeout"); }
        catch (HttpRequestException e) { return new(0, "", e.Message); }
    }
    public void Dispose() => client.Dispose();
}

internal sealed class Dispatcher : IDisposable
{
    private readonly CollectorConfig config;
    private readonly Device device;
    private readonly bool dryRun;
    private readonly ApiClient? http;
    private readonly FileStream? ownerLock;
    private long counter;
    public string QueueDirectory { get; }
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public Dispatcher(CollectorConfig config, Device device, bool dryRun, string token, HttpMessageHandler? handler = null)
    {
        this.config = config;
        this.device = device;
        this.dryRun = dryRun;
        QueueDirectory = Path.Combine(config.SpoolDirectory, device.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (dryRun) return;
        Directory.CreateDirectory(QueueDirectory);
        // Fail if another C# collector already owns this robot's spool.
        ownerLock = new FileStream(Path.Combine(QueueDirectory, ".collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        http = new(config, token, handler);
    }

    private string[] QueuedFiles() => Directory.GetFiles(QueueDirectory, "*.queue", SearchOption.TopDirectoryOnly)
        .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();

    private string Save(string endpoint, string json, bool rejected = false)
    {
        if (!rejected && QueuedFiles().Length >= config.SpoolMax)
            throw new IOException("Queue full; collector stops instead of silently dropping data");
        string targetDirectory = rejected ? Path.Combine(QueueDirectory, "rejected") : QueueDirectory;
        Directory.CreateDirectory(targetDirectory);
        long micros = (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        string name = $"{micros:D16}_{counter++:D6}_{Guid.NewGuid():N}.queue";
        string path = Path.Combine(targetDirectory, name), temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8, leaveOpen: true))
            {
                writer.Write(endpoint + "\n" + json + "\n");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path);
        }
        catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        return path;
    }

    private void Validate(string endpoint, string json)
    {
        if (!ApiPayloads.EndpointAllowed(endpoint)) throw new InvalidDataException("Unsafe queue endpoint");
        using var value = JsonDocument.Parse(json);
        if (value.RootElement.ValueKind != JsonValueKind.Object || !value.RootElement.TryGetProperty("robot_id", out var robot) ||
            !robot.TryGetInt32(out int id) || id != device.Id) throw new InvalidDataException("Queue payload robot_id does not match this robot");
    }

    public async Task PostAsync(string endpoint, string json, CancellationToken cancellation)
    {
        Validate(endpoint, json);
        if (dryRun) { AppLog.Write($"[DRY id={device.Id}] POST {endpoint} {json}"); return; }
        // Preserve existing backlog order. A newer sample must not overtake old samples.
        if (QueuedFiles().Length > 0) { Save(endpoint, json); return; }
        HttpResult result;
        try { result = await http!.SendAsync(endpoint, json, cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { Save(endpoint, json); throw; }
        if (result.Ok) { AppLog.Write($"[API {device.Id}] {endpoint} => {result.Summary}"); return; }
        string saved = Save(endpoint, json, result.Fatal);
        if (result.Fatal)
            throw new InvalidOperationException($"Laravel rejected POST: HTTP {result.Summary}. Payload preserved at {saved}. Check token/robot ID/API validation. {result.Body[..Math.Min(result.Body.Length, 800)]}");
        AppLog.Write($"[SPOOL {device.Id}] {endpoint} failed {result.Summary}");
    }

    public async Task FlushAsync(CancellationToken cancellation)
    {
        if (dryRun) return;
        foreach (string path in QueuedFiles().Take(10))
        {
            cancellation.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (file.Length > 1024 * 1024) throw new InvalidDataException($"Queue file is too large: {path}");
            string[] lines = await File.ReadAllLinesAsync(path, Utf8, cancellation).ConfigureAwait(false);
            if (lines.Length != 2) throw new InvalidDataException($"Invalid queue file: {path}");
            string endpoint = lines[0], json = lines[1];
            Validate(endpoint, json);
            var result = await http!.SendAsync(endpoint, json, cancellation).ConfigureAwait(false);
            if (result.Ok) { File.Delete(path); AppLog.Write($"[REPLAY {device.Id}] HTTP {result.Summary}"); continue; }
            if (result.Status == 422 && result.Error is null)
            {
                string directory = Path.Combine(QueueDirectory, "rejected");
                Directory.CreateDirectory(directory);
                File.Move(path, Path.Combine(directory, Path.GetFileName(path)));
                AppLog.Write($"[QUARANTINE {device.Id}] HTTP 422 {path}");
                continue;
            }
            if (result.Fatal) throw new InvalidOperationException($"Queue replay blocked: HTTP {result.Summary}. Original file retained: {path}");
            break;
        }
    }

    public void Dispose() { http?.Dispose(); ownerLock?.Dispose(); }
}
