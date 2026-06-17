using System;
using System.Collections.Generic;

namespace Wfa.Desktop.Models;

public sealed class UserSettings
{
    public string SelectedStrategy { get; set; } = string.Empty;
    public string Deposit { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Leverage { get; set; } = string.Empty;
    public int WindowCount { get; set; } = 5;
    public double InSamplePercent { get; set; } = 50.0;
    public string ForwardSplit { get; set; } = "1/2";
    public string LatencyMode { get; set; } = "Zero latency";
    public string ModellingType { get; set; } = "Every tick";
    public string DateRangePreset { get; set; } = "Last Month";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int DataSourceTabIndex { get; set; } = 0;
    public string CsvPath { get; set; } = string.Empty;
    public string AssetClass { get; set; } = string.Empty;
    public string ExecutionMode { get; set; } = "WFAOptimization";
    public string Mt5ReportPath { get; set; } = string.Empty;
    public string PayloadExecutionMode { get; set; } = "WFAOptimization";
    public string FitnessCriterion { get; set; } = "Balance max";
    public string OptimizationMode { get; set; } = "Fast";
    public Dictionary<string, List<StrategyInputRow>> StrategyInputs { get; set; } = new();
}
