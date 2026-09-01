using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Esi;

public record EsiResponse<T>(T? Data, bool NotModified, int Pages, string? Etag);

/// <summary>
/// Thin ESI wrapper: gzip, User-Agent, ETag conditional requests, X-ESI-Error-Limit
/// backoff and 420/5xx retry. All public ESI reads go through here.
/// </summary>
public class EsiClient
{
    public const string BaseUrl = "https://esi.evetech.net/latest";
    public const int JitaSystemId = 30000142;
    public const long Jita44StationId = 60003760;
    public const int TheForgeRegionId = 10000002;

    private readonly HttpClient _http;
    private readonly ILogger<EsiClient> _log;

    // Error-limit budget shared across all callers.
    private int _errorsRemaining = 100;
    private DateTime _errorWindowReset = DateTime.MinValue;
    private readonly SemaphoreSlim _backoffGate = new(1, 1);

    public EsiClient(HttpClient http, ILogger<EsiClient> log)
    {
        _http = http;
        _log = log;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<EsiResponse<T>> GetAsync<T>(string path, string? etag = null, string? accessToken = null, CancellationToken ct = default, int maxRetries = 4)
    {
        var url = path.StartsWith("http") ? path : BaseUrl + path;
        for (var attempt = 0; ; attempt++)
        {
            await WaitIfErrorLimitedAsync(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (accessToken is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                _log.LogWarning("ESI request failed ({Error}), retrying {Url}", ex.Message, url);
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                continue;
            }

            using (resp)
            {
                TrackErrorLimit(resp);
                var pages = resp.Headers.TryGetValues("X-Pages", out var pv) && int.TryParse(pv.FirstOrDefault(), out var p) ? p : 1;
                var newEtag = resp.Headers.ETag?.Tag;

                if (resp.StatusCode == HttpStatusCode.NotModified)
                    return new EsiResponse<T>(default, true, pages, etag);

                if ((int)resp.StatusCode == 420 || (int)resp.StatusCode >= 500)
                {
                    if (attempt >= maxRetries) throw new HttpRequestException($"ESI {resp.StatusCode} for {url} after retries");
                    var delay = (int)resp.StatusCode == 420 ? 60 : 3 * (attempt + 1);
                    _log.LogWarning("ESI {Status} for {Url}, backing off {Delay}s", (int)resp.StatusCode, url, delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    continue;
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new EsiResponse<T>(default, false, pages, newEtag);

                resp.EnsureSuccessStatusCode();
                try
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var data = await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
                    return new EsiResponse<T>(data, false, pages, newEtag);
                }
                catch (JsonException ex)
                {
                    // ESI occasionally returns 200 with an empty/garbled body. Surface it as a
                    // request failure so callers retry later, rather than caching "no data".
                    _log.LogWarning("ESI returned unparseable body for {Url}: {Error}", url, ex.Message);
                    throw new HttpRequestException($"Unparseable ESI body for {url}", ex);
                }
            }
        }
    }

    private void TrackErrorLimit(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-ESI-Error-Limit-Remain", out var remain) &&
            int.TryParse(remain.FirstOrDefault(), out var r))
        {
            _errorsRemaining = r;
            if (resp.Headers.TryGetValues("X-ESI-Error-Limit-Reset", out var reset) &&
                int.TryParse(reset.FirstOrDefault(), out var secs))
                _errorWindowReset = DateTime.UtcNow.AddSeconds(secs);
        }
    }

    private async Task WaitIfErrorLimitedAsync(CancellationToken ct)
    {
        if (_errorsRemaining > 10) return;
        await _backoffGate.WaitAsync(ct);
        try
        {
            if (_errorsRemaining <= 10 && _errorWindowReset > DateTime.UtcNow)
            {
                var wait = _errorWindowReset - DateTime.UtcNow + TimeSpan.FromSeconds(1);
                _log.LogWarning("ESI error budget low ({Remain}), pausing {Wait:0}s", _errorsRemaining, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                _errorsRemaining = 100;
            }
        }
        finally
        {
            _backoffGate.Release();
        }
    }
}

// ---- ESI DTOs ----

public class EsiPublicContract
{
    public long ContractId { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public double? Price { get; set; }
    public double? Reward { get; set; }
    public double? Collateral { get; set; }
    public long? StartLocationId { get; set; }
    public long? EndLocationId { get; set; }
    public DateTime DateIssued { get; set; }
    public DateTime DateExpired { get; set; }
    public double? Volume { get; set; }
    public bool? ForCorporation { get; set; }
    public int? DaysToComplete { get; set; }
}

public class EsiContractItem
{
    public int TypeId { get; set; }
    public long Quantity { get; set; }
    public bool? IsIncluded { get; set; }
    public bool? IsBlueprintCopy { get; set; }
}

public class EsiCharacterContract
{
    public long ContractId { get; set; }
    public int IssuerId { get; set; }
    public int IssuerCorporationId { get; set; }
    public int AssigneeId { get; set; }
    public int AcceptorId { get; set; }
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Title { get; set; }
    public double? Price { get; set; }
    public double? Reward { get; set; }
    public double? Collateral { get; set; }
    public long? StartLocationId { get; set; }
    public long? EndLocationId { get; set; }
    public DateTime DateIssued { get; set; }
    public DateTime DateExpired { get; set; }
    public DateTime? DateCompleted { get; set; }
    public bool ForCorporation { get; set; }
    public double? Volume { get; set; }
    public int? DaysToComplete { get; set; }
    public double? Buyout { get; set; }
}

public class EsiMarketHistoryDay
{
    public string Date { get; set; } = "";
    public double Volume { get; set; }
    public double Average { get; set; }
    public double Highest { get; set; }
    public double Lowest { get; set; }
    public long OrderCount { get; set; }
}

public class EsiStructure
{
    public string Name { get; set; } = "";
    public int SolarSystemId { get; set; }
}

public class EsiName
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}
