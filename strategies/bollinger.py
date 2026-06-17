import pandas as pd
import numpy as np

STRATEGY_PARAMS = {
    "bb_period": {"default": 20, "start": 10, "step": 5, "stop": 50},
    "bb_std": {"default": 2.0, "start": 1.5, "step": 0.5, "stop": 3.0}
}

def execute_strategy(ohlcv: pd.DataFrame, params: dict) -> dict:
    bb_period = int(params.get("bb_period", 20))
    bb_std = float(params.get("bb_std", 2.0))
    
    close = ohlcv['close'].astype(float)
    
    sma = close.rolling(window=bb_period).mean()
    std = close.rolling(window=bb_period).std()
    
    upper_band = sma + (std * bb_std)
    lower_band = sma - (std * bb_std)
    
    # LONG GİRİŞ: Fiyat üst bandı yukarı kestiğinde
    long_entries = (close > upper_band) & (close.shift(1) <= upper_band.shift(1))
    # LONG ÇIKIŞ: Fiyat SMA'ya (orta banda) döndüğünde
    long_exits = (close < sma) & (close.shift(1) >= sma.shift(1))
    
    # SHORT GİRİŞ: Fiyat alt bandı aşağı kestiğinde
    short_entries = (close < lower_band) & (close.shift(1) >= lower_band.shift(1))
    # SHORT ÇIKIŞ: Fiyat SMA'ya (orta banda) döndüğünde
    short_exits = (close > sma) & (close.shift(1) <= sma.shift(1))
    
    # Aynı anda çift sinyal çakışmasını engelle
    long_exits = long_exits & ~long_entries
    short_exits = short_exits & ~short_entries
    
    return {
        "entries": long_entries,
        "exits": long_exits,
        "short_entries": short_entries,
        "short_exits": short_exits
    }