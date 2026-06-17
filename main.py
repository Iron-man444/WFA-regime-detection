from __future__ import annotations
from wfa_engine import run_monte_carlo_from_trades
import uuid
# Eğer wfa_engine.py içinde tanımlıysa:
from wfa_engine import run_monte_carlo_from_trades, run_wfa_cluster
from typing import Any
from pydantic import BaseModel
from data_loader import download_and_save_data
from fastapi import BackgroundTasks, FastAPI, HTTPException, UploadFile, File
from pydantic import BaseModel
from pydantic import ConfigDict
from pydantic.alias_generators import to_camel
from fastapi.middleware.cors import CORSMiddleware

from data_loader import get_market_data
from payload_schemas import WfaRequestPayload
from schemas import (
    AnalyzeJobStartResponse,
    AnalyzeJobStatusResponse,
    AnalysisResult,
    MonteCarloResult,
    StrategyParameterRow,
    WFAResult,
)
from strategy_loader import load_strategy_parameters
from wfa_engine import (
    _wfa_windows_to_analysis_result,
    build_portfolio,
    run_monte_carlo,
    run_single_backtest,
    run_vectorbt_wfa,
)
from mt5_report_parser import parse_mt5_report
from pathlib import Path

app = FastAPI(title="Modular WFA API", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)

active_jobs: dict[str, dict[str, Any]] = {}


def _update_job(job_id: str, *, progress: int | None = None, status: str | None = None, **fields: Any) -> None:
    job = active_jobs.get(job_id)
    if job is None:
        return
    if progress is not None:
        job["progress"] = max(0, min(100, int(progress)))
    if status is not None:
        job["status"] = status
    job.update(fields)


def _derive_trading_exec_params(payload: WfaRequestPayload, mean_close: float) -> tuple[float, float, float]:
    init = float(payload.initial_deposit)
    comm = float(payload.commission)
    slip_pts = float(payload.slippage)

    notional = max(float(mean_close) * 100_000.0, 1.0)
    fee = comm / notional if comm > 0.2 else comm
    fee = max(min(fee, 0.05), 0.0)
    slip_frac = max(slip_pts * 1.0e-5, 0.0)
    return init, fee, slip_frac


def _input_parameters_for_engine(payload: WfaRequestPayload) -> list[dict[str, Any]]:
    if not payload.input_parameters:
        return []
    return [
        {
            "variable": row.variable,
            "value": row.value,
            "start": row.start,
            "step": row.step,
            "stop": row.stop,
            "optimize": row.optimize,
        }
        for row in payload.input_parameters
    ]

class DataDownloadRequest(BaseModel):
    asset_class: str  # "Crypto", "MT5", veya "Stocks"
    symbol: str
    timeframe: str
    start_date: str
    end_date: str

@app.post("/download-data") # Kendi app router'ınıza göre uyarlayın
def api_download_historical_data(req: DataDownloadRequest):
    try:
        saved_path = download_and_save_data(
            req.asset_class, req.symbol, req.timeframe, req.start_date, req.end_date
        )
        return {"status": "success", "file_path": saved_path}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))
    

def _loader_kwargs(payload: WfaRequestPayload) -> dict[str, str | None]:
    return {
        "data_source_type": payload.data_source_type,
        "file_path": payload.file_path,
        "asset_class": payload.asset_class,
        "symbol": payload.symbol,
        "timeframe": payload.timeframe,
    }


def _run_mt5_report_job(
    job_id: str,
    report_path: str,
    progress_callback: Any,
) -> AnalysisResult:
    progress_callback(10, "Opening MT5 report...")
    progress_callback(40, "Parsing MT5 report...")
    windows = parse_mt5_report(report_path)
    progress_callback(85, "Building analysis result...")
    return _wfa_windows_to_analysis_result(windows, execution_mode="MT5Report")


def _run_analyze_job(job_id: str, payload: WfaRequestPayload) -> None:
    """Background worker routed by ``execution_mode``."""

    def progress_callback(pct: int, msg: str) -> None:
        _update_job(job_id, progress=pct, status=msg)

    try:
        _update_job(job_id, progress=0, status="Starting...")
        mode = payload.execution_mode

        if mode == "MT5Report":
            report_path = (payload.file_path or "").strip()
            if not report_path:
                raise ValueError("file_path (MT5 report) is required for MT5Report mode.")
            result = _run_mt5_report_job(job_id, report_path, progress_callback)
        else:
            strategy_name = (payload.strategy_name or "").strip()
            if not strategy_name:
                raise ValueError("strategy_name is required.")

            progress_callback(5, "Loading data...")
            ohlcv = get_market_data(_loader_kwargs(payload))

            progress_callback(12, "Preparing portfolio settings...")
            mean_close = float(ohlcv["close"].astype(float).mean())
            init_cash, fee, slippage = _derive_trading_exec_params(payload, mean_close)
            engine_params = _input_parameters_for_engine(payload)

            if mode == "SingleBacktest":
                progress_callback(15, "Running single backtest...")
                result = run_single_backtest(
                    ohlcv=ohlcv,
                    strategy_name=strategy_name,
                    input_parameters=engine_params,
                    init_cash=init_cash,
                    fee=fee,
                    slippage=slippage,
                    progress_callback=progress_callback,
                )
            elif mode == "MonteCarlo":
                progress_callback(15, "Running Monte Carlo robustness simulation...")
                pf = build_portfolio(
                    ohlcv=ohlcv,
                    strategy_name=strategy_name,
                    input_parameters=engine_params,
                    init_cash=init_cash,
                    fee=fee,
                    slippage=slippage,
                )
                result = run_monte_carlo(pf)
            elif mode == "ClusterMatrix":
                progress_callback(5, "Matris hesaplaması başlıyor...")
                # Use matrix inputs from payload when provided, otherwise fallback to defaults
                wc = payload.matrix_windows if getattr(payload, 'matrix_windows', None) else [3,5,7,10]
                isp = payload.matrix_is_percents if getattr(payload, 'matrix_is_percents', None) else [30,40,50,60,70]
                result = run_wfa_cluster(
                    ohlcv=ohlcv,
                    strategy_name=strategy_name,
                    input_parameters=engine_params,
                    window_counts=wc,
                    in_sample_percents=isp,
                    optimization_mode=payload.optimization_mode,
                    progress_callback=progress_callback, # <--- BU SATIRI EKLİYORUZ
                )
            elif mode in ["WFAOptimization", "StandardOptimization"]:
                progress_callback(15, "Running walk-forward optimization...")
                result = run_vectorbt_wfa(
                    ohlcv=ohlcv,
                    strategy_name=strategy_name,
                    optimization_mode=payload.optimization_mode, 
                    input_parameters=engine_params,
                    n_windows=int(payload.window_count),
                    in_sample_percent=float(payload.in_sample_percent),
                    execution_mode=payload.execution_mode,
                    init_cash=init_cash,
                    fee=fee,
                    slippage=slippage,
                    progress_callback=progress_callback,
                )
            else:
                raise ValueError(f"Unsupported execution_mode: {mode}")

        _update_job(
            job_id,
            progress=100,
            status="Complete",
            is_complete=True,
            result=result,
            error=None,
        )
    except Exception as exc:
        _update_job(
            job_id,
            progress=active_jobs[job_id].get("progress", 0),
            status=f"Failed: {exc}",
            is_complete=True,
            result=None,
            error=str(exc),
        )


class Mt5ReportRequest(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)

    file_path: str | None = None


@app.post("/api/v1/parse-mt5-report", response_model=list[WFAResult])
async def parse_mt5_report_endpoint(
    req: Mt5ReportRequest,
    upload_file: UploadFile | None = File(None),
):
    try:
        if upload_file is not None:
            import tempfile
            import os

            suffix = "" if not upload_file.filename else Path(upload_file.filename).suffix
            with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
                content = await upload_file.read()
                tmp.write(content)
                tmp.flush()
                tmp_path = tmp.name
            try:
                results = parse_mt5_report(tmp_path)
            finally:
                try:
                    os.unlink(tmp_path)
                except Exception:
                    pass
        elif req.file_path:
            results = parse_mt5_report(req.file_path)
        else:
            raise HTTPException(status_code=400, detail="Provide `file_path` or upload a file.")

        return results
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except ImportError as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc


@app.get("/api/v1/strategies", response_model=list[str])
def list_strategies() -> list[str]:
    """Return available strategy module names (Python files in strategies/ without .py)."""
    try:
        strategies_dir = Path(__file__).parent / "strategies"
        if not strategies_dir.exists():
            return []
        py_files = [p.stem for p in strategies_dir.glob("*.py") if p.is_file() and p.name != "__init__.py"]
        return sorted(py_files)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@app.get("/api/v1/strategy/parameters/{strategy_filename}", response_model=list[StrategyParameterRow])
def get_strategy_parameters(strategy_filename: str) -> list[StrategyParameterRow]:
    try:
        rows = load_strategy_parameters(strategy_filename)
        return [StrategyParameterRow.model_validate(row) for row in rows]
    except FileNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except (ValueError, ImportError) as exc:
        raise HTTPException(status_code=400, detail=str(exc)) from exc
    except Exception as exc:  # pragma: no cover
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@app.post("/api/v1/analyze", response_model=AnalyzeJobStartResponse)
def analyze(payload: WfaRequestPayload, background_tasks: BackgroundTasks) -> AnalyzeJobStartResponse:
    """Queue an analysis job (single backtest, WFA, or MT5 report) and return a ``job_id``."""
    if payload.execution_mode == "MT5Report":
        if not payload.file_path or not str(payload.file_path).strip():
            raise HTTPException(status_code=400, detail="file_path (MT5 report) is required.")
    else:
        strategy_name = (payload.strategy_name or "").strip()
        if not strategy_name:
            raise HTTPException(status_code=400, detail="strategy_name is required.")

    job_id = str(uuid.uuid4())
    active_jobs[job_id] = {
        "progress": 0,
        "status": "Starting...",
        "is_complete": False,
        "result": None,
        "error": None,
    }
    background_tasks.add_task(_run_analyze_job, job_id, payload)
    return AnalyzeJobStartResponse(job_id=job_id)


@app.get("/api/v1/analyze/status/{job_id}", response_model=AnalyzeJobStatusResponse)
def analyze_status(job_id: str) -> AnalyzeJobStatusResponse:
    job = active_jobs.get(job_id)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job not found: {job_id}")

    return AnalyzeJobStatusResponse(
        progress=int(job.get("progress", 0)),
        status=str(job.get("status", "")),
        is_complete=bool(job.get("is_complete", False)),
        result=job.get("result"),
        error=job.get("error"),
    )
@app.post("/api/v1/analyze/cancel/{job_id}")
def cancel_analysis(job_id: str):
    job = active_jobs.get(job_id)
    if job:
        job["status"] = "Cancelled by user"
        job["is_complete"] = True
        job["error"] = "Cancelled"
        return {"message": "Job cancelled"}
    raise HTTPException(status_code=404, detail="Job not found")


class MonteCarloRequest(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)
    
    # DEĞİŞEN KISIM BURASI: list[TradeRecord] yerine list[dict] yaptık
    trades: list[dict] = [] 
    
    iterations: int = 200

class GenericTrade:
    def __init__(self, dictionary):
        for key, value in dictionary.items():
            # Hem orijinal anahtarı (örn: Pnl) hem de küçük harfli halini (pnl) nesneye ekliyoruz
            # Böylece wfa_engine.py içindeki "trade.pnl" çağrısı kusursuz çalışacak
            setattr(self, key, value)
            setattr(self, key.lower(), value)

@app.post("/api/v1/monte-carlo", response_model=MonteCarloResult)
async def monte_carlo_endpoint(req: MonteCarloRequest):
    try:
        # 1. Sözlükleri (dict) sahte nesnelere (object) dönüştürüyoruz
        trade_objects = [GenericTrade(t) for t in req.trades]
        
        # 2. Motor artık "trade.pnl" diyerek okuyabilecek!
        mc = run_monte_carlo_from_trades(trade_objects, iterations=int(req.iterations))
        return mc
    except Exception as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc

class WfaClusterRequest(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)
    payload: WfaRequestPayload
    window_counts: list[int] = [3,5,7]
    in_sample_percents: list[int] = [40,50,60,70]


@app.post("/api/v1/wfa-cluster")
async def wfa_cluster_endpoint(req: WfaClusterRequest):
    try:
        payload = req.payload
        strategy_name = (payload.strategy_name or "").strip()
        if not strategy_name:
            raise HTTPException(status_code=400, detail="strategy_name is required in payload.")

        ohlcv = get_market_data(_loader_kwargs(payload))
        engine_params = _input_parameters_for_engine(payload)

        rows: list[dict[str, Any]] = []
        for wc in req.window_counts:
            for isp in req.in_sample_percents:
                try:
                    result = run_vectorbt_wfa(
                        ohlcv=ohlcv,
                        strategy_name=strategy_name,
                        input_parameters=engine_params,
                        optimization_mode=payload.optimization_mode,
                        n_windows=wc,
                        in_sample_percent=float(isp),
                        execution_mode="WFAOptimization",
                    )

                    # extract net profit: prefer numeric SummaryModel when available
                    net_profit = 0.0
                    try:
                        if hasattr(result, 'summary') and isinstance(result.summary, dict) and 'Net Profit' in result.summary:
                            s = result.summary.get('Net Profit', '')
                            # strip non-numeric
                            import re
                            m = re.search(r"([-+]?[0-9]*\.?[0-9]+)", s)
                            if m:
                                net_profit = float(m.group(1))
                        else:
                            # fallback to summing wfa windows oos_profit
                            net_profit = sum(float(w.oos_profit) for w in result.wfa_windows)
                    except Exception:
                        net_profit = 0.0

                    rows.append({"window_count": wc, "in_sample_percent": isp, "net_profit": net_profit})
                except Exception as exc:
                    rows.append({"window_count": wc, "in_sample_percent": isp, "net_profit": float('nan')})

        return rows
    except Exception as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc