using System.Text.Json;

namespace Wfa.Desktop.Models;

/// <summary>
/// API response row from <c>GET /api/v1/strategy/parameters/{filename}</c> (camelCase JSON).
/// </summary>
public sealed class StrategyParameterDto
{
    public string Variable { get; set; } = string.Empty;

    public JsonElement DefaultValue { get; set; }

    public JsonElement Start { get; set; }

    public JsonElement Step { get; set; }

    public JsonElement Stop { get; set; }
}
