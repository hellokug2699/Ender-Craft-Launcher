#!/usr/bin/env python3
"""Validate local Markdown links and basic ADR metadata without third-party packages."""
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
MARKDOWN_ROOTS = [ROOT]
LINK_RE = re.compile(r"(?<!!)\[[^\]]*\]\((<[^>]+>|[^)]+)\)")
errors = []

for markdown in sorted(DOCS.rglob("*.md")):
    text = markdown.read_text(encoding="utf-8")
    for match in LINK_RE.finditer(text):
        raw = match.group(1).strip()
        if raw.startswith("<") and raw.endswith(">"):
            raw = raw[1:-1].strip()
        destination = raw.split(maxsplit=1)[0] if raw else ""
        parsed = urlsplit(destination)
        if not destination or parsed.scheme or parsed.netloc or destination.startswith("#"):
            continue
        path_part = unquote(parsed.path)
        if not path_part:
            continue
        target = (markdown.parent / path_part).resolve()
        try:
            target.relative_to(ROOT.resolve())
        except ValueError:
            errors.append(f"{markdown.relative_to(ROOT)}: link escapes repository: {destination}")
            continue
        if not target.exists():
            errors.append(f"{markdown.relative_to(ROOT)}: broken link: {destination}")

for adr in sorted((DOCS / "adr").glob("*.md")):
    text = adr.read_text(encoding="utf-8")
    if not re.search(r"^- 状态：.+$", text, re.MULTILINE):
        errors.append(f"{adr.relative_to(ROOT)}: missing '- 状态：...' metadata")

if errors:
    print("Documentation checks failed:")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print("Documentation checks passed.")
