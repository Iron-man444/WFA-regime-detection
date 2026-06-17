namespace Wfa.Desktop.Models;

public sealed class TradeRecordModel
{
    public int TradeId { get; set; }

    public int? EntryIndex { get; set; }

    public int? ExitIndex { get; set; }

    public string? EntryTime { get; set; }

    public string? ExitTime { get; set; }

    public string Direction { get; set; } = "Long";

    public double Size { get; set; }

    public double EntryPrice { get; set; }

    public double ExitPrice { get; set; }

    public double Pnl { get; set; }

    public double ReturnPercent { get; set; }
}
