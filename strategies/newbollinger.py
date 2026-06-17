import pandas as pd
import numpy as np

# Arayüzden optimize edilecek parametreler
STRATEGY_PARAMS = {
    "bb_period": {"default": 20, "start": 10, "step": 5, "stop": 50},
    "bb_std": {"default": 2.5, "start": 1.5, "step": 0.5, "stop": 3.0}, # Normalden daha geniş bant (2.5)
    "rsi_period": {"default": 14, "start": 7, "step": 7, "stop": 21}
}

def execute_strategy(ohlcv: pd.DataFrame, params: dict) -> dict:
    bb_period = int(params.get("bb_period", 20))
    bb_std = float(params.get("bb_std", 2.5))
    rsi_period = int(params.get("rsi_period", 14))

    close = ohlcv['close'].astype(float)
    low = ohlcv['low'].astype(float)
    high = ohlcv['high'].astype(float)

    # 1. Volatilite Çerçevesi (Bollinger)
    sma = close.rolling(window=bb_period).mean()
    std = close.rolling(window=bb_period).std()
    
    upper_band = sma + (std * bb_std)
    lower_band = sma - (std * bb_std)

    # 2. RSI Hesaplaması (Aşırı Alım/Satım Onayı)
    delta = close.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = -delta.where(delta < 0, 0.0)
    
    avg_gain = gain.rolling(window=rsi_period, min_periods=1).mean()
    avg_loss = loss.rolling(window=rsi_period, min_periods=1).mean()
    
    rs = avg_gain / (avg_loss + 1e-9)
    rsi = 100 - (100 / (1 + rs))

    # ==========================================
    # LİKİDİTE AVI (RANGE-BOUND LIQUIDITY SWEEP)
    # ==========================================
    
    # LONG: Fiyatın "Low" değeri alt bandın altına sarkmış (stopları patlatmış), 
    # ancak "Close" (kapanış) tekrar bandın İÇİNE dönmüş. Ek olarak RSI < 40 ile aşırı satım var.
    long_entries = (low < lower_band) & (close > lower_band) & (rsi < 40)
    
    # SHORT: Fiyatın "High" değeri üst bandın üstüne çıkmış (likiditeyi almış), 
    # ancak "Close" tekrar bandın İÇİNE düşmüş. Ek olarak RSI > 60 ile aşırı alım var.
    short_entries = (high > upper_band) & (close < upper_band) & (rsi > 60)

    # ÇIKIŞLAR: Fiyat güvenli bölgeye (SMA Orta Bandına) ulaştığında kârı alıp çık.
    long_exits = (close > sma) & (close.shift(1) <= sma.shift(1))
    short_exits = (close < sma) & (close.shift(1) >= sma.shift(1))

    # Sinyal çakışmalarını önle
    long_exits = long_exits & ~long_entries
    short_exits = short_exits & ~short_entries

    return {
        "entries": long_entries,
        "exits": long_exits,
        "short_entries": short_entries,
        "short_exits": short_exits
    }