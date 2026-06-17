namespace Wfa.Desktop.Models;

/// <summary>
/// One row in the Strategy Tester "Inputs" grid (optimization parameter definition).
/// </summary>
public sealed class StrategyInputRow
{
    public string Variable { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Start { get; set; } = string.Empty;

    public string Step { get; set; } = string.Empty;

    public string Stop { get; set; } = string.Empty;

    public bool Optimize { get; set; }
}
