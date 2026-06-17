namespace Wfa.Desktop.Models;

/// <summary>
/// Immediate response from <c>POST /api/v1/analyze</c> when a background job is queued.
/// </summary>
public sealed class AnalyzeJobStartResponse
{
    public string JobId { get; set; } = string.Empty;
}
