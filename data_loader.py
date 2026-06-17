"""
Market data ingestion for the WFA API.

``get_market_data`` normalizes every path (CSV, yfinance, ccxt, mt5) into a canonical OHLCV
``pandas.DataFrame`` with columns ``open, high, low, close, volume`` and a sorted index.
Provides functions to download and save historical data locally.
"""

from __future__ import annotations

from typing import Any
import pandas as pd
from datetime import datetime
import os

def _normalize_ccxt_symbol(symbol: str) -> str:
    s = symbol.strip().upper()
    if "/" in s:
        return s
    if "-" in s:
        base, quote = s.split("-", 1)
        return f"{base}/{quote}"
    if s.endswith("USDT") and len(s) > 4:
        return f"{s[:-4]}/USDT"
    return s

def _fetch_crypto_ccxt(symbol: str, timeframe: str, start_date: datetime = None, end_date: datetime = None, limit: int = 2000) -> pd.DataFrame:
    import ccxt  # type: ignore[import-not-found]

    sym = _normalize_ccxt_symbol(symbol)
    exchange = ccxt.binance({"enableRateLimit": True})
    if not exchange.has.get("fetchOHLCV"):
        raise RuntimeError("Selected ccxt exchange does not expose fetchOHLCV.")

    ccxt_tf = {"M1": "1m", "M5": "5m", "M15": "15m", "H1": "1h", "H4": "4h", "D1": "1d"}.get(timeframe.upper(), "1m")

    if start_date and end_date:
        since_ts = int(start_date.timestamp() * 1000)
        end_ts = int(end_date.timestamp() * 1000)
        all_ohlcv = []
        while since_ts < end_ts:
            raw = exchange.fetch_ohlcv(sym, timeframe=ccxt_tf, since=since_ts, limit=1000)
            if not raw:
                break
            all_ohlcv.extend(raw)
            since_ts = raw[-1][0] + 1
            if since_ts >= end_ts:
                break
    else:
        all_ohlcv = exchange.fetch_ohlcv(sym, timeframe=ccxt_tf, limit=limit)

    if not all_ohlcv:
        raise ValueError(f"No OHLCV rows returned for {sym} @ {timeframe}.")

    df = pd.DataFrame(all_ohlcv, columns=["timestamp", "open", "high", "low", "close", "volume"])
    df["timestamp"] = pd.to_datetime(df["timestamp"], unit="ms", utc=True)
    df = df.set_index("timestamp").sort_index()
    return df

def _fetch_yfinance(symbol: str, timeframe: str, start_date: datetime = None, end_date: datetime = None, period: str = "5y") -> pd.DataFrame:
    import yfinance as yf  # type: ignore[import-not-found]

    sym = symbol.strip()
    if len(sym) == 6 and sym.isalpha() and not sym.endswith("=X"):
        sym = f"{sym}=X"

    interval = {"M1": "1m", "M5": "5m", "M15": "15m", "H1": "1h", "D1": "1d"}.get(timeframe.upper(), "1d")
    
    if "h" in interval or "m" in interval:
        period = "730d"

    kwargs = {"tickers": sym, "interval": interval, "progress": False, "auto_adjust": False}
    if start_date and end_date:
        kwargs["start"] = start_date
        kwargs["end"] = end_date
    else:
        kwargs["period"] = period

    df = yf.download(**kwargs)
    
    if df.empty:
        raise ValueError(f"yfinance returned no rows for {sym} @ {interval}.")

    if isinstance(df.columns, pd.MultiIndex):
        df.columns = [str(c[0]).lower() for c in df.columns]
    else:
        df.columns = [str(c).lower() for c in df.columns]

    if "adj close" in df.columns:
        if "close" not in df.columns:
            df = df.rename(columns={"adj close": "close"})
        else:
            df = df.drop(columns=["adj close"])
            
    df = df.loc[:, ~df.columns.duplicated()]

    cols = set(df.columns)
    if not {"open", "high", "low", "close"} <= cols:
        raise ValueError(f"yfinance frame missing OHLC columns. Got: {list(df.columns)}")

    if "volume" not in cols:
        df["volume"] = 0.0

    df = df[["open", "high", "low", "close", "volume"]].apply(pd.to_numeric, errors="coerce")
    df = df.dropna(subset=["close"]).sort_index()
    return df

def _fetch_mt5(symbol: str, timeframe: str, start_date: datetime, end_date: datetime) -> pd.DataFrame:
    import MetaTrader5 as mt5

    if not mt5.initialize():
        raise Exception(f"MetaTrader 5 başlatılamadı. Hata: {mt5.last_error()}")

    timeframes = {
        "M1": mt5.TIMEFRAME_M1, "M5": mt5.TIMEFRAME_M5, "M15": mt5.TIMEFRAME_M15,
        "H1": mt5.TIMEFRAME_H1, "H4": mt5.TIMEFRAME_H4, "D1": mt5.TIMEFRAME_D1,
    }
    mt5_tf = timeframes.get(timeframe.upper(), mt5.TIMEFRAME_M1)

    rates = mt5.copy_rates_range(symbol, mt5_tf, start_date, end_date)
    mt5.shutdown()
    
    if rates is None or len(rates) == 0:
        raise ValueError(f"{symbol} için belirtilen tarihler arasında MT5 üzerinde veri bulunamadı.")

    df = pd.DataFrame(rates)
    df['time'] = pd.to_datetime(df['time'], unit='s')
    df = df.set_index('time')
    df = df[['open', 'high', 'low', 'close', 'tick_volume']]
    df.rename(columns={'tick_volume': 'volume'}, inplace=True)
    return df

def _load_csv_mt5(file_path: str) -> pd.DataFrame:
    # 1. Esnek Ayırıcı ve Kodlama (MT5'in UTF-16 ve boşluklu formatlarına karşı)
    try:
        df = pd.read_csv(file_path, sep='\t')
        if len(df.columns) < 4:
            df = pd.read_csv(file_path, sep=r'\s+', engine='python') # Space veya Tab karması
    except UnicodeDecodeError:
        # Eğer UTF-16 LE olarak export edilmişse
        df = pd.read_csv(file_path, sep='\t', encoding='utf-16')
    except Exception:
        df = pd.read_csv(file_path, sep=',')

    # 2. Sütun İsimlerini Temizle
    df.columns = [str(c).strip().replace('<', '').replace('>', '').lower() for c in df.columns]

    # 3. Datetime Dönüşümü (Saniye zorunluluğu kaldırılarak esnetildi)
    if 'date' in df.columns and 'time' in df.columns:
        df['datetime'] = pd.to_datetime(df['date'] + ' ' + df['time'], errors='coerce')
        df.set_index('datetime', inplace=True)
    elif 'time' in df.columns:
        df['datetime'] = pd.to_datetime(df['time'], errors='coerce')
        df.set_index('datetime', inplace=True)
    elif 'date' in df.columns:
        df['datetime'] = pd.to_datetime(df['date'], errors='coerce')
        df.set_index('datetime', inplace=True)
    elif 'timestamp' in df.columns:
        df['datetime'] = pd.to_datetime(df['timestamp'], errors='coerce')
        df.set_index('datetime', inplace=True)
        
    # 4. GİZLİ HATA ÇÖZÜMÜ: Çift volume sütununu engelle, ilk bulduğunu al
    if 'tickvol' in df.columns:
        df['volume'] = df['tickvol']
    elif 'vol' in df.columns:
        df['volume'] = df['vol']
    elif 'realvol' in df.columns:
        df['volume'] = df['realvol']
    elif 'tick volume' in df.columns:
        df['volume'] = df['tick volume']

    cols = set(df.columns)
    if not {"open", "high", "low", "close"} <= cols:
        raise ValueError(f"CSV OHLC sütunlarını içermiyor. Bulunanlar: {list(df.columns)}")

    if "volume" not in cols:
        df["volume"] = 0.0

    # 5. Pandas'ın kafasını karıştıracak diğer tüm kalıntı sütunları izole et
    # .loc kullanarak duplicate sütun çökmesini tamamen engelliyoruz
    df = df.loc[:, ["open", "high", "low", "close", "volume"]]
    
    # Tüm verileri float64 tipine zorla
    df = df.apply(pd.to_numeric, errors="coerce")
    
    # 6. Temizlik ve Kontrol
    df = df.dropna(subset=["close"]).sort_index()
    
    if df.empty:
        raise ValueError(f"CRITICAL: {file_path} dosyası okundu ancak indeksleme hatası yüzünden veri çıkarılamadı.")
        
    return df

def get_market_data(payload: dict[str, Any]) -> pd.DataFrame:
    src = str(payload.get("data_source_type", "")).upper()
    if src not in {"CSV", "API"}:
        raise ValueError("data_source_type must be 'CSV' or 'API'.")

    if src == "CSV":
        path = payload.get("file_path")
        if not path:
            raise ValueError("CSV mode requires file_path.")
        return _load_csv_mt5(str(path))

    asset = str(payload.get("asset_class") or "").strip().upper()
    symbol = str(payload.get("symbol") or "").strip()
    timeframe = str(payload.get("timeframe") or "").strip()
    
    if not symbol or not timeframe:
        raise ValueError("API mode requires symbol and timeframe.")

    # 1. Veriyi Çek
    if asset == "CRYPTO":
        df = _fetch_crypto_ccxt(symbol, timeframe)
        prefix = "CRYPTO"
    elif asset == "MT5" or asset == "FOREX":
        # Varsayılan son 1 aylık test için
        df = _fetch_mt5(symbol, timeframe, datetime.now().replace(day=1), datetime.now())
        prefix = "MT5"
    else:
        df = _fetch_yfinance(symbol, timeframe)
        prefix = "YF"

    # --- YENİ EKLENEN OTOMATİK KAYIT KISMI ---
    try:
        import os
        os.makedirs("data_cache", exist_ok=True)
        safe_symbol = symbol.replace('/', '_')
        file_path = f"data_cache/AUTO_{prefix}_{safe_symbol}_{timeframe}.csv"
        # Veriyi diske kaydet
        df.to_csv(file_path)
        print(f"[VERİ DEPOSU] Taze veri çekildi ve başarıyla kaydedildi: {file_path}")
    except Exception as e:
        print(f"[UYARI] Otomatik kayıt başarısız oldu: {e}")
    # -----------------------------------------

    return df

# --- C# ARAYÜZÜ İÇİN CSV İNDİRME SERVİSLERİ ---

def download_and_save_data(asset_class: str, symbol: str, timeframe: str, start_date: str, end_date: str) -> str:
    """
    Belirtilen kaynaktan veriyi çeker, data_cache klasörüne CSV olarak kaydeder
    ve C# arayüzünün kullanabilmesi için dosya yolunu döndürür.
    """
    os.makedirs("data_cache", exist_ok=True)
    s_date = datetime.strptime(start_date, "%Y-%m-%d")
    e_date = datetime.strptime(end_date, "%Y-%m-%d")
    
    asset = asset_class.upper()
    
    if "CRYPTO" in asset:
        df = _fetch_crypto_ccxt(symbol, timeframe, s_date, e_date)
        prefix = "CRYPTO"
    elif "MT5" in asset or "FOREX" in asset:
        df = _fetch_mt5(symbol, timeframe, s_date, e_date)
        prefix = "MT5"
    else:
        df = _fetch_yfinance(symbol, timeframe, s_date, e_date)
        prefix = "YF"
        
    df.reset_index(inplace=True)
    filename = f"data_cache/{prefix}_{symbol.replace('/', '_')}_{timeframe}.csv"
    df.to_csv(filename, index=False)
    
    return filename