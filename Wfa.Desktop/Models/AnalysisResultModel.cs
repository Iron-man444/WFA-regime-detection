using System.Collections.Generic;
using System.Text.Json;

namespace Wfa.Desktop.Models;

/// <summary>
/// Unified analysis payload returned by all execution modes.
/// </summary>
public sealed class AnalysisResultModel
{
    public string ExecutionMode { get; set; } = string.Empty;

    // Old typed summary model kept as fallback for numeric fields when available.
    public AnalysisSummaryModel? SummaryModel { get; set; } = new();

    // New flexible summary: key/value string pairs sent by the API (MT5-style Account Statistics)
    public Dictionary<string, string> Summary { get; set; } = new();

    public List<EquityPointModel> EquityCurve { get; set; } = new();

    // Monte Carlo original equity as a simple list of values (per-trade cumulative PnL)
    public List<double> OriginalEquity { get; set; } = new();

    public List<List<double>> SimulatedEquities { get; set; } = new();

    public double SurvivalProbability { get; set; } = 0.0;

    public List<EquityPointModel> PriceCurve { get; set; } = new();
    public List<OhlcPointModel> OhlcCurve { get; set; } = new();

    // Baseline (raw) equity curve returned by the server for reference/comparison
    public List<EquityPointModel> BaselineEquityCurve { get; set; } = new();

    public List<DrawdownPointModel> DrawdownCurve { get; set; } = new();

    public List<TradeRecordModel> Trades { get; set; } = new();

    public List<WfaResultModel> WfaWindows { get; set; } = new();

    public Dictionary<string, JsonElement> Parameters { get; set; } = new();

    // Scores produced during optimization passes (pass 1, pass 2, ...)
    public List<double> OptimizationScores { get; set; } = new();

    // Sensitivity analysis: list of tested parameter dictionaries and corresponding profits
    public List<Dictionary<string, JsonElement>> TestedParams { get; set; } = new();
    public List<double> TestedProfits { get; set; } = new();

    // Optimization results: list of dict rows (Score + parameter values)
    public List<Dictionary<string, JsonElement>> OptimizationResults { get; set; } = new();

    // Cluster matrix returned from server: list of rows (dict)
    public List<Dictionary<string, JsonElement>> ClusterMatrix { get; set; } = new();
}



public sealed class OhlcPointModel
{
    public int Index { get; set; }
    public string? Timestamp { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
}