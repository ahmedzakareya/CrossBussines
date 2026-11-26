"""Snapshot the .NET intent catalogue into tests/catalogue.json.

Run:  python -I tools/extract_catalogue.py      (from crossbuy_ai/)

WHY A SNAPSHOT AND NOT A LIVE READ. The tests must run with no .NET build, no database and no
service; a fixture does that. The cost is that it goes stale, so re-run this after changing any
handler's Keywords, Example or Description — a stale fixture turns the tests from a check on the
product into a check on its history.

ONE CLASS AT A TIME. Two handlers share AskAccountingHandlers.cs and two share
AskInventoryHandlers.cs. A file-wide regex handed the second handler in each file the FIRST one's
keyword list, which showed up downstream as impossible exact ties — my-warehouses scoring 0.919 on
"اخر حركاته" because it had been given the movements handler's keywords. The tool, not the
product, was wrong; splitting on the class declaration is what makes it right.
"""
from __future__ import annotations

import glob
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
HANDLERS = os.path.normpath(os.path.join(ROOT, "..", "CrossBuy", "BL", "Platform", "Ai"))
OUT = os.path.join(ROOT, "tests", "catalogue.json")

# The catalogue has both hand-written handlers and composed-surface handlers. Omitting the latter
# left the Python fixture at ten intents while the runtime and trained model exposed 24.
CLASS = re.compile(r"public sealed class \w+\s*:\s*(?:IAskHandler|AskSurfaceQueryHandler)")
INTENT = re.compile(r'IntentId\s*=\s*"([^"]+)"')
KEYWORDS = re.compile(r"Keywords\s*\{\s*get;\s*\}\s*=\s*new\[\]\s*\{(.*?)\n\t*\};", re.S)
EXAMPLE = re.compile(r'Example\s*=>\s*AskText\.Of\(\s*"([^"]*)"\s*,\s*"([^"]*)"')
DESCRIPTION = re.compile(r'Description\s*=>\s*AskText\.Of\(\s*"([^"]*)"\s*,\s*"([^"]*)"', re.S)
STRING = re.compile(r'"([^"]+)"')


def extract() -> dict:
    out: dict = {}
    for path in sorted(glob.glob(os.path.join(HANDLERS, "Ask*.cs"))):
        with io.open(path, encoding="utf-8-sig") as fh:
            src = fh.read()
        cuts = [m.start() for m in CLASS.finditer(src)] + [len(src)]
        for start, end in zip(cuts, cuts[1:]):
            block = src[start:end]
            intent = INTENT.search(block)
            if not intent:
                continue
            kw = KEYWORDS.search(block)
            ex = EXAMPLE.search(block)
            de = DESCRIPTION.search(block)
            out[intent.group(1)] = {
                "keywords": STRING.findall(kw.group(1)) if kw else [],
                "example": list(ex.groups()) if ex else [],
                "desc": list(de.groups()) if de else [],
            }
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    catalogue = extract()
    if not catalogue:
        print(f"no handlers found under {HANDLERS}", file=sys.stderr)
        raise SystemExit(1)

    # A handler with no phrasings is unreachable; say so rather than writing a fixture that
    # quietly tests nothing.
    for intent, body in catalogue.items():
        if not (body["keywords"] or body["example"]):
            print(f"  WARNING  {intent} has no keywords and no example — nothing can reach it")

    with io.open(OUT, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(catalogue, ensure_ascii=False, indent=1))
    print(f"{len(catalogue)} intents -> {OUT}")
    for intent, body in catalogue.items():
        print(f"  {intent:<32} {len(body['keywords']):>3} keywords")
