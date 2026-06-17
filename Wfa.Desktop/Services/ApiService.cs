using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wfa.Desktop.Models;

namespace Wfa.Desktop.Services;

/// <summary>
/// Singleton HTTP gateway for the local FastAPI WFA backend.
/// </summary>
public sealed class ApiService
{
    private static readonly Lazy<ApiService> LazyInstance = new(() => new ApiService());

    public static ApiService Instance => LazyInstance.Value;

    private static readonly Uri DefaultBaseUri = new("http://localhost:8000/");

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    // Current running job id (set when RunAnalysisAsync starts a job)
    public string? CurrentJobId { get; private set; }

    private ApiService()
        : this(DefaultBaseUri)
    {
    }

    /// <summary>
    /// Testing / DI-friendly constructor; prefer <see cref="Instance"/> from WPF code-behind.
    /// </summary>
    public ApiService(Uri baseAddress)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(5),
        };

        _jsonOptions = CreateJsonOptions();
    }

    private static JsonSerializerOptions CreateJsonOptions() =>
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = response.ReasonPhrase ?? "(empty response body)";
        }

        throw new ApiException((int)response.StatusCode, body);
    }

    /// <summary>
    /// Runs the analysis job via <c>POST /api/v1/analyze</c> with job polling.
    /// </summary>
    public async Task<AnalysisResultModel> RunAnalysisAsync(
        WfaRequestPayload payload,
        IProgress<(int Progress, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var startResponse = await _httpClient
            .PostAsJsonAsync("api/v1/analyze", payload, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessOrThrowAsync(startResponse, cancellationToken).ConfigureAwait(false);

        var startPayload = await startResponse.Content
            .ReadFromJsonAsync<AnalyzeJobStartResponse>(_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (startPayload is null || string.IsNullOrWhiteSpace(startPayload.JobId))
        {
            throw new InvalidOperationException("Analyze job did not return a job_id.");
        }

        var jobId = startPayload.JobId;
        CurrentJobId = jobId;
        progress?.Report((0, "Starting..."));

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                using var statusResponse = await _httpClient
                    .GetAsync($"api/v1/analyze/status/{Uri.EscapeDataString(jobId)}", cancellationToken)
                    .ConfigureAwait(false);

            await EnsureSuccessOrThrowAsync(statusResponse, cancellationToken).ConfigureAwait(false);

            var status = await statusResponse.Content
                .ReadFromJsonAsync<AnalyzeJobStatusResponse>(_jsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (status is null)
            {
                throw new InvalidOperationException("Analyze job status response was empty.");
            }

            progress?.Report((status.Progress, status.Status));

            if (!status.IsComplete)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(status.Error))
            {
                throw new InvalidOperationException(status.Error);
            }

            return status.Result ?? new AnalysisResultModel();
            }
        }
        finally
        {
            // Clear job id when finished or on error
            CurrentJobId = null;
        }
    }

    /// <summary>
    /// Parse an MT5 report file on the API host and return a list of results.
    /// </summary>
    public async Task<List<WfaResultModel>> ParseMt5ReportAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var req = new { filePath = filePath };
        using var response = await _httpClient
            .PostAsJsonAsync("api/v1/parse-mt5-report", req, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = await JsonSerializer
            .DeserializeAsync<List<WfaResultModel>>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return results ?? new List<WfaResultModel>();
    }

    /// <summary>
    /// Cancels a running analyze job on the API host.
    /// </summary>
    public async Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;

        using var response = await _httpClient.PostAsync($"api/v1/analyze/cancel/{Uri.EscapeDataString(jobId)}", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Run a Monte Carlo simulation on the server from a provided trade list.
    /// </summary>
    public async Task<MonteCarloResultModel> RunMonteCarloAsync(List<TradeRecordModel> trades, int iterations = 200, CancellationToken cancellationToken = default)
    {
        var req = new { trades = trades, iterations = iterations };
        using var response = await _httpClient.PostAsJsonAsync("api/v1/monte-carlo", req, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        var mc = await response.Content.ReadFromJsonAsync<MonteCarloResultModel>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return mc ?? new MonteCarloResultModel();
    }

    /// <summary>
    /// Run a WFA cluster analysis: multiple window counts × in-sample percentages.
    /// </summary>
    public async Task<List<WfaClusterRowModel>> RunWfaClusterAsync(WfaRequestPayload payload, List<int> windowCounts, List<int> inSamplePercents, CancellationToken cancellationToken = default)
    {
        var req = new { payload = payload, windowCounts = windowCounts, inSamplePercents = inSamplePercents };
        using var response = await _httpClient.PostAsJsonAsync("api/v1/wfa-cluster", req, _jsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        var rows = await response.Content.ReadFromJsonAsync<List<WfaClusterRowModel>>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return rows ?? new List<WfaClusterRowModel>();
    }

    /// <summary>
    /// Loads <c>STRATEGY_PARAMS</c> from a Python strategy file on the API host.
    /// </summary>
    public async Task<List<StrategyInputRow>> GetStrategyParametersAsync(
        string strategyFileName,
        CancellationToken cancellationToken = default)
    {
        var encoded = Uri.EscapeDataString(strategyFileName);
        using var response = await _httpClient
            .GetAsync($"api/v1/strategy/parameters/{encoded}", cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        // Backend returns snake_case keys: variable, default_value, start, step, stop
        var dicts = await JsonSerializer
            .DeserializeAsync<List<Dictionary<string, JsonElement>>>(stream, _jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (dicts is null || dicts.Count == 0)
        {
            return new List<StrategyInputRow>();
        }

        var rows = new List<StrategyInputRow>(dicts.Count);
        foreach (var d in dicts)
        {
            // Helper to pick keys with possible naming variations
            JsonElement Get(JsonElement defaultEl, params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (d.TryGetValue(k, out var v)) return v;
                }
                return defaultEl;
            }

            var varName = string.Empty;
            if (d.TryGetValue("variable", out var v0) && v0.ValueKind == JsonValueKind.String)
                varName = v0.GetString() ?? string.Empty;
            else if (d.TryGetValue("Variable", out var v0c) && v0c.ValueKind == JsonValueKind.String)
                varName = v0c.GetString() ?? string.Empty;

            var defaultJe = Get(default, "default_value", "defaultValue", "defaultvalue");
            var startJe = Get(default, "start");
            var stepJe = Get(default, "step");
            var stopJe = Get(default, "stop");

            rows.Add(new StrategyInputRow
            {
                Variable = varName,
                Value = FormatJsonElement(defaultJe),
                Start = FormatJsonElement(startJe),
                Step = FormatJsonElement(stepJe),
                Stop = FormatJsonElement(stopJe),
                Optimize = true,
            });
        }

        return rows;
    }

    private static StrategyInputRow MapToInputRow(StrategyParameterDto dto) =>
        new()
        {
            Variable = dto.Variable,
            Value = FormatJsonElement(dto.DefaultValue),
            Start = FormatJsonElement(dto.Start),
            Step = FormatJsonElement(dto.Step),
            Stop = FormatJsonElement(dto.Stop),
            Optimize = true,
        };

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var i)
                ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : element.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText(),
        };
    }
}
