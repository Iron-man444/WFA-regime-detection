import pandas as pd
import numpy as np

# 1. KURAL: Arayüzde görünecek parametrelerini buraya yaz
STRATEGY_PARAMS = {
    "benim_param_1": 10,
    "benim_param_2": 5.5
}

def execute_strategy(ohlcv: pd.DataFrame, params: dict) -> dict:
    """
    KULLANICIYA NOT: 
    Buraya kendi özel stratejinizi yazın. 'ohlcv' size fiyat verisini, 
    'params' ise arayüzden gelen değerleri verir.
    Tek kural, en sonda 'entries' ve 'short_entries' (True/False) döndürmenizdir.
    """
    
    # Kullanıcı parametrelerini alıyor
    p1 = params.get("benim_param_1", 10)
    p2 = params.get("benim_param_2", 5.5)
    
    close = ohlcv['close']
    
    # --- KULLANICI BURADA TAMAMEN ÖZGÜRDÜR ---
    # İsterse burada Machine Learning çalıştırır, isterse RSI hesaplar.
    # Bizim motorumuz buraya ASLA karışmaz.
    
    my_long_signals = pd.Series(False, index=close.index)
    my_short_signals = pd.Series(False, index=close.index)
    
    # Kendi mantığını uyguluyor...
    my_long_signals.iloc[-1] = True  # Örnek sinyal
    
    # ------------------------------------------

    # 3. KURAL: Sonucu bizim motorun anladığı dilde teslim et
    return {
        "entries": my_long_signals,
        "short_entries": my_short_signals
    }