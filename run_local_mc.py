from schemas import TradeRecord
from wfa_engine import run_monte_carlo_from_trades

trades_raw = [
    {'trade_id':1,'entry_index':10,'exit_index':20,'entry_time':'2023-01-01T00:00:00Z','exit_time':'2023-01-02T00:00:00Z','direction':'Long','size':1.0,'entry_price':100.0,'exit_price':110.0,'pnl':10.0,'return_percent':10.0},
    {'trade_id':2,'entry_index':30,'exit_index':40,'entry_time':'2023-01-03T00:00:00Z','exit_time':'2023-01-04T00:00:00Z','direction':'Long','size':1.0,'entry_price':105.0,'exit_price':95.0,'pnl':-10.0,'return_percent':-9.52},
]

trades = [TradeRecord.model_validate(t) for t in trades_raw]

try:
    mc = run_monte_carlo_from_trades(trades, iterations=200)
    print('MonteCarloResult:', mc)
except Exception as e:
    import traceback
    traceback.print_exc()
    print('Exception:', e)
