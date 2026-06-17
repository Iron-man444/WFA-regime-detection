import pandas as pd
import numpy as np

# Arayüzden WFA Matrisi ve Optimizasyon için Parametre Uzayı
STRATEGY_PARAMS = {
    "bb_period": {"default": 20, "start": 10, "step": 2, "stop": 40},
    "bb_std": {"default": 2.0, "start": 1.5, "step": 0.1, "stop": 3.0},
    "rsi_period": {"default": 14, "start": 7, "step": 1, "stop": 21},
    "rsi_ob": {"default": 70, "start": 60, "step": 2, "stop": 85},
    "rsi_os": {"default": 30, "start": 15, "step": 2, "stop": 40}
}

def execute_strategy(ohlcv: pd.DataFrame, params: dict) -> dict:
    bb_period = int(params.get("bb_period", 20))
    bb_std = float(params.get("bb_std", 2.0))
    rsi_period = int(params.get("rsi_period", 14))
    rsi_ob = float(params.get("rsi_ob", 70))
    rsi_os = float(params.get("rsi_os", 30))

    close = ohlcv['close'].astype(float)
    high = ohlcv['high'].astype(float)
    low = ohlcv['low'].astype(float)

    # ==========================================
    # 1. RSI HESAPLAMASI
    # ==========================================
    delta = close.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = -delta.where(delta < 0, 0.0)

    avg_gain = gain.rolling(window=rsi_period, min_periods=1).mean()
    avg_loss = loss.rolling(window=rsi_period, min_periods=1).mean()

    rs = avg_gain / (avg_loss + 1e-9)
    rsi = 100 - (100 / (1 + rs))

    # ==========================================
    # 2. BOLLINGER BANTLARI HESAPLAMASI
    # ==========================================
    sma = close.rolling(window=bb_period).mean()
    std = close.rolling(window=bb_period).std()
    
    upper_band = sma + (std * bb_std)
    lower_band = sma - (std * bb_std)

    # ==========================================
    # 3. GİRİŞ SİNYALLERİ (Entries)
    # ==========================================
    # LONG: Fiyat alt bandı delmişse VE RSI dipteyse
    long_entries = (low <= lower_band) & (rsi <= rsi_os)

    # SHORT: Fiyat üst bandı delmişse VE RSI tepedeyse
    short_entries = (high >= upper_band) & (rsi >= rsi_ob)

    # ==========================================
    # 4. ÇIKIŞ SİNYALLERİ (Exits)
    # ==========================================
    # LONG ÇIKIŞ: Fiyat orta banda (SMA) geri döndüğünde kârı al ve çık
    long_exits = (close >= sma) & (close.shift(1) < sma.shift(1))

    # SHORT ÇIKIŞ: Fiyat orta banda (SMA) geri döndüğünde kârı al ve çık
    short_exits = (close <= sma) & (close.shift(1) > sma.shift(1))

    # Aynı mumdaki mantıksız çakışmaları temizle
    long_exits = long_exits & ~long_entries
    short_exits = short_exits & ~short_entries

    return {
        "entries": long_entries,
        "exits": long_exits,
        "short_entries": short_entries,
        "short_exits": short_exits
    }