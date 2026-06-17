using System.Collections.Generic;

namespace Wfa.Desktop.Models;

public sealed class MonteCarloResultModel
{
    public List<double> OriginalEquity { get; set; } = new();
    public List<List<double>> SimulatedEquities { get; set; } = new();
    public double SurvivalProbability { get; set; } = 0.0;
}
