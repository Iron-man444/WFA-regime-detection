using System;
using System.Collections.Generic;

namespace Wfa.Desktop.Models;

/// <summary>
/// POST body for <c>/api/v1/analyze</c>. Serialized with <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/>.
/// </summary>
public sealed class WfaRequestPayload
{
    public string DataSourceType { get; set; } = "API";

    public string? FilePath { get; set; }

    public string? AssetClass { get; set; }

    public string? Symbol { get; set; }

    public string? Timeframe { get; set; }

    // ExecutionMode for the server payload. "WFAOptimization" = Walk-Forward Analysis (server expected value), "StandardOptimization" = plain optimization without WFA windows, "MonteCarlo" = Monte Carlo robustness simulation.
    public string ExecutionMode { get; set; } = "WFAOptimization";

    // Fitness criterion used for selecting best parameters (MT5-style options)
    public string FitnessCriterion { get; set; } = "Balance max";

    public string OptimizationMode { get; set; } = "Fast";

    public string? StrategyName { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public string? DateRangePreset { get; set; }

    public string ForwardSplit { get; set; } = "1/2";

    public string LatencyMode { get; set; } = "Zero latency";

    public string ModellingType { get; set; } = "Every tick";

    public double InitialDeposit { get; set; } = 10_000d;

    public string Currency { get; set; } = "USD";

    public string Leverage { get; set; } = "1:100";

    public double Commission { get; set; } = 5d;

    public double Slippage { get; set; } = 2d;

    public int WindowCount { get; set; } = 5;

    public double InSamplePercent { get; set; } = 50d;

    // Cluster matrix inputs
    public List<int> MatrixWindows { get; set; } = new();
    public List<int> MatrixIsPercents { get; set; } = new();

    public string WfaType { get; set; } = "Expanding";

    public List<StrategyInputRow>? InputParameters { get; set; }
}
