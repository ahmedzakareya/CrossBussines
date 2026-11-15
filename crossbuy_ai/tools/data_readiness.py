"""Measure the data under every AI capability, and refuse to let it slip backwards.

Run:  python -I tools/data_readiness.py            (from crossbuy_ai/)
      python -I tools/data_readiness.py --accept   (record today's figures as the new floor)

WHY A RATCHET AND NOT A PASS/FAIL. Five of six capabilities are short of data today, so a check
that fails whenever anything is short would fail every time and be ignored within a week. What
must never happen is the other thing: a capability that HAD enough data quietly losing it, because
that is invisible from every screen — the assistant simply starts answering "no record" more
often, and nobody connects the two.

So the floor is whatever was last accepted, and the only failure is going below it. Improvement is
free; regression is loud.

THE NUMBERS COME FROM SQL, not from an ORM, on purpose: this must stay runnable when the .NET
build is red, which it has been for most of this project's life.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
SQL = os.path.join(HERE, "data_readiness.sql")
BASELINE = os.path.join(HERE, "data_readiness_baseline.json")

SERVER = os.environ.get("CROSSBUY_SQL_SERVER", "localhost")
DATABASE = os.environ.get("CROSSBUY_SQL_DATABASE", "CrossBuyDB2")

# A capability may dip this far below its accepted floor before it counts as a regression. Coverage
# moves a little whenever anyone adds a catalogue row without the operational data behind it, and a
# check that cries at half a percent is a check nobody reads.
TOLERANCE = 2.0


def measure() -> dict[str, dict]:
    out = subprocess.run(
        ["sqlcmd", "-S", SERVER, "-E", "-C", "-d", DATABASE, "-i", SQL, "-W", "-s", "|", "-h", "-1"],
        capture_output=True, text=True, timeout=300,
    )
    if out.returncode != 0:
        print(out.stderr.strip() or out.stdout.strip())
        raise SystemExit(f"sqlcmd failed against {SERVER}/{DATABASE}")

    rows: dict[str, dict] = {}
    for line in out.stdout.splitlines():
        parts = [p.strip() for p in line.split("|")]
        if len(parts) != 6 or parts[0] in ("capability", "") or set(parts[0]) == {"-"}:
            continue
        name, requires, have, of, pct, verdict = parts
        try:
            rows[name] = {"requires": requires, "have": int(have), "of": int(of),
                          "pct": float(pct), "verdict": verdict}
        except ValueError:
            continue
    return rows


def main() -> int:
    rows = measure()
    if not rows:
        raise SystemExit("no rows parsed — has the schema or the query changed?")

    baseline = {}
    if os.path.exists(BASELINE):
        with open(BASELINE, encoding="utf-8") as fh:
            baseline = json.load(fh)

    print(f"\n  data readiness — {DATABASE}\n")
    print(f"    {'capability':<20} {'coverage':>18}  {'was':>7}   verdict")
    regressions: list[str] = []

    for name, row in rows.items():
        was = baseline.get(name, {}).get("pct")
        delta = "" if was is None else f"{was:>6.1f}%"
        flag = ""
        if was is not None and row["pct"] < was - TOLERANCE:
            regressions.append(f"{name}: {was:.1f}% -> {row['pct']:.1f}%  ({row['requires']})")
            flag = "  <- REGRESSED"
        elif was is not None and row["pct"] > was + TOLERANCE:
            flag = "  <- improved"
        print(f"    {name:<20} {row['have']:>6} / {row['of']:<5} {row['pct']:>5.1f}%  "
              f"{delta:>7}   {row['verdict']}{flag}")

    not_ready = [n for n, r in rows.items() if r["verdict"] != "ready"]
    print(f"\n    {len(rows) - len(not_ready)} of {len(rows)} capabilities have the data they need.")
    if not_ready:
        print("    Short of data (not a failure — a fact to act on):")
        for n in not_ready:
            print(f"      - {n}: {rows[n]['requires']} — {rows[n]['have']} of {rows[n]['of']}")

    if "--accept" in sys.argv:
        with open(BASELINE, "w", encoding="utf-8") as fh:
            json.dump(rows, fh, ensure_ascii=False, indent=1)
        print(f"\n    accepted as the new floor -> {BASELINE}")
        return 0

    if regressions:
        print("\n    REGRESSED — a capability lost data it used to have:")
        for r in regressions:
            print(f"      - {r}")
        print("    This is invisible from every screen: the assistant simply starts saying")
        print("    'no record' more often. Find what dropped, or re-accept deliberately.")
        return 1

    if not baseline:
        print("\n    no floor recorded yet — run with --accept to set one")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
