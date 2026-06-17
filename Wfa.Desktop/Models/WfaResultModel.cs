using System.Collections.Generic;
using System.Text.Json;

namespace Wfa.Desktop.Models;

/// <summary>
/// Response row from <c>POST /api/v1/analyze</c> (camelCase JSON from FastAPI).
/// </summary>
public sealed class WfaResultModel
{
    public int TestWindowId { get; set; }

    public double IsProfit { get; set; }

    public double OosProfit { get; set; }

    public double DrawdownPercent { get; set; }

    public double ProfitFactor { get; set; }

    public double WinRate { get; set; }

    public int TotalTrades { get; set; }

    public Dictionary<string, JsonElement> BestParameters { get; set; } = new();

    // Indices (into baseline equity / series) describing IS/OOS windows returned by backend
    public int IsStartIndex { get; set; }
    public int IsEndIndex { get; set; }
    public int OosStartIndex { get; set; }
    public int OosEndIndex { get; set; }

    // New quantitative metric returned by backend (may be missing in older responses)
    [System.Text.Json.Serialization.JsonPropertyName("regime_shift_score")]
    [System.ComponentModel.DisplayName("Regime Shift (KL)")]
    public double? RegimeShiftScore { get; set; }
}
