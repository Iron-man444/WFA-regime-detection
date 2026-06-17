import pandas as pd
import numpy as np

# C# Arayüzünün (WPF) okuyup ekrana çizeceği dinamik parametre sözlüğü
STRATEGY_PARAMS = {
    "InpBarsN": {"default": 5, "min": 2, "max": 15, "step": 1},
    "InpOrderDistPoints": {"default": 500, "min": 100, "max": 1500, "step": 100},
    "InpTpPoints": {"default": 52000, "min": 1000, "max": 100000, "step": 1000}, # TP biraz küçültüldü
    "InpSlPoints": {"default": 12500, "min": 5000, "max": 50000, "step": 2500},
    "InpTslPoints": {"default": 300, "min": 100, "max": 1000, "step": 100},
    "InpTslTriggerPoints": {"default": 600, "min": 200, "max": 2000, "step": 200}
}

def execute_strategy(data: pd.DataFrame, params: dict, point_size: float = None):
    df = data.copy()
    
    # 1. AKILLI POINT HESAPLAYICI
    # Eğer point_size arayüzden gelmediyse, enstrümanın fiyatına bakarak otomatik tahmin et.
    if point_size is None:
        mean_price = df["close"].mean()
        if mean_price < 5.0:       # EURUSD, GBPUSD gibi major Forex pariteleri
            point_size = 0.00001
        elif mean_price < 200.0:   # JPY pariteleri veya Petrol
            point_size = 0.001
        else:                      # Altın (XAUUSD), BTC, Endeksler (Nasdaq)
            point_size = 0.01

    # 2. Parametreleri Yükle ve Nokta (Point) Değeriyle Çarp
    bars_n = int(params.get("InpBarsN", 5))
    order_dist = float(params.get("InpOrderDistPoints", 500)) * point_size
    tp_dist = float(params.get("InpTpPoints", 52000)) * point_size
    sl_dist = float(params.get("InpSlPoints", 12500)) * point_size
    
    window = 2 * bars_n + 1

    # 3. MAJÖR SWING HESAPLAMASI (Geleceği Görmeden - No Lookahead Bias)
    roll_max = df["high"].rolling(window=window, center=True).max()
    is_swing_high = df["high"] == roll_max

    roll_min = df["low"].rolling(window=window, center=True).min()
    is_swing_low = df["low"] == roll_min

    # Sinyalin kesinleşmesi için sağdaki "bars_n" kadar mumun kapanmasını bekle
    df["SwingHigh"] = df["high"][is_swing_high]
    df["SwingHigh"] = df["SwingHigh"].shift(bars_n).ffill()

    df["SwingLow"] = df["low"][is_swing_low]
    df["SwingLow"] = df["SwingLow"].shift(bars_n).ffill()

    # 4. KIRILIM (BREAKOUT) SİNYALLERİ
    buy_level = df['SwingHigh'] + order_dist
    sell_level = df['SwingLow'] - order_dist

    # Fiyat kırılım seviyesini yukarı keserse
    df["Buy_Signal"] = (df["close"] > buy_level) & (df["close"].shift(1) <= buy_level.shift(1))

    # Fiyat kırılım seviyesini aşağı keserse
    df["Sell_Signal"] = (df["close"] < sell_level) & (df["close"].shift(1) >= sell_level.shift(1))
    
    return {
        "entries": df["Buy_Signal"],
        "short_entries": df["Sell_Signal"],
        "sl_distance": sl_dist,
        "tp_distance": tp_dist
    }