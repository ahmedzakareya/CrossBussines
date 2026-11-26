"""Quality gates for locally trained intent-model candidates."""
from __future__ import annotations

MAX_GENERATED_WRONG_RATE = 0.005
MAX_GENERATED_LEAK_RATE = 0.002
MIN_REAL_CORRECT = 20
MAX_REAL_WRONGLY_REFUSED = 2


def candidate_is_acceptable(real: dict, generated: dict) -> bool:
    """Require perfect precision on human-written cases plus bounded synthetic regressions."""
    total = max(1, int(generated.get("total", 0)))
    return (
        int(real.get("wrong", 0)) == 0
        and int(real.get("leaked", 0)) == 0
        and float(real.get("precision", 0.0)) == 1.0
        and int(real.get("correct", 0)) >= MIN_REAL_CORRECT
        and int(real.get("refusedWrongly", total)) <= MAX_REAL_WRONGLY_REFUSED
        and int(generated.get("wrong", total)) / total <= MAX_GENERATED_WRONG_RATE
        and int(generated.get("leaked", total)) / total <= MAX_GENERATED_LEAK_RATE
    )
