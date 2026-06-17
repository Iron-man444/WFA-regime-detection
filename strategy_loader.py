"""
Dynamic discovery of ``STRATEGY_PARAMS`` from user-supplied ``.py`` strategy modules.
"""

from __future__ import annotations

import importlib.util
import re
from pathlib import Path
from typing import Any

STRATEGIES_DIR = Path(__file__).resolve().parent / "strategies"

_FILENAME_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*\.py$")


def _sanitize_filename(strategy_filename: str) -> str:
    """Reject path traversal; only allow a single ``.py`` basename."""
    name = Path(strategy_filename).name
    if not name or name != strategy_filename.strip() or ".." in strategy_filename:
        raise ValueError("Invalid strategy filename.")
    if not _FILENAME_PATTERN.fullmatch(name):
        raise ValueError("Strategy filename must match [A-Za-z0-9][A-Za-z0-9_.-]*.py")
    return name


def _coerce_spec_value(raw: Any) -> Any:
    if raw is None:
        return None
    if isinstance(raw, (int, float, bool, str)):
        return raw
    return str(raw)


def load_strategy_module(strategy_filename: str) -> Any:
    """
    Load a strategy module from ``strategies/<strategy_filename>`` via ``importlib``.

    The module must define ``execute_strategy(ohlcv, params)``.
    """
    safe_name = _sanitize_filename(strategy_filename)
    module_path = (STRATEGIES_DIR / safe_name).resolve()

    if STRATEGIES_DIR.resolve() not in module_path.parents:
        raise ValueError("Strategy path escapes the strategies directory.")

    if not module_path.is_file():
        raise FileNotFoundError(f"Strategy file not found: {safe_name}")

    module_name = f"wfa_strategy_{safe_name.replace('.', '_')}"
    spec = importlib.util.spec_from_file_location(module_name, module_path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Unable to load strategy module: {safe_name}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_strategy_parameters(strategy_filename: str) -> list[dict[str, Any]]:
    """
    Load ``STRATEGY_PARAMS`` from ``strategies/<strategy_filename>`` via ``importlib``.

    Returns a list of dicts with keys:
    ``variable``, ``default_value``, ``start``, ``step``, ``stop``.
    """
    module = load_strategy_module(strategy_filename)
    safe_name = _sanitize_filename(strategy_filename)

    params = getattr(module, "STRATEGY_PARAMS", None)
    if not isinstance(params, dict):
        raise ValueError(
            f"{safe_name} must define STRATEGY_PARAMS as a top-level dict."
        )

    rows: list[dict[str, Any]] = []
    for variable, definition in params.items():
        if not isinstance(definition, dict):
            raise ValueError(f"STRATEGY_PARAMS['{variable}'] must be a dict.")

        default_value = _coerce_spec_value(definition.get("default"))
        start = _coerce_spec_value(definition.get("min", definition.get("start")))
        stop = _coerce_spec_value(definition.get("max", definition.get("stop")))
        step = _coerce_spec_value(definition.get("step"))

        rows.append(
            {
                "variable": str(variable),
                "default_value": default_value,
                "start": start,
                "step": step,
                "stop": stop,
            }
        )

    return rows
