"""Local copy for the WPF Expert list — keep in sync with the API ``strategies/`` folder."""

STRATEGY_PARAMS = {
    "InpFastMa": {"default": 10, "min": 5, "max": 50, "step": 5},
    "InpSlowMa": {"default": 30, "min": 20, "max": 80, "step": 5},
    "InpRiskPct": {"default": 1.0, "min": 0.5, "max": 5.0, "step": 0.5},
}
