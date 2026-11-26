"""Dependency-free normalization shared by training-data validation and intent matching."""
from __future__ import annotations

import re
import unicodedata

_TASHKEEL = re.compile(r"[ً-ْـ]")
_NONWORD = re.compile(r"[^\w\s]", re.UNICODE)
_SPACES = re.compile(r"\s+")
_FOLD = {"أ": "ا", "إ": "ا", "آ": "ا", "ٱ": "ا", "ى": "ي", "ة": "ه", "ؤ": "و", "ئ": "ي"}


def normalise(text: str) -> str:
    """Fold common Arabic spelling forms, digits, punctuation, and whitespace."""
    if not text:
        return ""
    text = unicodedata.normalize("NFKC", text)
    text = _TASHKEEL.sub("", text)
    out = []
    for ch in text:
        if ch in _FOLD:
            out.append(_FOLD[ch])
        elif "٠" <= ch <= "٩":
            out.append(chr(ord("0") + ord(ch) - 0x0660))
        elif "۰" <= ch <= "۹":
            out.append(chr(ord("0") + ord(ch) - 0x06F0))
        else:
            out.append(ch.lower())
    return _SPACES.sub(" ", _NONWORD.sub(" ", "".join(out))).strip()
