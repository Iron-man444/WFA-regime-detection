from typing import Any

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class AnalyzeJobStartResponse(BaseModel):
    """Immediate response from POST /api/v1/analyze when a background job is queued."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    job_id: str

class OhlcPoint(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)
    index: int
    timestamp: str | None = None
    open: float
    high: float
    low: float
    close: float

class EquityPoint(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    index: int
    timestamp: str | None = None
    value: float


class DrawdownPoint(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    index: int
    timestamp: str | None = None
    value: float = Field(description="Drawdown as a positive percentage.")


class TradeRecord(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    trade_id: int
    entry_index: int | None = None
    exit_index: int | None = None
    entry_time: str | None = None
    exit_time: str | None = None
    direction: str = "Long"
    size: float = 0.0
    entry_price: float = 0.0
    exit_price: float = 0.0
    pnl: float = 0.0
    return_percent: float = 0.0


class AnalysisSummary(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    net_profit: float = 0.0
    total_return_percent: float = 0.0
    sharpe_ratio: float = 0.0
    max_drawdown_percent: float = 0.0
    profit_factor: float = 0.0
    win_rate: float = 0.0
    total_trades: int = 0


class WFAResult(BaseModel):
    """One walk-forward window row (WFA optimization or MT5 report)."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    is_start_index: int = 0
    is_end_index: int = 0
    oos_start_index: int = 0
    oos_end_index: int = 0
    test_window_id: int
    is_profit: float = Field(description="In-sample profit (IS)")
    oos_profit: float = Field(description="Out-of-sample profit (OOS)")
    drawdown_percent: float
    profit_factor: float
    win_rate: float
    total_trades: int = Field(description="Total number of trades in the OOS period")
    best_parameters: dict[str, Any]
    regime_shift_score: float = 0.0

class OhlcPoint(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)
    index: int
    timestamp: str | None = None
    open: float
    high: float
    low: float
    close: float

class AnalysisResult(BaseModel):
    """Unified response for Single Backtest, WFA Optimization, and MT5 Report modes."""
    optimization_scores: list[float] = Field(default_factory=list)  
    price_curve: list[EquityPoint] = Field(default_factory=list)
    ohlc_curve: list[OhlcPoint] = Field(default_factory=list)
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    baseline_equity_curve: list[EquityPoint] = Field(default_factory=list)
    execution_mode: str
    summary: dict[str, str] = Field(default_factory=dict)
    equity_curve: list[EquityPoint] = Field(default_factory=list)
    drawdown_curve: list[DrawdownPoint] = Field(default_factory=list)
    trades: list[TradeRecord] = Field(default_factory=list)
    wfa_windows: list[WFAResult] = Field(default_factory=list)
    parameters: dict[str, Any] = Field(default_factory=dict)
    tested_params: list[dict] = Field(default_factory=list)
    tested_profits: list[float] = Field(default_factory=list)
    optimization_results: list[dict] = Field(default_factory=list)

    # Monte Carlo fields (optional)
    original_equity: list[float] = Field(default_factory=list)
    simulated_equities: list[list[float]] = Field(default_factory=list)
    survival_probability: float = 0.0

    # Cluster matrix: list of rows where keys are columns (e.g. {"Windows":5, "IS_30": 100})
    cluster_matrix: list[dict[str, Any]] = Field(default_factory=list)


class MonteCarloResult(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    # Simple arrays for Monte Carlo plotting
    original_equity: list[float] = Field(default_factory=list)
    simulated_equities: list[list[float]] = Field(default_factory=list)
    survival_probability: float = 0.0

class AnalyzeJobStatusResponse(BaseModel):
    """Polling payload from GET /api/v1/analyze/status/{job_id}."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    progress: int = Field(ge=0, le=100)
    status: str
    is_complete: bool
    result: AnalysisResult | MonteCarloResult | None = None
    error: str | None = None


class StrategyParameterRow(BaseModel):
    """One optimization input row returned by GET /api/v1/strategy/parameters/{filename}."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
    )

    variable: str
    default_value: Any
    start: Any
    step: Any
    stop: Any
