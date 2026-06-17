"""
MetaTrader 5 Strategy Tester — Forward optimization & Single Backtest report parser.
"""

from __future__ import annotations

import re
import warnings
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping, MutableMapping, Sequence

from schemas import WFAResult

try:
    import xmltodict
except ImportError:
    xmltodict = None  # type: ignore[assignment]

try:
    from bs4 import BeautifulSoup
except ImportError:
    BeautifulSoup = None  # type: ignore[assignment]


@dataclass(slots=True)
class Mt5XmlMapping:
    pass_paths: tuple[tuple[str, ...], ...] = (
        ("Report", "OptimizationResults", "Pass"),
        ("Report", "Pass"),
        ("StrategyTesterReport", "Optimization", "Pass"),
        ("OptimizationReport", "Results", "Pass"),
        ("OptimizationReport", "Pass"),
    )
    in_sample_section: str = "InSample"
    out_of_sample_section: str = "Forward"
    metric_profit: str = "Profit"
    metric_drawdown: str = "Drawdown"
    metric_profit_factor: str = "ProfitFactor"
    metric_win_rate: str = "WinRate"
    parameters_container: str = "Parameters"
    parameter_element: str = "Parameter"
    in_sample_aliases: tuple[str, ...] = ("InSample", "Backtest", "Training", "History", "IS")
    out_of_sample_aliases: tuple[str, ...] = ("Forward", "OutOfSample", "OOS", "ForwardResult")


@dataclass(slots=True)
class Mt5HtmlTableMapping:
    pass_id_columns: tuple[str, ...] = ("pass", "#", "id", "row", "adım", "no")
    is_profit_headers: tuple[str, ...] = ("profit", "result", "kar", "kâr", "sonuç", "bakiye")
    oos_profit_headers: tuple[str, ...] = ("forward result", "forward profit", "out of sample", "oos profit", "forward")
    oos_drawdown_headers: tuple[str, ...] = ("forward drawdown", "oos drawdown", "drawdown", "drawdown %", "düşüş")
    oos_profit_factor_headers: tuple[str, ...] = ("forward profit factor", "profit factor", "kâr faktörü")
    oos_win_rate_headers: tuple[str, ...] = ("forward win", "win rate", "kazanç", "başarı")
    parameter_header_prefix: str = "param"


def _ensure_list(node: Any) -> list[Any]:
    if node is None: return []
    if isinstance(node, list): return node
    return [node]


def _walk_path(root: Mapping[str, Any], path: Sequence[str]) -> Any:
    cur: Any = root
    for part in path:
        if not isinstance(cur, Mapping): return None
        cur = cur.get(part)
        if cur is None: return None
    return cur


def _normalize_header(text: str) -> str:
    t = text.strip().lower()
    replacements = {'ı': 'i', 'i̇': 'i', 'ğ': 'g', 'ü': 'u', 'ş': 's', 'ö': 'o', 'ç': 'c'}
    for tr, eng in replacements.items(): t = t.replace(tr, eng)
    return re.sub(r"\s+", " ", t)


def _parse_money(text: str | None) -> float:
    if text is None: return 0.0
    s = str(text).strip().replace("\u00a0", " ").replace(" ", "")
    s = re.sub(r"[^\d,.\-+eE]", "", s).replace(",", "")
    try: return float(s)
    except ValueError: return 0.0


def _parse_percent(text: str | None) -> float:
    if text is None: return 0.0
    s = str(text).strip().replace("\u00a0", "").replace("%", "").replace(" ", "")
    if s.count(",") == 1 and "." not in s: s = s.replace(",", ".")
    else: s = s.replace(",", "")
    try: val = float(s)
    except ValueError: return 0.0
    if 0.0 < abs(val) <= 1.0 and "%" not in str(text): return abs(val) * 100.0
    return abs(val)


def _parse_factor(text: str | None) -> float:
    val = _parse_money(text)
    return min(val, 999.0)


def _parse_win_rate(text: str | None) -> float:
    if text is None: return 0.0
    raw = str(text).strip()
    if not raw: return 0.0
    val = _parse_percent(raw)
    if "%" in raw or val > 1.5: return max(0.0, min(1.0, val / 100.0))
    return max(0.0, min(1.0, val))


def _find_section(pass_node: Mapping[str, Any], names: Iterable[str]) -> Mapping[str, Any] | None:
    for name in names:
        sec = pass_node.get(name)
        if isinstance(sec, Mapping): return sec
    return None


def _extract_metrics(section: Mapping[str, Any] | None, mapping: Mt5XmlMapping) -> dict[str, float]:
    if not section:
        return {"profit": 0.0, "drawdown": 0.0, "profit_factor": 0.0, "win_rate": 0.0}

    def leaf(*candidate_keys: str) -> str | None:
        for k in candidate_keys:
            if k in section and section[k] not in (None, ""):
                val = section[k]
                if isinstance(val, Mapping) and "#text" in val: return str(val["#text"])
                if isinstance(val, (list, dict)) and not isinstance(val, str): continue
                return str(val)
        return None

    return {
        "profit": _parse_money(leaf(mapping.metric_profit, "NetProfit", "Result")),
        "drawdown": _parse_percent(leaf(mapping.metric_drawdown, "DrawdownPercent", "EquityDD")),
        "profit_factor": _parse_factor(leaf(mapping.metric_profit_factor, "PF")),
        "win_rate": _parse_win_rate(leaf(mapping.metric_win_rate, "WinRatePercent")),
    }


def _extract_parameters(pass_node: Mapping[str, Any], mapping: Mt5XmlMapping) -> dict[str, Any]:
    params: dict[str, Any] = {}
    container = pass_node.get(mapping.parameters_container)
    if container is None:
        for k, v in pass_node.items():
            if not k.startswith("@") and isinstance(v, (str, int, float)): params[k] = v
        return params

    if isinstance(container, Mapping):
        rows = container.get(mapping.parameter_element)
        for row in _ensure_list(rows):
            if not isinstance(row, Mapping): continue
            name = row.get("@Name") or row.get("Name")
            value = row.get("@Value") or row.get("Value")
            if name and str(name).strip(): params[str(name).strip()] = _coerce_param_value(value)
    return params


def _coerce_param_value(value: Any) -> Any:
    if value is None: return None
    if isinstance(value, (int, float, bool)): return value
    s = str(value).strip()
    if re.fullmatch(r"-?\d+", s): return int(s)
    if re.fullmatch(r"-?\d+(\.\d+)?([eE][-+]?\d+)?", s): return float(s)
    return s


def _parse_xml_passes(doc: Mapping[str, Any], mapping: Mt5XmlMapping) -> list[Mapping[str, Any]]:
    for path in mapping.pass_paths:
        passes = _ensure_list(_walk_path(doc, path))
        if passes and all(isinstance(p, Mapping) for p in passes): return passes
    return []


def _pass_to_wfaresult(idx: int, pass_node: Mapping[str, Any], mapping: Mt5XmlMapping) -> WFAResult:
    is_section = _find_section(pass_node, (mapping.in_sample_section, *mapping.in_sample_aliases))
    oos_section = _find_section(pass_node, (mapping.out_of_sample_section, *mapping.out_of_sample_aliases))
    is_metrics = _extract_metrics(is_section, mapping)
    oos_metrics = _extract_metrics(oos_section, mapping)

    return WFAResult(
        test_window_id=idx,
        is_profit=float(is_metrics["profit"]),
        oos_profit=float(oos_metrics["profit"]),
        drawdown_percent=float(oos_metrics["drawdown"]),
        profit_factor=float(oos_metrics["profit_factor"]),
        win_rate=float(oos_metrics["win_rate"]),
        best_parameters=_extract_parameters(pass_node, mapping),
        total_trades=0
    )


def _detect_kind(path: Path, raw_head: str) -> str:
    lower = raw_head.lstrip()[:200].lower()
    if "<html" in lower or "<!doctype html" in lower or path.suffix.lower() in {".html", ".htm"}:
        return "html"
    return "xml"


def _match_header(norm_headers: list[str], patterns: tuple[str, ...]) -> int | None:
    for i, h in enumerate(norm_headers):
        for p in patterns:
            if p in h: return i
    return None


def _read_file_safe(path: Path) -> str:
    try: return path.read_text(encoding="utf-16")
    except Exception: return path.read_text(encoding="utf-8", errors="replace")


def _parse_html_table(path: Path, mapping: Mt5HtmlTableMapping) -> list[WFAResult]:
    if BeautifulSoup is None:
        raise ImportError("HTML parsing requires beautifulsoup4. Install with: pip install beautifulsoup4")

    html_text = _read_file_safe(path)
    soup = BeautifulSoup(html_text, "html.parser")
    text_content = soup.get_text().lower()

    # =========================================================================
    # MOD 1: TEKLİ BACKTEST (SINGLE REPORT) KONTROLÜ
    # =========================================================================
    if "total net profit:" in text_content or "toplam net kâr:" in text_content:
        kv = {}
        # Tablodaki tüm td'leri yan yana tarayıp key-value sözlüğü oluşturuyoruz
        for tr in soup.find_all("tr"):
            tds = tr.find_all(["td", "th"])
            for i in range(len(tds) - 1):
                key = tds[i].get_text(" ", strip=True).lower().replace(":", "")
                val = tds[i+1].get_text(" ", strip=True)
                if key and val: kv[key] = val
        
        profit = float(_parse_money(kv.get("total net profit", kv.get("toplam net kâr", "0"))))
        pf = float(_parse_factor(kv.get("profit factor", kv.get("kâr faktörü", "0"))))
        trades = int(_parse_money(kv.get("total trades", kv.get("toplam işlem", "0"))))

        # Drawdown içinden yüzdelik dilimi çekme (Örn: "410.45 (4.10%)" -> 4.10)
        dd_str = kv.get("equity drawdown maximal", kv.get("maksimal düşüş", "0"))
        if "(" in dd_str and "%" in dd_str:
            dd = float(_parse_percent(dd_str.split("(")[1].replace("%", "").replace(")", "")))
        else:
            dd = float(_parse_percent(dd_str))

        # Kazanma Oranını çekme (Örn: "107 (62.57%)" -> 0.6257)
        wr_str = kv.get("profit trades (% of total)", kv.get("kârlı işlemler", "0"))
        if "(" in wr_str and "%" in wr_str:
            wr = float(_parse_percent(wr_str.split("(")[1].replace("%", "").replace(")", ""))) / 100.0
        else:
            wr = float(_parse_percent(wr_str))

        return [
            WFAResult(
                test_window_id=1,
                is_profit=profit,
                oos_profit=profit, # Tekli test olduğu için In-Sample = Out-of-Sample
                drawdown_percent=dd,
                profit_factor=pf,
                win_rate=wr,
                best_parameters={},
                total_trades=trades
            )
        ]

    # =========================================================================
    # MOD 2: OPTİMİZASYON RAPORU (ÇOKLU TABLO) KONTROLÜ
    # =========================================================================
    table = soup.find("table")
    if table is None: return []

    rows = table.find_all("tr")
    if not rows: return []

    header_row_idx = 0
    headers = []
    for i, row in enumerate(rows[:10]):
        cells = row.find_all(["th", "td"])
        if len(cells) > 3:
            header_row_idx = i
            headers = [_normalize_header(c.get_text(" ", strip=True)) for c in cells]
            break

    if not headers: return []

    idx_is = _match_header(headers, mapping.is_profit_headers)
    idx_oos = _match_header(headers, mapping.oos_profit_headers)
    idx_dd = _match_header(headers, mapping.oos_drawdown_headers)
    idx_pf = _match_header(headers, mapping.oos_profit_factor_headers)
    idx_wr = _match_header(headers, mapping.oos_win_rate_headers)
    idx_tr = _match_header(headers, ("trades", "işlem"))

    param_cols = []
    for i, h in enumerate(headers):
        if mapping.parameter_header_prefix in h or "=" in h:
            original_text = rows[header_row_idx].find_all(["th", "td"])[i].get_text(" ", strip=True)
            param_cols.append((i, original_text))

    results = []
    pass_counter = 0

    for tr in rows[header_row_idx + 1:]:
        cells = tr.find_all(["td", "th"])
        if not cells or len(cells) < 3: continue
        texts = [c.get_text(" ", strip=True) for c in cells]

        def cell(idx: int | None) -> str | None:
            if idx is None or idx >= len(texts): return None
            return texts[idx]

        pass_counter += 1
        is_profit = _parse_money(cell(idx_is))
        oos_profit = _parse_money(cell(idx_oos))
        dd = _parse_percent(cell(idx_dd))
        pf = _parse_factor(cell(idx_pf))
        wr = _parse_win_rate(cell(idx_wr))
        trd = int(_parse_money(cell(idx_tr))) if cell(idx_tr) else 0

        params = {}
        for i, raw_name in param_cols:
            raw_val = cell(i)
            if raw_val is None: continue
            if "=" in raw_name:
                parts = raw_name.split("=", 1)
                pname, pval = parts[0].strip(), parts[1].strip() or raw_val
            else:
                pname, pval = raw_name.strip(), raw_val
            if pname: params[pname] = _coerce_param_value(pval)

        results.append(
            WFAResult(
                test_window_id=pass_counter,
                is_profit=float(is_profit),
                oos_profit=float(oos_profit),
                drawdown_percent=float(dd),
                profit_factor=float(pf),
                win_rate=float(wr),
                best_parameters=params,
                total_trades=trd
            )
        )

    return results


def parse_mt5_report(
    file_path: str,
    *,
    xml_mapping: Mt5XmlMapping | None = None,
    html_mapping: Mt5HtmlTableMapping | None = None,
) -> list[WFAResult]:
    path = Path(file_path)
    if not path.is_file(): raise FileNotFoundError(str(path))

    raw_head = _read_file_safe(path)[:4096]
    kind = _detect_kind(path, raw_head)

    if kind == "html":
        return _parse_html_table(path, html_mapping or Mt5HtmlTableMapping())

    if xmltodict is None:
        raise ImportError("XML parsing requires xmltodict. Install with: pip install xmltodict")

    xml_text = _read_file_safe(path)
    doc_any: Any = xmltodict.parse(xml_text)
    if not isinstance(doc_any, MutableMapping): return []

    doc: Mapping[str, Any] = doc_any
    mapping = xml_mapping or Mt5XmlMapping()
    passes = _parse_xml_passes(doc, mapping)

    if not passes: return []
    return [_pass_to_wfaresult(i + 1, p, mapping) for i, p in enumerate(passes)]