"""HTTP request bodies for the WFA FastAPI surface."""

from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator, AliasChoices
from pydantic.alias_generators import to_camel


ExecutionMode = Literal["SingleBacktest", "WFAOptimization", "MT5Report", "StandardOptimization", "MonteCarlo", "ClusterMatrix"]


class StrategyInputRow(BaseModel):
    """One optimization input row (POST body / nested in ``input_parameters``)."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        extra="ignore",
    )

    variable: str
    value: str = ""
    start: str | float | int | None = None
    step: str | float | int | None = None
    stop: str | float | int | None = None
    optimize: bool = True

class WfaRequestPayload(BaseModel):
    """POST /api/v1/analyze — accepts camelCase JSON from the WPF client."""

    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        extra="ignore",
    )

    data_source_type: Literal["CSV", "API"] = Field(
        ...,
        description="CSV loads ``file_path`` on the API host; API streams OHLCV from providers.",
    )
    file_path: str | None = Field(default=None, description="Absolute path to OHLCV CSV when using CSV mode.")
    asset_class: str | None = Field(default=None, description="e.g. STOCK, FX, CRYPTO — drives provider selection.")
    symbol: str | None = Field(default=None, description="Ticker / pair symbol for API mode.")
    timeframe: str | None = Field(default=None, description="Bar size, e.g. 1d, 1h.")
    resample_freq: str | None = Field(default=None, description="Optional pandas resample frequency (e.g. '5min', '1h'). Use None for no resampling.")

    execution_mode: ExecutionMode = Field(
        default="WFAOptimization",
        description="SingleBacktest | WFAOptimization | MT5Report | MonteCarlo",
    )

    strategy_name: str | None = None
    start_date: datetime | None = None
    end_date: datetime | None = None
    date_range_preset: str | None = None
    forward_split: str = Field(default="1/2", description="MT5-style OOS fraction label, e.g. 1/2, 1/3.")
    latency_mode: str = Field(default="Zero latency")
    modelling_type: str = Field(default="Every tick")
    currency: str = Field(default="USD")
    leverage: str = Field(default="1:100")
    input_parameters: list[StrategyInputRow] | None = None
    wfa_type: str = "Expanding"
    window_count: int = Field(5, alias="windowCount")
    
    # EKSİK OLAN VE HATAYA SEBEP OLAN SATIRI BURAYA EKLİYORUZ:
    optimization_mode: str = Field("Fast", alias="OptimizationMode")

    initial_deposit: float = Field(default=10_000.0, gt=0)
    commission: float = Field(default=5.0, ge=0, description="Fraction of notional OR $/lot (see server heuristics).")
    slippage: float = Field(default=2.0, ge=0, description="Points (price ticks) — converted server-side.")

    window_count: int = Field(default=5, ge=1, le=200)
    in_sample_percent: float = Field(default=50.0, gt=0.0, lt=100.0)

    # Cluster matrix inputs (optional)
    matrix_windows: list[int] | None = Field(default=None, validation_alias=AliasChoices("MatrixWindows", "matrixWindows", "matrix_windows"))
    matrix_is_percents: list[int] | None = Field(default=None, validation_alias=AliasChoices("MatrixIsPercents", "matrixIsPercents", "matrix_is_percents"))

    
    @model_validator(mode="after")
    def _validate_source(self) -> WfaRequestPayload:
        if self.execution_mode == "MT5Report":
            if not self.file_path or not str(self.file_path).strip():
                raise ValueError("file_path (MT5 report) is required when execution_mode is MT5Report.")
            return self

        if self.data_source_type == "CSV":
            if not self.file_path or not str(self.file_path).strip():
                raise ValueError("file_path is required when data_source_type is CSV.")
        else:
            if not self.symbol or not str(self.symbol).strip():
                raise ValueError("symbol is required when data_source_type is API.")
            if not self.timeframe or not str(self.timeframe).strip():
                raise ValueError("timeframe is required when data_source_type is API.")
        return self
