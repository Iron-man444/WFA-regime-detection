namespace Wfa.Desktop.Models;
/// <summary>
/// Polling payload from <c>GET /api/v1/analyze/status/{job_id}</c>.
/// </summary>
public sealed class AnalyzeJobStatusResponse
{
    public int Progress { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool IsComplete { get; set; }

    public AnalysisResultModel? Result { get; set; }

    public string? Error { get; set; }
}
