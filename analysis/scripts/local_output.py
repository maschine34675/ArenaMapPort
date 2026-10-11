"""Keep raw analysis exports out of the public source tree."""
from pathlib import Path

LOCAL_ANALYSIS = Path(__file__).resolve().parents[2] / "work" / "local-analysis"


def output_path(name: str) -> Path:
    LOCAL_ANALYSIS.mkdir(parents=True, exist_ok=True)
    return LOCAL_ANALYSIS / name
