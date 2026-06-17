import pandas as pd
import numpy as np

# Arayüzden WFA Matrisi ve Optimizasyon için Parametre Uzayı
STRATEGY_PARAMS = {
    "bars_n": {"default": 5, "start": 3, "step": 1, "stop": 10},
    "channel_dist_mult": {"default": 0.1, "start": 0.05, "step": 0.05, "stop": 0.3}, # Yüzde yerine kanal genişliği çarpanı
    "rsi_period": {"default": 14, "start": 7, "step": 7, "stop": 21},
    "rsi_ob": {"default": 60, "start": 55, "step": 5, "stop": 75},
    "rsi_os": {"default": 40, "start": 25, "step": 5, "stop": 45},
    "start_hour": {"default": 8, "start": 0, "step": 2, "stop": 10},
    "end_hour": {"default": 20, "start": 16, "step": 2, "stop": 22}
}

def execute_strategy(ohlcv: pd.DataFrame, params: dict) -> dict:
    bars_n = int(params.get("bars_n", 5))
    channel_dist_mult = float(params.get("channel_dist_mult", 0.1))
    rsi_period = int(params.get("rsi_period", 14))
    rsi_ob = float(params.get("rsi_ob", 60))
    rsi_os = float(params.get("rsi_os", 40))
    start_hour = int(params.get("start_hour", 8))
    end_hour = int(params.get("end_hour", 20))

    close = ohlcv['close'].astype(float)
    high = ohlcv['high'].astype(float)
    low = ohlcv['low'].astype(float)

    # ==========================================
    # 1. RSI HESAPLAMASI (1-Dakikalık Serilerle Uyumlu)
    # ==========================================
    delta = close.diff()
    gain = delta.where(delta > 0, 0.0)
    loss = -delta.where(delta < 0, 0.0)

    avg_gain = gain.rolling(window=rsi_period, min_periods=1).mean()
    avg_loss = loss.rolling(window=rsi_period, min_periods=1).mean()

    rs = avg_gain / (avg_loss + 1e-9)
    rsi = 100 - (100 / (1 + rs))

    # ==========================================
    # 2. DİNAMİK KANAL GENİŞLİĞİ VE LİMİT SEVİYELERİ
    # ==========================================
    highest_high = high.rolling(window=bars_n).max()
    lowest_low = low.rolling(window=bars_n).min()
    
    # Yüzdesel hesaplama yerine Kanal Genişliği (Channel Width) denklemi
    channel_width = highest_high - lowest_low

    # Emir seviyeleri, kanalın kendi volatilitesine göre dışarıya yerleştirilir
    sell_limit_price = highest_high + (channel_width * channel_dist_mult)
    buy_limit_price = lowest_low - (channel_width * channel_dist_mult)

    # ==========================================
    # 3. ZAMAN FİLTRESİ (TRADING HOURS)
    # ==========================================
    hours = ohlcv.index.hour
    if start_hour < end_hour:
        time_filter = (hours >= start_hour) & (hours < end_hour)
    elif start_hour > end_hour:
        # Gece yarısını geçen saat aralığı (Örn: 22:00 - 04:00)
        time_filter = (hours >= start_hour) | (hours < end_hour)
    else:
        time_filter = pd.Series(True, index=ohlcv.index)

    # ==========================================
    # 4. GİRİŞ SİNYALLERİ (LIMIT EMİR SİMÜLASYONU)
    # ==========================================
    # LONG: RSI dipteyse VE mumun en düşük fiyatı dinamik alt bant limitine değmişse
    long_entries = (rsi <= rsi_os) & (low <= buy_limit_price) & time_filter

    # SHORT: RSI şişkinse VE mumun en yüksek fiyatı dinamik üst bant limitine değmişse
    short_entries = (rsi >= rsi_ob) & (high >= sell_limit_price) & time_filter

    # ==========================================
    # 5. DİNAMİK ÇIKIŞ SİNYALLERİ
    # ==========================================
    # Ortalamaya dönüş: Fiyat koptuğu ortalamaya (SMA) geri döndüğünde işlemi kapat.
    sma = close.rolling(window=rsi_period).mean()

    long_exits = (close > sma) & (close.shift(1) <= sma.shift(1))
    short_exits = (close < sma) & (close.shift(1) >= sma.shift(1))

    # Aynı mumda çakışan giriş ve çıkış sinyallerini temizle
    long_exits = long_exits & ~long_entries
    short_exits = short_exits & ~short_entries

    return {
        "entries": long_entries,
        "exits": long_exits,
        "short_entries": short_entries,
        "short_exits": short_exits
    }