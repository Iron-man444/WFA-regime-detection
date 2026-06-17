namespace Wfa.Desktop.Models;

public sealed class AnalysisSummaryModel
{
    public double NetProfit { get; set; }

    public double TotalReturnPercent { get; set; }

    public double SharpeRatio { get; set; }

    public double MaxDrawdownPercent { get; set; }

    public double ProfitFactor { get; set; }

    public double WinRate { get; set; }

    public int TotalTrades { get; set; }
}
