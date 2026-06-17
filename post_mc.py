import requests

url = 'http://127.0.0.1:8000/api/v1/monte-carlo'
payload = {
    'trades': [
        {
            'trade_id': 1,
            'entry_index': 10,
            'exit_index': 20,
            'entry_time': '2023-01-01T00:00:00Z',
            'exit_time': '2023-01-02T00:00:00Z',
            'direction': 'Long',
            'size': 1.0,
            'entry_price': 100.0,
            'exit_price': 110.0,
            'pnl': 10.0,
            'return_percent': 10.0,
        },
        {
            'trade_id': 2,
            'entry_index': 30,
            'exit_index': 40,
            'entry_time': '2023-01-03T00:00:00Z',
            'exit_time': '2023-01-04T00:00:00Z',
            'direction': 'Long',
            'size': 1.0,
            'entry_price': 105.0,
            'exit_price': 95.0,
            'pnl': -10.0,
            'return_percent': -9.52,
        },
    ],
    'iterations': 200,
}

try:
    r = requests.post(url, json=payload, timeout=30)
    print('STATUS', r.status_code)
    print(r.text)
except Exception as e:
    print('EX', repr(e))
