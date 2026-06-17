"""
Walk-forward analysis (WFA) engine built on VectorBT.

This module wires together:
  * OHLCV ingestion (CSV via pandas)
  * **Dynamic** strategy loading from the strategies directory
  * A rolling Walk-Forward window protocol driven by ``in_sample_percent`` and ``n_windows``
  * Per-window parameter search on in-sample data with **Net Profit** as the objective
  * Out-of-sample evaluation using the winning in-sample parameters
The public entry point is :func:`run_vectorbt_wfa`.
"""
from __future__ import annotations


from schemas import OhlcPoint
from schemas import OhlcPoint



import optuna
from scipy.stats import pearsonr
import logging

# Optuna'nın gereksiz loglarını susturmak için
optuna.logging.set_verbosity(optuna.logging.WARNING)



import os
import math
from concurrent.futures import ProcessPoolExecutor, as_completed
import warnings
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

import numpy as np
import pandas as pd
import vectorbt as vbt

from schemas import (
    AnalysisResult,
    AnalysisSummary,
    DrawdownPoint,
    EquityPoint,
    MonteCarloResult,
    TradeRecord,
    WFAResult,
)
from strategy_loader import load_strategy_module

# Backtest defaults.
_INIT_CASH: float = 100_000.0
_FEE: float = 0.0005
_SLIPPAGE: float = 0.0

ProgressCallback = Callable[[int, str], None]


_WORKER_STRATEGY_CACHE = {}

def _worker_batch_backtest(ohlcv, strategy_name, param_batch, freq, exec_cfg):
    """Her bir işlemci çekirdeğinin kendi içinde çalıştıracağı bağımsız test motoru"""
    global _WORKER_STRATEGY_CACHE
    from strategy_loader import load_strategy_module
    
    # Stratejiyi her testte baştan yüklememek için çekirdek belleğinde (cache) tutuyoruz
    if strategy_name not in _WORKER_STRATEGY_CACHE:
        _WORKER_STRATEGY_CACHE[strategy_name] = load_strategy_module(strategy_name)
    
    strategy_module = _WORKER_STRATEGY_CACHE[strategy_name]
    
    best_params = None
    best_score = -float('inf')
    
    # Çekirdeğe verilen 100-200'lük paketi (batch) test et
    for params in param_batch:
        try:
            pf = _backtest_strategy(ohlcv, strategy_module, params, freq, exec_cfg)
            profit = float(pf.total_profit())
            if np.isfinite(profit) and profit > best_score:
                best_score = profit
                best_params = params
        except Exception:
            continue
            
    return best_params, best_score



def _notify(progress_callback: ProgressCallback | None, pct: int, msg: str) -> None:
    if progress_callback is not None:
        progress_callback(int(pct), msg)


@dataclass(frozen=True, slots=True)
class _PortfolioExec:
    """Immutable execution parameters so concurrent API calls never clobber globals."""

    init_cash: float
    fee: float
    slippage: float


def _load_ohlcv(csv_path: str, resample_freq: str | None = None) -> pd.DataFrame:
    """
    Disk üzerinden veriyi yükler.
    Eğer veri Tick (sadece fiyat) formatındaysa 1 Saniyelik OHLC'ye (1s) resample eder.
    Eğer veri zaten M1 (1 Dakika) veya üstü OHLC ise doğrudan kabul eder.
    Eğer resample_freq belirtilmişse (örn. '5min', '1h'), çıktı bu frekansa yeniden örneklenir.
    """
    df = pd.read_csv(csv_path)
    if df.empty:
        raise ValueError(f"CSV is empty: {csv_path}")

    colmap = {c.lower(): c for c in df.columns}
    
    # Zaman damgasını Index yap (Resampling işlemi için kesinlikle şarttır)
    for ts_name in ("date", "time", "timestamp", "datetime"):
        if ts_name in colmap:
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", category=UserWarning)
                df.index = pd.to_datetime(df[colmap[ts_name]], errors="coerce")
            df = df[~df.index.isna()]
            break

    if df.index.name is None and not isinstance(df.index, pd.DatetimeIndex):
        raise ValueError("Veri setinde geçerli bir zaman damgası (Timestamp) bulunamadı.")

    out: pd.DataFrame

    # SENARYO 1: Veri Tick Formatındaysa (Sadece Fiyat var, Open/High/Low/Close yok)
    if ("open" not in colmap or "close" not in colmap) and ("price" in colmap or "last" in colmap):
        price_col = colmap.get("price", colmap.get("last"))
        vol_col = colmap.get("volume", None)
        
        price_series = pd.to_numeric(df[price_col], errors="coerce")
        
        # Tick verisini 1 Saniyelik (1s) OHLC mumlarına çevir (Resample)
        resampled = price_series.resample("1s").ohlc()
        
        # Hacim varsa saniyelik olarak topla, yoksa sıfır ata
        if vol_col:
            resampled["volume"] = pd.to_numeric(df[vol_col], errors="coerce").resample("1s").sum()
        else:
            resampled["volume"] = 0.0
            
        out = resampled.dropna(subset=["close"])

    else:
        # SENARYO 2: Veri Zaten OHLC Formatındaysa (Örn: M1 Verisi)
        required = ("open", "high", "low", "close", "volume")
        missing = [c for c in required if c not in colmap]
        if missing:
            raise ValueError(f"CSV missing columns {missing}. Found: {list(df.columns)}")

        out = pd.DataFrame(
            {
                "open": pd.to_numeric(df[colmap["open"]], errors="coerce"),
                "high": pd.to_numeric(df[colmap["high"]], errors="coerce"),
                "low": pd.to_numeric(df[colmap["low"]], errors="coerce"),
                "close": pd.to_numeric(df[colmap["close"]], errors="coerce"),
                "volume": pd.to_numeric(df[colmap["volume"]], errors="coerce"),
            },
            index=df.index
        )

        out = out.dropna(subset=["close"])
        if out.empty:
            raise ValueError("No usable rows after parsing OHLCV.")

    # Eğer yeniden örnekleme istenmişse, pandas resample ile dönüştür
    if resample_freq and str(resample_freq).strip().lower() != "none":
        try:
            rf = str(resample_freq).strip()
            # Ensure index is sorted and datetime
            if not isinstance(out.index, pd.DatetimeIndex):
                out.index = pd.to_datetime(out.index)
            out = out.sort_index()
            out = out.resample(rf).agg({
                'open': 'first',
                'high': 'max',
                'low': 'min',
                'close': 'last',
                'volume': 'sum',
            }).dropna(subset=['close'])
        except Exception as exc:
            raise ValueError(f"Resample failed with freq={resample_freq}: {exc}") from exc

    return out


def _infer_freq_or_default(index: pd.Index, default: str = "1d") -> str:
    """
    Pandas'ın frekans bulamadığı durumlarda, tüm zaman dizisini tarayarak
    en çok tekrar eden (mode) zaman farkını bulur. Cuma-Pazartesi boşluklarına aldanmaz.
    """
    if not isinstance(index, pd.DatetimeIndex):
        try:
            index = pd.to_datetime(index)
        except Exception:
            return default

    if len(index) < 2:
        return default

    try:
        # Bütün mumlar arasındaki zaman farklarını hesapla
        diffs = index.to_series().diff().dropna()
        if not diffs.empty:
            # En çok tekrar eden zaman farkını (Mode) al
            most_common_diff = diffs.mode().iloc[0]
            delta_seconds = most_common_diff.total_seconds()

            if delta_seconds >= 86400:
                return f"{int(delta_seconds // 86400)}d"
            if delta_seconds >= 3600:
                return f"{int(delta_seconds // 3600)}h"
            if delta_seconds >= 60:
                return f"{int(delta_seconds // 60)}min"
            if delta_seconds > 0:
                return f"{int(delta_seconds)}s"
    except Exception:
        pass

    return default


def _coerce_param_value(value: Any) -> Any:
    if value is None:
        return None
    if isinstance(value, (int, float, bool)):
        return value
    if isinstance(value, str):
        stripped = value.strip()
        if not stripped:
            return stripped
        try:
            num = float(stripped)
            if abs(num - round(num)) < 1e-9:
                return int(round(num))
            return num
        except ValueError:
            return stripped
    return value


def _build_fixed_params(input_parameters: list[dict[str, Any]]) -> dict[str, Any]:
    """Single backtest: use the Value column only (ignore optimize ranges)."""
    params: dict[str, Any] = {}
    for param in input_parameters:
        variable = param.get("variable", "")
        if not variable:
            continue
        params[variable] = _coerce_param_value(param.get("value"))
    return params


def _timestamp_label(index: pd.Index, position: int) -> str | None:
    if not isinstance(index, pd.DatetimeIndex):
        return None
    if position < 0 or position >= len(index):
        return None
    ts = index[position]
    if pd.isna(ts):
        return None
    return ts.isoformat()


def _downsample_series(series: pd.Series, max_points: int = 2000) -> pd.Series:
    if len(series) <= max_points:
        return series
    step = max(len(series) // max_points, 1)
    return series.iloc[::step]


def _series_to_equity_curve(equity: pd.Series) -> list[EquityPoint]:
    equity = _downsample_series(equity.astype(float))
    index = equity.index
    points: list[EquityPoint] = []
    for i, (idx, value) in enumerate(equity.items()):
        pos = index.get_loc(idx)
        bar_index = int(pos) if isinstance(pos, (int, np.integer)) else int(i)
        points.append(
            EquityPoint(
                index=bar_index,
                timestamp=_timestamp_label(index, bar_index),
                value=float(value),
            )
        )
    return points


def _series_to_drawdown_curve(drawdown: pd.Series) -> list[DrawdownPoint]:
    dd_pct = drawdown.astype(float).abs() * 100.0
    dd_pct = _downsample_series(dd_pct)
    index = dd_pct.index
    points: list[DrawdownPoint] = []
    for i, (idx, value) in enumerate(dd_pct.items()):
        pos = index.get_loc(idx)
        bar_index = int(pos) if isinstance(pos, (int, np.integer)) else int(i)
        points.append(
            DrawdownPoint(
                index=bar_index,
                timestamp=_timestamp_label(index, bar_index),
                value=float(value),
            )
        )
    return points


def _safe_float(value: Any, default: float = 0.0) -> float:
    try:
        val = float(value)
        return val if np.isfinite(val) else default
    except Exception:
        return default


def _build_parameter_combinations(input_parameters: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """
    Build all parameter combinations from the input_parameters list.
    
    For each parameter:
      - If optimize=False, use the value as a single-element list
      - If optimize=True, generate a range using start, step, stop
    
    Returns a list of all valid combinations (Cartesian product).
    """
    if not input_parameters:
        return [{}]
    
    param_ranges = []
    
    for param in input_parameters:
        variable = param.get("variable", "")
        if not variable:
            continue
        
        optimize = param.get("optimize", False)
        
        if not optimize:
            # Use the value as-is
            value = param.get("value")
            try:
                # Try to convert to numeric if possible
                if isinstance(value, str):
                    try:
                        value = float(value)
                        if value == int(value):
                            value = int(value)
                    except (ValueError, TypeError):
                        pass
            except Exception:
                pass
            param_ranges.append({variable: [value]})
        else:
            # Generate range from start, step, stop
            try:
                start = float(param.get("start", 0))
                step = float(param.get("step", 1))
                stop = float(param.get("stop", start + 1))
                
                if step <= 0:
                    raise ValueError(f"Parameter {variable}: step must be positive, got {step}")
                
                # Generate range
                values = []
                current = start
                while current <= stop + 1e-9:  # Small epsilon for floating point
                    # Try to use int if it's a whole number
                    val = current
                    if abs(val - round(val)) < 1e-9:
                        val = int(round(val))
                    values.append(val)
                    current += step
                
                if not values:
                    values = [start]
                
                param_ranges.append({variable: values})
            except (ValueError, TypeError) as e:
                raise ValueError(f"Invalid parameter range for {variable}: {e}")
    
    # Generate Cartesian product
    if not param_ranges:
        return [{}]
    
    # Start with first parameter
    combinations = [{}]
    for param_dict in param_ranges:
        var_name = list(param_dict.keys())[0]
        values = param_dict[var_name]
        
        new_combinations = []
        for combo in combinations:
            for value in values:
                new_combo = combo.copy()
                new_combo[var_name] = value
                new_combinations.append(new_combo)
        combinations = new_combinations
    
    return combinations


def _backtest_strategy(
    ohlcv_segment: pd.DataFrame,
    strategy_module: Any,
    params: dict[str, Any],
    freq: str,
    exec_cfg: _PortfolioExec,
) -> vbt.Portfolio:
    strategy_result = strategy_module.execute_strategy(ohlcv_segment, params)
    
    def _get_series(key):
        s = strategy_result.get(key)
        if s is not None:
            return pd.Series(s, index=ohlcv_segment.index).astype(bool)
        return pd.Series(False, index=ohlcv_segment.index)
    
    entries = _get_series("entries")
    exits = _get_series("exits")
    short_entries = _get_series("short_entries")
    short_exits = _get_series("short_exits")
    
    close = ohlcv_segment["close"].astype(float)
    
    # DİKKAT: Argümanlar kesinlikle isimleriyle eşleştirilmelidir (kwargs)
    pf = vbt.Portfolio.from_signals(
        close=close,
        entries=entries,
        exits=exits,
        short_entries=short_entries,
        short_exits=short_exits,
        freq=freq,
        init_cash=exec_cfg.init_cash,
        fees=exec_cfg.fee,
        slippage=exec_cfg.slippage,
        direction="both",
        call_seq="auto",
        size=100000,          # Sabit Hacim (0 PNL sorununu çözer)
        size_type="amount"
    )
    return pf


def _build_wfa_slices(
    wfa_type: str,
    n_rows: int,
    n_windows: int,
    in_sample_percent: float,
    warmup_bars: int,
) -> list[tuple[slice, slice]]:
    """
    Produce ``n_windows`` (in_sample, oos_sample) slice pairs.
    Supports both 'Expanding' (Anchor) and 'Rolling' (Sliding) window methods.
    """
    if n_windows < 1:
        raise ValueError("n_windows must be >= 1.")
    
    frac = float(in_sample_percent) / 100.0
    max_t0 = n_rows - n_windows
    t0 = int(max(warmup_bars, min(int(np.ceil(frac * n_rows)), max_t0)))
    tail = n_rows - t0
    
    while tail < n_windows and t0 > warmup_bars:
        t0 -= 1
        tail = n_rows - t0
        
    if tail < n_windows:
        raise ValueError("Veri bu kadar çok pencere/OOS için yetersiz. Pencere sayısını azaltın.")

    step = tail // n_windows
    is_length = t0 # Sabit In-Sample boyutu (Rolling için)
    slices: list[tuple[slice, slice]] = []
    
    for k in range(n_windows):
        oos_start = t0 + k * step
        oos_end = t0 + (k + 1) * step if k < n_windows - 1 else n_rows
        
        if str(wfa_type).strip().lower() == "rolling":
            # KAYAN PENCERE (Rolling): IS boyutu sabittir, pencere ileri kayar.
            is_start = k * step
            is_end = oos_start
            # Başlangıçta yeterli warmup barı yoksa sıfıra yasla
            if is_end - is_start < warmup_bars:
                is_start = 0 
        else:
            # GENİŞLEYEN PENCERE (Expanding): IS hep en baştan (0) başlar.
            is_start = 0
            is_end = oos_start
            
        slices.append((slice(is_start, is_end), slice(oos_start, oos_end)))
        
    return slices


def _calculate_hurst(ts: np.ndarray, max_lag: int = 20) -> float:
    """
    Zaman serisinin Hurst Üssü'nü (H) hesaplar.
    """
    if len(ts) < max_lag * 2:
        return 0.5
    lags = range(2, max_lag)
    tau = [np.sqrt(np.std(np.subtract(ts[lag:], ts[:-lag]))) for lag in lags]
    tau = [t if t > 0 else 1e-8 for t in tau]
    m = np.polyfit(np.log(lags), np.log(tau), 1)
    hurst = m[0] * 2.0
    return float(max(0.0, min(1.0, hurst)))

def _calculate_cri_series(close_series: pd.Series, window: int = 60) -> pd.Series:
    """
    Sürekli Rejim Endeksi (CRI) hesaplar. [-1, +1] aralığında değer üretir.
    -1'e yakın: Güçlü Ortalamaya Dönüş (Yatay)
    +1'e yakın: Güçlü Trend Eğilimi
    """
    prices = close_series.values
    n = len(prices)
    
    rolling_mean = close_series.rolling(window=window, min_periods=window).mean().values
    rolling_std = close_series.rolling(window=window, min_periods=window).std().values
    
    cri_values = np.zeros(n)
    sigma_h = 0.1  # Hurst standart sapma sabiti
    
    for i in range(window, n):
        window_slice = prices[i-window:i]
        h = _calculate_hurst(window_slice, max_lag=min(20, window//2))
        
        std_val = rolling_std[i-1] if rolling_std[i-1] > 0 else 1e-8
        z_score = (prices[i] - rolling_mean[i-1]) / std_val
        
        # CRI = tanh((H - 0.5) / sigma_H) * min(|Z|, 3)
        cri = math.tanh((h - 0.5) / sigma_h) * min(abs(z_score), 3.0)
        
        # -1 ile 1 arasına normalize etmek için max Z (3) değerine bölüyoruz
        cri_values[i] = cri / 3.0
        
    return pd.Series(cri_values, index=close_series.index)

def _build_markov_matrix(cri_series: pd.Series) -> np.ndarray:
    """
    CRI serisini 3 duruma böler ve 3x3 Markov Geçiş Olasılık Matrisi oluşturur.
    Durum 0: Yatay, Durum 1: Kaos, Durum 2: Trend
    """
    # CRI değerlerini 3 faza ayır (Discretization)
    bins = [-1.1, -0.3, 0.3, 1.1]
    labels = [0, 1, 2]
    
    try:
        states = pd.cut(cri_series, bins=bins, labels=labels).dropna().astype(int).values
    except Exception:
        return np.ones((3, 3)) / 3.0 # Hata olursa eşit dağılım dön
        
    transitions = np.zeros((3, 3))
    if len(states) < 2:
        return np.ones((3, 3)) / 3.0
        
    for (i, j) in zip(states[:-1], states[1:]):
        transitions[i, j] += 1
        
    # Satırları olasılığa (0-1 arası) çevir (Row-wise normalization)
    row_sums = transitions.sum(axis=1)
    for i in range(3):
        if row_sums[i] > 0:
            transitions[i, :] /= row_sums[i]
        else:
            # Eğer o durum hiç yaşanmadıysa (örneğin hiç trend olmadıysa), homojen dağıt
            transitions[i, :] = 1.0 / 3.0
            
    return transitions

def _calculate_kl_divergence(p: np.ndarray, q: np.ndarray) -> float:
    """
    Kullback-Leibler (KL) Iraksaması.
    In-Sample (P) ile Out-of-Sample (Q) matrisleri arasındaki yapısal farkı ölçer.
    """
    epsilon = 1e-8
    p_safe = np.clip(p, epsilon, 1.0)
    q_safe = np.clip(q, epsilon, 1.0)
    
    # Tekrar normalize et
    p_safe = p_safe / p_safe.sum(axis=1, keepdims=True)
    q_safe = q_safe / q_safe.sum(axis=1, keepdims=True)
    
    kl_div = np.sum(p_safe * np.log(p_safe / q_safe))
    return float(kl_div)



def _trade_count(trades: Any) -> int:
    """Normalize VectorBT trade counting across versions (callable vs scalar vs sized)."""
    raw = getattr(trades, "count", None)
    if callable(raw):
        try:
            return int(raw())
        except Exception:
            pass
    if isinstance(raw, (int, np.integer)):
        return int(raw)
    try:
        return int(len(trades))
    except Exception:
        return 0


def _profit_factor_from_trades(pf: vbt.Portfolio) -> float:
    """
    Profit factor = gross profits / gross losses (losses as positive denominator).

    Uses VectorBT helpers when present; otherwise falls back to record arrays.
    """
    trades = pf.trades
    n = _trade_count(trades)

    if n == 0:
        return 0.0

    # VectorBT >= 0.25 typically exposes profit_factor on the trades accessor.
    pf_attr = getattr(trades, "profit_factor", None)
    if callable(pf_attr):
        try:
            val = float(pf_attr())
            if np.isfinite(val):
                return val
        except Exception:
            pass

    # Fallback: derive from signed PnL aggregates.
    try:
        pnl = trades.pnl.to_numpy(dtype=float, copy=False)
    except Exception:
        rec = getattr(trades, "records", None)
        if rec is None or not hasattr(rec, "pnl"):
            return 0.0
        pnl = np.asarray(rec.pnl, dtype=float)

    wins = pnl[pnl > 0].sum()
    losses = -pnl[pnl < 0].sum()
    if losses == 0.0:
        return float("inf") if wins > 0 else 0.0
    return float(wins / losses)


def _win_rate_from_trades(pf: vbt.Portfolio) -> float:
    trades = pf.trades
    n = _trade_count(trades)
    if n == 0:
        return 0.0

    wr_attr = getattr(trades, "win_rate", None)
    if callable(wr_attr):
        try:
            return float(wr_attr())
        except Exception:
            pass

    try:
        pnl = trades.pnl.to_numpy(dtype=float, copy=False)
    except Exception:
        rec = getattr(trades, "records", None)
        if rec is None or not hasattr(rec, "pnl"):
            return 0.0
        pnl = np.asarray(rec.pnl, dtype=float)

    return float((pnl > 0).mean()) if pnl.size else 0.0


def _max_drawdown_percent(pf: vbt.Portfolio) -> float:
    """
    Convert VectorBT's max drawdown scalar into a positive percentage for the API contract.

    VectorBT convention: ``max_drawdown`` is usually a **positive** fraction (0.25 == 25%).
    We defensively handle signed inputs as well.
    """
    mdd = float(pf.max_drawdown())
    if not np.isfinite(mdd):
        return 0.0
    mdd = abs(mdd)
    # Heuristic: values in (-1, 1) are treated as fractional; already-percent values pass through.
    return mdd * 100.0 if mdd <= 1.0 else mdd


def _extract_net_profit_max_dd_pf_win(pf: vbt.Portfolio) -> tuple[float, float, float, float, int]:
    """Bundle the headline metrics we surface to the C# adapter."""
    net_profit = float(pf.total_profit())
    dd_pct = _max_drawdown_percent(pf)
    pf_factor = _profit_factor_from_trades(pf)
    win_rate = _win_rate_from_trades(pf)
    trade_count = _trade_count(pf.trades)

    # ``inf`` is JSON-hostile and meaningless to most UIs – clamp to a large finite sentinel.
    if not np.isfinite(pf_factor) or pf_factor == float("inf"):
        pf_factor = 999.0 if pf_factor == float("inf") else 0.0

    return net_profit, dd_pct, pf_factor, win_rate, trade_count

def _evaluate_fitness(pf: vbt.Portfolio, criterion: str, cri_series: pd.Series = None, strategy_nature: str = "trend") -> float:
    criterion = criterion.strip().lower()
    
    # 11. YENİ EKLENEN: Rejim Uyumlu Özkaynak Puanı (RAFS) ve Günlük Risk Kontrolü
    if "rafs" in criterion or "regime-aligned" in criterion:
        net_profit = float(pf.total_profit())
        if net_profit <= 0: return -9999.0
        
        trades_df = pf.trades.records_readable
        if len(trades_df) < 5: return -9999.0
        
        # RİSK KONTROLÜ: Toplam günlük kayıp analizi (Gün içindeki tüm kâr ve zararların net toplamı)
        try:
            exit_times = pd.to_datetime(trades_df.get("Exit Index", trades_df.get("exit_idx")))
            daily_totals = trades_df.groupby(exit_times.dt.date)['PnL'].sum()
            
            # Eğer herhangi bir gün, toplam net PnL -$500 sınırını aştıysa bu geni tamamen ele
            if daily_totals.min() < -500.0:
                return -9999.0 
        except Exception:
            pass

        # RAFS MATEMATİĞİ: İşlemlerin yapıldığı rejimlere (CRI) göre ağırlıklandırılması
        try:
            entries = pd.to_datetime(trades_df.get("Entry Index", trades_df.get("entry_idx")))
            weighted_pnl_sum = 0.0
            
            for idx, row in trades_df.iterrows():
                entry_time = entries[idx]
                pnl = float(row.get("PnL", 0.0))
                
                # İşleme girildiği andaki CRI (Rejim) değerini bul
                try:
                    entry_cri = float(cri_series.loc[entry_time])
                except Exception:
                    entry_cri = 0.0
                
                # Ağırlık formülü: Trend stratejisi ise +CRI ödüllendirilir, Yatay ise -CRI ödüllendirilir
                if strategy_nature == "trend":
                    weight = 1.0 + entry_cri
                else: # mean_reversion
                    weight = 1.0 - entry_cri
                    
                # Eğer kârlı bir işlem yanlış rejimde yapıldıysa cezalandır (ağırlık < 1 olur)
                # Eğer zararlı bir işlem ise ve yanlış rejimdeyse zararı daha da büyütülerek cezalandırılır
                weighted_pnl_sum += pnl * weight
                
            pnls = np.array(trades_df['PnL'].astype(float))
            std_pnl = float(np.std(pnls, ddof=1)) if len(pnls) > 1 else 1e-8
            if std_pnl == 0: std_pnl = 1e-8
            
            rafs = (math.sqrt(len(trades_df)) / std_pnl) * weighted_pnl_sum
            return rafs
            
        except Exception as e:
            return -9999.0
        
    """
    MT5 tarzı optimizasyon puanlama (fitness) fonksiyonu.
    Gelen kriter adına göre portföyün başarısını tek bir float değere dönüştürür.
    """
    criterion = criterion.strip().lower()
    
    # 1. Balance max (Net Kâr)
    if "balance" in criterion:
        return float(pf.total_profit())
        
    # 2. Profit Factor max
    elif "profit factor" in criterion:
        pf_val = _profit_factor_from_trades(pf)
        return pf_val if np.isfinite(pf_val) else 0.0
        
    # 3. Expected Payoff max (Beklenen Getiri = Toplam Kâr / İşlem Sayısı)
    elif "payoff" in criterion:
        trades_count = max(1, _trade_count(pf.trades))
        return float(pf.total_profit()) / trades_count
        
    # 4. Drawdown min (Minimize etmek için eksi ile çarpıyoruz ki en yüksek değer en düşük DD olsun)
    elif "drawdown" in criterion:
        dd = _max_drawdown_percent(pf)
        return -1.0 * dd
        
    # 5. Recovery Factor max (Toparlanma Faktörü = Net Kâr / Max Drawdown)
    elif "recovery" in criterion:
        profit = float(pf.total_profit())
        dd = _max_drawdown_percent(pf)
        if profit <= 0: return 0.0
        return profit / max(0.01, dd) # Sıfıra bölünme hatasını önlemek için 0.01
        
    # 6. Sharpe Ratio max
    elif "sharpe" in criterion:
        try:
            sr = float(pf.sharpe_ratio())
            return sr if np.isfinite(sr) else 0.0
        except Exception:
            return 0.0

    # 7. System Quality Number (SQN) max
    elif "sqn" in criterion or "system quality number" in criterion:
        trade_count = _trade_count(pf.trades)
        if trade_count < 5:
            return 0.0

        index = pd.Index([])
        if hasattr(pf, "wrapper") and getattr(pf.wrapper, "index", None) is not None:
            index = pf.wrapper.index

        trades = _extract_trades(pf, index)
        pnls = np.array([_safe_float(trade.pnl) for trade in trades], dtype=float)
        if pnls.size == 0:
            return 0.0

        std_pnl = float(np.std(pnls, ddof=1))
        if std_pnl == 0.0:
            return 0.0

        mean_pnl = float(np.mean(pnls))
        return math.sqrt(trade_count) * (mean_pnl / std_pnl)

    # 8. Sortino Ratio max
    elif "sortino" in criterion:
        try:
            so = float(pf.sortino_ratio())
            return so if np.isfinite(so) else 0.0
        except Exception:
            return 0.0

    # 9. Custom Composite Objective max
    elif "custom composite" in criterion:
        profit = float(pf.total_profit())
        try:
            sortino = float(pf.sortino_ratio())
            sortino = sortino if np.isfinite(sortino) and sortino > 0 else 0.1
        except Exception:
            sortino = 0.1

        dd = max(0.01, _max_drawdown_percent(pf))
        return (profit * sortino) / dd

    # 10. Complex Criterion max (MT5'in özel formülü: Kâr * Sharpe / Drawdown)
    elif "complex" in criterion:
        profit = float(pf.total_profit())
        if profit <= 0: return 0.0
        
        try:
            sr = float(pf.sharpe_ratio())
            sr = sr if np.isfinite(sr) and sr > 0 else 0.1
        except Exception:
            sr = 0.1
            
        dd = max(0.01, _max_drawdown_percent(pf))
        return (profit * sr) / dd

    # Varsayılan (Bulamazsa Balance döner)
    return float(pf.total_profit())

def _extract_trades(pf: vbt.Portfolio, index: pd.Index, offset: int = 0) -> list[TradeRecord]:
    trades: list[TradeRecord] = []
    
    if getattr(pf, "trades", None) is None:
        return trades

    try:
        raw_records = None
        
        # Sürüm farklılıklarına karşı VectorBT'nin HAM (raw) numpy dizisini buluyoruz
        if hasattr(pf.trades, "values") and isinstance(pf.trades.values, np.ndarray):
            raw_records = pf.trades.values
        elif hasattr(pf.trades, "records_arr"):
            raw_records = pf.trades.records_arr
        elif hasattr(pf.trades, "records") and hasattr(pf.trades.records, "values"):
            raw_records = pf.trades.records.values
            
        if raw_records is None or len(raw_records) == 0:
            return trades
            
        for i in range(len(raw_records)):
            record = raw_records[i]
            
            # Pandas'ı (DataFrame) aradan çıkardığımız için 
            # anahtarlar asla değişmez ve her zaman tam isabetle okunur.
            abs_entry = int(record['entry_idx']) + offset
            abs_exit = int(record['exit_idx']) + offset
            
            # VectorBT çekirdeğinde 0=Long, 1=Short'tur.
            direction = "Short" if record['direction'] == 1 else "Long"
            
            pnl = float(record['pnl'])
            ret = float(record['return'])
            size = float(record['size'])
            entry_price = float(record['entry_price'])
            exit_price = float(record['exit_price'])
            
            trades.append(
                TradeRecord(
                    trade_id=i + 1,
                    entry_index=abs_entry,
                    exit_index=abs_exit,
                    entry_time=_timestamp_label(index, abs_entry),
                    exit_time=_timestamp_label(index, abs_exit),
                    direction=direction,
                    size=size,
                    entry_price=entry_price,
                    exit_price=exit_price,
                    pnl=pnl,
                    return_percent=ret * 100.0 if abs(ret) <= 1.0 else ret,
                )
            )
    except Exception as e:
        import traceback
        traceback.print_exc()

    return trades

def _portfolio_to_analysis_result(
    pf: vbt.Portfolio,
    ohlcv: pd.DataFrame,
    *,
    execution_mode: str,
    parameters: dict[str, Any] | None = None,
    wfa_windows: list[WFAResult] | None = None,
) -> AnalysisResult:
    net_profit, dd_pct, pf_factor, win_rate, trade_count = _extract_net_profit_max_dd_pf_win(pf)

    sharpe = 0.0
    try:
        sharpe = _safe_float(pf.sharpe_ratio())
    except Exception:
        pass

    total_return_pct = 0.0
    try:
        total_return_pct = _safe_float(pf.total_return()) * 100.0
    except Exception:
        pass

    equity_series = pf.value()
    if isinstance(equity_series, pd.DataFrame):
        equity_series = equity_series.iloc[:, 0]

    drawdown_series = pf.drawdown()
    if isinstance(drawdown_series, pd.DataFrame):
        drawdown_series = drawdown_series.iloc[:, 0]

    summary = {
        "Net Profit": f"{net_profit:.2f} $",
        "Total Return": f"{total_return_pct:.2f} %",
        "Sharpe Ratio": f"{sharpe:.2f}",
        "Max Drawdown": f"{dd_pct:.2f} %",
        "Profit Factor": f"{pf_factor:.2f}",
        "Win Rate": f"{win_rate:.2f} %",
        "Total Trades": str(trade_count),
        "Execution Mode": execution_mode
    }

    # TEKLİ TEST DÜZELTMESİ: 
    # Eğer wfa_windows boşsa ve SingleBacktest ise, arayüzdeki tablonun dolması için 
    # genel sonuçları 1. Pencere (Window 1) olarak listeye ekle.
    if execution_mode == "SingleBacktest" and not wfa_windows:
        wfa_windows = [
            WFAResult(
                test_window_id=1,
                is_profit=float(net_profit),
                oos_profit=float(net_profit),
                drawdown_percent=float(dd_pct),
                profit_factor=float(pf_factor),
                win_rate=float(win_rate),
                total_trades=int(trade_count),
                best_parameters=parameters or {}
            )
        ]

    return AnalysisResult(
        execution_mode=execution_mode,
        summary=summary,
        price_curve=_series_to_equity_curve(ohlcv["close"]), 
        equity_curve=_series_to_equity_curve(equity_series),
        drawdown_curve=_series_to_drawdown_curve(drawdown_series),
        trades=_extract_trades(pf, ohlcv.index),
        wfa_windows=wfa_windows or [],
        parameters=parameters or {},
    )


def _wfa_windows_to_analysis_result(
    windows: list[WFAResult],
    *,
    ohlc_curve: list | None = None,
    summary: dict[str, str] | None = None,
    execution_mode: str = "WFAOptimization",
    all_trades: list[TradeRecord] | None = None,
    baseline_equity_curve: list | None = None,
    price_curve: list | None = None,
    tested_params: list[dict[str, Any]] | None = None,
    tested_profits: list[float] | None = None,
    optimization_results: list[dict[str, Any]] | None = None,
) -> AnalysisResult:
    # KORUMA: Eğer pencere veya test sonucu yoksa boş değerlerle güvenli çıkış yap
    if not windows:
        return AnalysisResult(
            execution_mode=execution_mode,
            summary=summary or {},
            equity_curve=[],
            drawdown_curve=[],
            trades=all_trades or [],
            wfa_windows=[],
            parameters={},
            baseline_equity_curve=baseline_equity_curve or [],
            price_curve=price_curve or [],
            tested_params=tested_params or [],
            tested_profits=tested_profits or [],
            optimization_results=optimization_results or [],
        )

    ordered = sorted(windows, key=lambda row: row.test_window_id)
    cumulative = 0.0
    equity_points: list[EquityPoint] = []
    drawdown_points: list[DrawdownPoint] = []
    peak = 0.0

    for row in ordered:
        cumulative += float(row.oos_profit)
        equity_points.append(
            EquityPoint(index=row.test_window_id, timestamp=None, value=cumulative)
        )
        peak = max(peak, cumulative)
        dd_value = ((peak - cumulative) / peak * 100.0) if peak > 0 else 0.0
        drawdown_points.append(
            DrawdownPoint(index=row.test_window_id, timestamp=None, value=dd_value)
        )

    return AnalysisResult(
        execution_mode=execution_mode,
        summary=summary or {},
        equity_curve=equity_points,
        drawdown_curve=drawdown_points,
        trades=all_trades or [],
        wfa_windows=ordered,
        parameters=ordered[-1].best_parameters if ordered else {},
        baseline_equity_curve=baseline_equity_curve or [],
        price_curve=price_curve or [],
        tested_params=tested_params or [],
        tested_profits=tested_profits or [],
        optimization_results=optimization_results or [],
    )


def run_single_backtest(
    *,
    ohlcv: pd.DataFrame | None = None,
    csv_path: str | None = None,
    strategy_name: str | None = None,
    input_parameters: list[dict[str, Any]] | None = None,
    init_cash: float = _INIT_CASH,
    fee: float = _FEE,
    slippage: float = _SLIPPAGE,
    resample_freq: str | None = None,
    progress_callback: ProgressCallback | None = None,
) -> AnalysisResult:
    """Run one full-sample backtest using fixed parameter values (no walk-forward windows)."""
    if (ohlcv is None) == (csv_path is None):
        raise ValueError("Provide exactly one of ``ohlcv`` or ``csv_path``.")

    if not strategy_name:
        raise ValueError("strategy_name is required.")

    if csv_path is not None:
        _notify(progress_callback, 10, "Loading OHLCV data...")
        ohlcv = _load_ohlcv(csv_path)
    else:
        _notify(progress_callback, 10, "Preparing OHLCV data...")
    assert ohlcv is not None

    _notify(progress_callback, 25, "Loading strategy module...")
    strategy_module = load_strategy_module(strategy_name)
    if not hasattr(strategy_module, "execute_strategy"):
        raise ValueError(f"Strategy {strategy_name} does not have an execute_strategy function.")

    _notify(progress_callback, 40, "Applying strategy parameters...")
    params = _build_fixed_params(input_parameters or [])

    exec_cfg = _PortfolioExec(init_cash=float(init_cash), fee=float(fee), slippage=float(slippage))
    freq = _infer_freq_or_default(ohlcv["close"].index)

    _notify(progress_callback, 60, "Running single backtest...")
    pf = _backtest_strategy(ohlcv, strategy_module, params, freq, exec_cfg)

    _notify(progress_callback, 90, "Building equity curve and trade log...")
    result = _portfolio_to_analysis_result(
        pf,
        ohlcv,
        execution_mode="SingleBacktest",
        parameters=params,
    )
    _notify(progress_callback, 95, "Finalizing results...")
    return result


def build_portfolio(
    ohlcv: pd.DataFrame,
    strategy_name: str,
    input_parameters: list[dict[str, Any]] | None = None,
    init_cash: float = _INIT_CASH,
    fee: float = _FEE,
    slippage: float = _SLIPPAGE,
) -> vbt.Portfolio:
    """Construct a backtest portfolio for a single full-sample strategy run."""
    if not strategy_name:
        raise ValueError("strategy_name is required.")

    strategy_module = load_strategy_module(strategy_name)
    if not hasattr(strategy_module, "execute_strategy"):
        raise ValueError(f"Strategy {strategy_name} does not have an execute_strategy function.")

    params = _build_fixed_params(input_parameters or [])
    exec_cfg = _PortfolioExec(init_cash=float(init_cash), fee=float(fee), slippage=float(slippage))
    freq = _infer_freq_or_default(ohlcv["close"].index)
    return _backtest_strategy(ohlcv, strategy_module, params, freq, exec_cfg)


def run_monte_carlo_from_trades(trades: list[TradeRecord], iterations: int = 200) -> MonteCarloResult:
    """Run a Monte Carlo robustness simulation directly from a list of TradeRecord models.

    Returns a MonteCarloResult containing simple numeric arrays suitable for plotting.
    """
    if iterations < 1:
        raise ValueError("iterations must be >= 1")

    pnls = np.asarray([_safe_float(t.pnl) for t in (trades or [])], dtype=float)

    # original equity as cumulative PnL
    if pnls.size == 0:
        original_equity = []
    else:
        original_equity = np.cumsum(pnls).astype(float).tolist()

    simulations: list[list[float]] = []
    rng = np.random.default_rng()
    for _ in range(int(iterations)):
        if pnls.size == 0:
            simulations.append([])
            continue
        shuffled = rng.permutation(pnls)
        simulations.append(np.cumsum(shuffled).astype(float).tolist())

    positive_survivals = sum(1 for curve in simulations if curve and curve[-1] > 0)
    survival_probability = float(positive_survivals) / float(iterations) * 100.0 if iterations else 0.0

    return MonteCarloResult(
        original_equity=original_equity,
        simulated_equities=simulations,
        survival_probability=survival_probability,
    )


def run_monte_carlo(pf: vbt.Portfolio, iterations: int = 200) -> AnalysisResult:
    """Backwards-compatible wrapper: build trade list from portfolio, run Monte Carlo, and return an AnalysisResult
    containing Monte Carlo arrays so the front-end can plot them using the unified AnalysisResult payload."""
    # Extract trades from portfolio
    equity_series = pf.value()
    if isinstance(equity_series, pd.DataFrame):
        equity_series = equity_series.iloc[:, 0]
    equity_series = pd.Series(equity_series).astype(float)

    trades = _extract_trades(pf, equity_series.index)
    mc = run_monte_carlo_from_trades(trades, iterations=iterations)

    # Wrap into AnalysisResult for compatibility with the Analyze job response
    return AnalysisResult(
        execution_mode="MonteCarlo",
        summary={},
        equity_curve=[],
        drawdown_curve=[],
        trades=trades,
        wfa_windows=[],
        parameters={},
        baseline_equity_curve=[],
        price_curve=[],
        tested_params=[],
        tested_profits=[],
        original_equity=mc.original_equity,
        simulated_equities=mc.simulated_equities,
        survival_probability=mc.survival_probability,
    )


def run_wfa_cluster(
    ohlcv: pd.DataFrame,
    strategy_name: str,
    input_parameters: list[dict[str, Any]] | None = None,
    window_counts: list[int] | None = None,
    in_sample_percents: list[int] | None = None,
    optimization_mode: str = "Fast",
    progress_callback: ProgressCallback | None = None,
) -> AnalysisResult:
    """Run WFA over a grid of window_counts × in_sample_percents and return a cluster_matrix in AnalysisResult."""
    
    if window_counts is None:
        window_counts = [3, 5, 7, 10]
    if in_sample_percents is None:
        in_sample_percents = [30, 40, 50, 60, 70]

    rows: list[dict[str, Any]] = []
    params = input_parameters or []
    
    total_tests = len(window_counts) * len(in_sample_percents)
    current_test = 0

    for wc in window_counts:
        row: dict[str, Any] = {"Windows": int(wc)}
        for isp in in_sample_percents:
            current_test += 1
            
            try:
                # İlerleme Çubuğu İçin Özel Formül: 
                # (Bulunduğumuz Hücrenin Yüzdesi) + (Optuna'nın Kendi Yüzdesi)
                def local_progress(pct: int, msg: str):
                    if progress_callback:
                        base_pct = ((current_test - 1) / total_tests) * 100
                        added_pct = (pct / total_tests)
                        progress_callback(
                            int(base_pct + added_pct),
                            f"Matris [{current_test}/{total_tests}] (Win:{wc} IS:%{isp}) -> {msg}"
                        )

                res = run_vectorbt_wfa(
                    ohlcv=ohlcv,
                    strategy_name=strategy_name,
                    input_parameters=params,
                    optimization_mode=optimization_mode,
                    n_windows=wc,
                    in_sample_percent=float(isp),
                    execution_mode="WFAOptimization",
                    progress_callback=local_progress
                )

                net_profit = 0.0
                try:
                    if hasattr(res, 'summary') and isinstance(res.summary, dict) and 'Net Profit' in res.summary:
                        import re
                        m = re.search(r"([-+]?[0-9]*\.?[0-9]+)", res.summary.get('Net Profit', ''))
                        if m: 
                            net_profit = float(m.group(1))
                    else:
                        net_profit = sum(float(w.oos_profit) for w in res.wfa_windows)
                except Exception:
                    net_profit = 0.0

                row[f"IS_{isp}"] = net_profit
            except Exception:
                row[f"IS_{isp}"] = float('nan')
                
        rows.append(row)

    return AnalysisResult(
        execution_mode="ClusterMatrix",
        summary={"ClusterRows": str(len(rows))},
        equity_curve=[], drawdown_curve=[], trades=[], wfa_windows=[],
        parameters={}, tested_params=[], tested_profits=[],
        cluster_matrix=rows,
    )


def _optimize_is_params(
    ohlcv_is: pd.DataFrame,
    strategy_name: str,
    param_combinations: list[dict[str, Any]],
    freq: str,
    exec_cfg: _PortfolioExec,
    optimization_mode: str = "Slow",
    fitness_criterion: str = "Balance max",
    strategy_nature: str = "trend", # YENİ EKLENEN: Stratejinin doğası (trend veya mean_reversion)
    progress_callback: ProgressCallback | None = None,
    base_pct: int = 0,
    alloc_pct: int = 0,
) -> tuple[dict[str, Any], list[dict[str, Any]], list[float], list[dict[str, Any]]]:

    # 1. Genetik havuzu taramadan önce, In-Sample periyodu için rejim haritasını (CRI) bir kez çıkar:
    cri_series = pd.Series(0.0, index=ohlcv_is.index)
    if "rafs" in fitness_criterion.lower() or "regime" in fitness_criterion.lower():
        cri_series = _calculate_cri_series(ohlcv_is['close'].astype(float))

    total_combos = len(param_combinations)
    best_params = None
    best_score = -float('inf')
    
    tested_params = []
    tested_profits = []
    optimization_results: list[dict[str, Any]] = []

    # ==========================================
    # MOD 1: FAST (GENETIC ALGORITHM / OPTUNA)
    # ==========================================
    if optimization_mode.lower() == "fast":
        def objective(trial):
            idx = trial.suggest_int("combo_idx", 0, total_combos - 1)
            params = param_combinations[idx]
            
            from strategy_loader import load_strategy_module
            strategy_module = load_strategy_module(strategy_name)
            
            try:
                pf = _backtest_strategy(ohlcv_is, strategy_module, params, freq, exec_cfg)
                score = _evaluate_fitness(pf, fitness_criterion)
                
                if np.isfinite(score):
                    # --- MT5 METRİKLERİNİ HESAPLA VE HAFIZAYA AL ---
                    net_profit, dd_pct, pf_factor, win_rate, trade_count = _extract_net_profit_max_dd_pf_win(pf)
                    
                    trial.set_user_attr("Profit", net_profit)
                    trial.set_user_attr("Total trades", trade_count)
                    trial.set_user_attr("Profit factor", pf_factor)
                    trial.set_user_attr("Drawdown %", dd_pct)
                    trial.set_user_attr("Win rate %", win_rate)
                    
                    # Expected Payoff ve Recovery Factor (MT5 Ekstraları)
                    payoff = (net_profit / trade_count) if trade_count > 0 else 0.0
                    recovery = (net_profit / dd_pct) if dd_pct > 0 else (999.0 if net_profit > 0 else 0.0)
                    trial.set_user_attr("Expected payoff", payoff)
                    trial.set_user_attr("Recovery factor", recovery)
                else:
                    score = -999999.0
            except Exception:
                score = -999999.0
                
            return score

        study = optuna.create_study(direction="maximize")
        n_trials = min(250, total_combos) 
        
        # YAPAY ZEKA KAPANIP AÇILMADAN KENDİ İÇİNDEN BİLGİ VERECEK
        def optuna_progress(study_obj, trial_obj):
            if progress_callback and trial_obj.number % 10 == 0:
                current_pct = base_pct + int((trial_obj.number / n_trials) * alloc_pct)
                progress_callback(current_pct, f"Genetik Zeka: {trial_obj.number}/{n_trials} Nesil...")

        study.optimize(objective, n_trials=n_trials, callbacks=[optuna_progress])
                
        best_idx = study.best_trial.params["combo_idx"]
        best_params = param_combinations[best_idx]
        best_score = study.best_value
        
        # --- SONUÇLARI FİLTRELE, YUVARLA VE MT5 FORMATINDA PAKETLE ---
        seen_combos = set() # Kopya (duplicate) sonuçları engellemek için hafıza
        
        for t in study.trials:
            if t.value != -999999.0:
                combo_idx = t.params["combo_idx"]
                
                # Eğer bu parametre kombinasyonunu daha önce tabloya eklediysek, atla!
                if combo_idx in seen_combos:
                    continue
                seen_combos.add(combo_idx)

                params = param_combinations[combo_idx]
                tested_params.append(params)
                tested_profits.append(t.value)
                
                # Sütunları 2 basamağa yuvarlayarak (round) tertemiz bir hale getiriyoruz
                row: dict[str, Any] = {
                    "Result (Score)": round(float(t.value), 2),
                    "Profit": round(float(t.user_attrs.get("Profit", 0.0)), 2),
                    "Total trades": int(t.user_attrs.get("Total trades", 0)),
                    "Profit factor": round(float(t.user_attrs.get("Profit factor", 0.0)), 2),
                    "Expected payoff": round(float(t.user_attrs.get("Expected payoff", 0.0)), 2),
                    "Drawdown %": round(float(t.user_attrs.get("Drawdown %", 0.0)), 2),
                    "Recovery factor": round(float(t.user_attrs.get("Recovery factor", 0.0)), 2),
                    # 0.416 gibi gelen Win Rate'i 41.66 formatına çeviriyoruz
                    "Win rate %": round(float(t.user_attrs.get("Win rate %", 0.0)) * 100, 2), 
                }
                
                # Parametreleri eklerken gereksiz ondalıkları kırp
                for k, v in params.items():
                    if isinstance(v, float):
                        row[str(k)] = round(v, 4)
                    else:
                        row[str(k)] = v
                    
                optimization_results.append(row)
        
        # TABLOYU SIRALA: En yüksek Score'a sahip olan strateji en üstte (1. sırada) çıksın
        optimization_results.sort(key=lambda x: x["Result (Score)"], reverse=True)

    # ==========================================
    # MOD 2: SLOW (GRID SEARCH / TÜMÜNÜ TARA)
    # ==========================================
    else:
        max_workers = max(1, (os.cpu_count() or 2) - 1)
        chunk_size = max(1, math.ceil(total_combos / (max_workers * 10)))
        batches = [param_combinations[i:i + chunk_size] for i in range(0, total_combos, chunk_size)]
        completed_combos = 0
        update_step = max(1, total_combos // 20)
        next_update = update_step

        with ProcessPoolExecutor(max_workers=max_workers) as executor:
            futures = {
                executor.submit(_worker_batch_backtest, ohlcv_is, strategy_name, batch, freq, exec_cfg): len(batch)
                for batch in batches
            }
            for future in as_completed(futures):
                batch_size = futures[future]
                try:
                    # _worker_batch_backtest'in tüm denenenleri döndürecek şekilde güncellendiğini varsayıyoruz
                    # Basitlik için sadece best'i alıyoruz, WFC'yi slow modda atlayabiliriz veya rastgele 50 seçebiliriz.
                    batch_best_params, batch_best_score = future.result()
                    if batch_best_params is not None and batch_best_score > best_score:
                        best_score = batch_best_score
                        best_params = batch_best_params
                        tested_params.append(best_params)
                        tested_profits.append(best_score)
                        # record into optimization_results as well
                        row = {"Score": float(batch_best_score)}
                        for k, v in (batch_best_params or {}).items():
                            row[str(k)] = v
                        optimization_results.append(row)
                except Exception:
                    pass
                completed_combos += batch_size
                if progress_callback and completed_combos >= next_update:
                    current_pct = base_pct + int((completed_combos / total_combos) * alloc_pct)
                    progress_callback(current_pct, f"Tam Tarama: {completed_combos}/{total_combos}...")
                    next_update += update_step

    if best_params is None:
        raise ValueError("Optimizasyonda geçerli parametre bulunamadı.")
        
    # DEĞİŞTİRİN:
    return best_params, tested_params, tested_profits, optimization_results


def _df_to_ohlc_curve(df: pd.DataFrame) -> list[OhlcPoint]:
    points = []
    for i in range(len(df)):
        points.append(OhlcPoint(
            index=i,
            timestamp=_timestamp_label(df.index, i),
            open=float(df['open'].iloc[i]),
            high=float(df['high'].iloc[i]),
            low=float(df['low'].iloc[i]),
            close=float(df['close'].iloc[i]),
        ))
    return points



def run_vectorbt_wfa(
    *,
    ohlcv: pd.DataFrame | None = None,
    csv_path: str | None = None,
    strategy_name: str | None = None,
    input_parameters: list[dict[str, Any]] | None = None,
    execution_mode: str = "WFAOptimization", 
    wfa_type: str = "Expanding",
    fitness_criterion: str = "Balance max",
    optimization_mode: str = "Fast",
    n_windows: int = 5,
    in_sample_percent: float = 80.0,
    init_cash: float = _INIT_CASH,
    fee: float = _FEE,
    slippage: float = _SLIPPAGE,
    resample_freq: str | None = None,
    progress_callback: ProgressCallback | None = None,
) -> AnalysisResult:
    if (ohlcv is None) == (csv_path is None):
        raise ValueError("Provide exactly one of ``ohlcv`` or ``csv_path``.")
    
    if not strategy_name:
        raise ValueError("strategy_name is required.")

    if csv_path is not None:
        _notify(progress_callback, 18, "Loading OHLCV data...")
        ohlcv = _load_ohlcv(csv_path, resample_freq)
    else:
        _notify(progress_callback, 18, "Preparing OHLCV data...")
    assert ohlcv is not None

    _notify(progress_callback, 20, "Loading strategy module...")
    strategy_module = load_strategy_module(strategy_name)

    if not hasattr(strategy_module, "execute_strategy"):
        raise ValueError(f"Strategy {strategy_name} does not have an execute_strategy function.")

    _notify(progress_callback, 22, "Building parameter combinations...")
    param_combinations = _build_parameter_combinations(input_parameters or [])
    if not param_combinations:
        raise ValueError("No parameter combinations generated from input_parameters.")

    exec_cfg = _PortfolioExec(init_cash=float(init_cash), fee=float(fee), slippage=float(slippage))

    # EKSİK OLAN VE HATAYA SEBEP OLAN SATIRLAR EKLENDİ
    close = ohlcv["close"].copy()
    freq = _infer_freq_or_default(close.index)
    warmup_bars = 50

 

    # Orijinal (Optimize Edilmemiş) Bakiye Eğrisini Hesapla
    params_default = _build_fixed_params(input_parameters or [])
    pf_baseline = _backtest_strategy(ohlcv, strategy_module, params_default, freq, exec_cfg)
    baseline_equity = _series_to_equity_curve(pf_baseline.value())

    total_bars = len(ohlcv)
    
    # STANDART OPTİMİZASYON İÇİN BAYPASS MANTIĞI
    if execution_mode == "StandardOptimization":
        # Tüm veriyi tek parça (In-Sample ve OOS aynı) olarak al
        wf_slices = [(slice(0, total_bars), slice(0, total_bars))]
        _notify(progress_callback, 24, "Running Standard Optimization (No WFA splits)...")
    else:
        # WFA İÇİN NORMAL PARÇALAMA
        wf_slices = _build_wfa_slices(
            wfa_type=wfa_type,
            n_rows=total_bars,
            n_windows=int(n_windows),
            in_sample_percent=float(in_sample_percent),
            warmup_bars=warmup_bars,
        )

    total_windows = len(wf_slices)
    results: list[WFAResult] = []
    all_oos_trades = []

    # Aggregate tested parameter sets and profits across windows for sensitivity plotting
    all_tested_params: list[dict[str, Any]] = []
    all_tested_profits: list[float] = []
    all_optimization_results: list[dict[str, Any]] = []
    
    for window_id, (is_sl, oos_sl) in enumerate(wf_slices, start=1):
        window_base_pct = 25 + int(70 * (window_id - 1) / total_windows)
        window_alloc_pct = int(70 / total_windows)
        
        ohlcv_is = ohlcv.iloc[is_sl]
        ohlcv_oos = ohlcv.iloc[oos_sl]
        
        # --- YENİ: MARKOV REJİM GEÇİŞ ANALİZİ ---
        kl_score = 0.0
        if "rafs" in fitness_criterion.lower() or "regime" in fitness_criterion.lower():
            try:
                # IS ve OOS için CRI serilerini çıkar
                cri_is = _calculate_cri_series(ohlcv_is['close'].astype(float))
                cri_oos = _calculate_cri_series(ohlcv_oos['close'].astype(float))
                
                # İki dönemin Markov Geçiş Matrislerini çıkar
                markov_is = _build_markov_matrix(cri_is)
                markov_oos = _build_markov_matrix(cri_oos)
                
                # İki matris arasındaki KL Farkını ölç (Rejim Kırılma Skoru)
                kl_score = _calculate_kl_divergence(markov_is, markov_oos)
            except Exception:
                kl_score = 0.0
        
        try:
            # DÜZELTME BURADA: Gelen 4'lü paketi (Tuple) doğru şekilde 4 değişkene ayırıyoruz
            best_params, tested_params, tested_profits, optimization_results = _optimize_is_params(
                    ohlcv_is, 
                    strategy_name, 
                    param_combinations, 
                    freq, 
                    exec_cfg,
                    fitness_criterion=fitness_criterion,
                    optimization_mode=optimization_mode,  # C#'tan gelen Fast/Slow buraya iletiliyor
                    progress_callback=progress_callback,
                    base_pct=window_base_pct,
                    alloc_pct=window_alloc_pct
                )
        except ValueError:
            continue
        
        # collect tested param / profit pairs for later reporting/plotting
        if tested_params:
            all_tested_params.extend(tested_params)
        if tested_profits:
            all_tested_profits.extend(tested_profits)
        if optimization_results:
            all_optimization_results.extend(optimization_results)
        
        pf_is_best = _backtest_strategy(ohlcv_is, strategy_module, best_params, freq, exec_cfg)
        is_profit, _, _, _, _ = _extract_net_profit_max_dd_pf_win(pf_is_best)
        
        pf_oos = _backtest_strategy(ohlcv_oos, strategy_module, best_params, freq, exec_cfg)
        oos_profit, oos_dd, oos_pf, oos_wr, oos_trades = _extract_net_profit_max_dd_pf_win(pf_oos)
        

        extracted_trades = _extract_trades(pf_oos, ohlcv.index, offset=int(oos_sl.start or 0))
        all_oos_trades.extend(extracted_trades)




        results.append(
            WFAResult(
                test_window_id=int(window_id),
                is_start_index=int(is_sl.start or 0),
                is_end_index=int(is_sl.stop or 0),
                oos_start_index=int(oos_sl.start or 0),
                oos_end_index=int(oos_sl.stop or 0),
                is_profit=float(is_profit),
                oos_profit=float(oos_profit),
                drawdown_percent=float(oos_dd),
                profit_factor=float(oos_pf),
                win_rate=float(oos_wr),
                regime_shift_score=float(kl_score),
                total_trades=int(oos_trades),
                best_parameters=best_params,
            )
        )
    # --- DÖNGÜ BİTTİKTEN SONRA EKLENECEK KISIM ---
    _notify(progress_callback, 95, "Calculating global statistics...")
    
    # Tüm OOS işlemlerini kullanarak detaylı MT5/WFAToolbox metrikleri hesaplama
    total_oos_profit = sum(t.pnl for t in all_oos_trades)
    total_trades = len(all_oos_trades)
    
    winning_trades = [t.pnl for t in all_oos_trades if t.pnl > 0]
    losing_trades = [t.pnl for t in all_oos_trades if t.pnl <= 0]
    
    gross_profit = sum(winning_trades)
    gross_loss = abs(sum(losing_trades))
    
    win_count = len(winning_trades)
    loss_count = len(losing_trades)
    win_rate = (win_count / total_trades * 100) if total_trades > 0 else 0.0
    
    largest_win = max(winning_trades, default=0.0)
    largest_loss = min(losing_trades, default=0.0)
    avg_win = (gross_profit / win_count) if win_count > 0 else 0.0
    avg_loss = (gross_loss / loss_count) if loss_count > 0 else 0.0
    
    long_trades_count = sum(1 for t in all_oos_trades if "long" in t.direction.lower())
    short_trades_count = sum(1 for t in all_oos_trades if "short" in t.direction.lower())
    
    profit_factor = (gross_profit / gross_loss) if gross_loss > 0 else (999.0 if gross_profit > 0 else 0.0)
    global_max_dd = min((w.drawdown_percent for w in results), default=0.0)

    # WFAToolbox tarzı detaylı karne
    global_summary = {
        "Net Profit": f"{total_oos_profit:.2f} $",
        "Total Trades": str(total_trades),
        "Profit Factor": f"{profit_factor:.2f}",
        "Max Drawdown": f"{global_max_dd:.2f} %",
        "Gross Profit": f"{gross_profit:.2f} $",
        "Gross Loss": f"{gross_loss:.2f} $",
        "Win Rate": f"{win_rate:.2f} %",
        "Winning Trades": str(win_count),
        "Losing Trades": str(loss_count),
        "Largest Win": f"{largest_win:.2f} $",
        "Largest Loss": f"{largest_loss:.2f} $",
        "Average Win": f"{avg_win:.2f} $",
        "Average Loss": f"{avg_loss:.2f} $",
        "Long Trades": str(long_trades_count),
        "Short Trades": str(short_trades_count),
        "Optimization Mode": optimization_mode
    }

    # Fiyat verisini C#'a göndermek için hazırla



    price_points = _series_to_equity_curve(ohlcv["close"])
    ohlc_points = _df_to_ohlc_curve(ohlcv) # <--- YENİ

    return _wfa_windows_to_analysis_result(
        results, 
        execution_mode=execution_mode,
        all_trades=all_oos_trades,
        summary=global_summary, 
        baseline_equity_curve=baseline_equity,
        price_curve=price_points, 
        ohlc_curve=ohlc_points, # <--- YENİ
        tested_params=all_tested_params,
        tested_profits=all_tested_profits,
    )




def _df_to_ohlc_curve(df: pd.DataFrame) -> list[OhlcPoint]:
    points = []
    for i in range(len(df)):
        points.append(OhlcPoint(
            index=i,
            timestamp=_timestamp_label(df.index, i),
            open=float(df['open'].iloc[i]),
            high=float(df['high'].iloc[i]),
            low=float(df['low'].iloc[i]),
            close=float(df['close'].iloc[i]),
        ))
    return points