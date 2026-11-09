using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Core;

/// Sends the firmware the stick takes, when its owner asks, to Are we ARM
/// yet's private store. Nothing there is published: the files are what it
/// takes to ask the maker to put them in linux-firmware. Files go by SHA-256,
/// so the store first says which ones it lacks and only those are sent; the
/// list of every file with its driver package comes last, and the store
/// answers it with a code the owner can put in a report.
public static class FirmwareDrop
{
    /// OMARCHY_FIRMWARE_DROP points it elsewhere, such as `wrangler dev`.
    public static readonly Uri Endpoint = new(Environment.GetEnvironmentVariable("OMARCHY_FIRMWARE_DROP") ?? "https://arewearmyet.com/api/firmware/");

    /// What one request may carry; a bigger file is listed but not sent.
    public const long LargestFile = 95L * 1024 * 1024;
    const int HashesPerAsk = 40;
    const int Retries = 5;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmarchyArmInstaller",
            Log.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? Log.Version : "0"));
        return client;
    }

    sealed record Hashed(FirmwareFile File, string Sha256);

    sealed record ReportFile(
        [property: JsonPropertyName("folder")] string Folder,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256);

    public static long Size(IEnumerable<FirmwarePackage> packages) =>
        packages.SelectMany(p => p.Found).Where(f => f.Size <= LargestFile).Sum(f => f.Size);

    /// Sends what the store lacks and returns the code for this list of files.
    public static async Task<string> SendAsync(IReadOnlyList<FirmwarePackage> packages, SmbiosIdentity? identity, string model,
        IProgress<(string Text, double Fraction)> progress, CancellationToken ct)
    {
        var files = packages.SelectMany(p => p.Found).ToList();
        var total = Math.Max(1, files.Sum(f => f.Size));

        // Hashing reads every file once: the first fifth of the bar.
        var hashed = new List<Hashed>();
        long read = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(($"Checking {f.Name}", 0.2 * read / total));
            await using (var stream = File.OpenRead(f.SourcePath))
                hashed.Add(new Hashed(f, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant()));
            read += f.Size;
        }

        var sendable = hashed.Where(h => h.File.Size <= LargestFile).DistinctBy(h => h.Sha256).ToList();
        var missing = new HashSet<string>();
        foreach (var batch in sendable.Chunk(HashesPerAsk))
        {
            progress.Report(("Asking which files Are we ARM yet lacks", 0.2));
            using var answer = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "missing"))
            {
                Content = JsonContent.Create(new { sha256 = batch.Select(h => h.Sha256).ToArray() }),
            }, ct);
            var body = await answer.Content.ReadFromJsonAsync<JsonElement>(ct);
            foreach (var h in body.GetProperty("missing").EnumerateArray()) missing.Add(h.GetString()!);
        }

        var toSend = sendable.Where(h => missing.Contains(h.Sha256)).ToList();
        var sendTotal = Math.Max(1, toSend.Sum(h => h.File.Size));
        long sent = 0;
        foreach (var h in toSend)
        {
            progress.Report(($"Sending {h.File.Name}", 0.2 + 0.78 * sent / sendTotal));
            using var _ = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Put, new Uri(Endpoint, $"files/{h.Sha256}"))
            {
                Content = new StreamContent(File.OpenRead(h.File.SourcePath)),
            }, ct);
            sent += h.File.Size;
        }
        Log.Info($"Firmware drop: {hashed.Count} files, {missing.Count} new, {toSend.Count} sent ({sent} bytes)");

        progress.Report(("Sending the list of files", 0.98));
        var machine = new Dictionary<string, string?>
        {
            ["manufacturer"] = identity?.Manufacturer ?? "Unknown",
            ["product"] = identity?.ProductName ?? model,
            ["family"] = identity?.Family,
            ["sku"] = identity?.Sku,
            ["baseboard"] = identity?.BaseboardProduct,
            ["bios"] = identity?.BiosVersion,
        }.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary();
        using var done = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "reports"))
        {
            Content = JsonContent.Create(new
            {
                installer = Log.Version,
                machine,
                files = hashed.Select(h => new ReportFile(h.File.Folder, h.File.Name, h.File.Size, h.Sha256)).ToArray(),
            }),
        }, ct);
        var code = (await done.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString()!;
        progress.Report(("Sent", 1));
        return code;
    }

    /// Sends a request, waiting a minute and trying again while the store
    /// says too many have come from here, and failing with its own words
    /// otherwise.
    static async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = request();
            var response = await Http.SendAsync(message, ct);
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout && attempt < Retries)
            {
                Log.Info($"Firmware drop: {(int)status}, waiting a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                continue;
            }
            throw new HttpRequestException($"Are we ARM yet did not take the firmware: {ErrorText(text) ?? $"{(int)status} {status}"}", null, status);
        }
    }

    /// The store's own explanation, {"error": "..."}, when it gave one.
    static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
