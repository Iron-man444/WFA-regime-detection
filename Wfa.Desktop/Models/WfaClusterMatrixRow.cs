using System.Windows.Media;

namespace Wfa.Desktop.Models;

public sealed class WfaClusterMatrixRow
{
    public int WindowCount { get; set; }
    // dynamic columns for in-sample values will be added to DataTable; this helper carries a background color for cells
    public Brush BackgroundBrush { get; set; } = Brushes.Transparent;
}
