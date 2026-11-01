#!/usr/bin/env python3
"""Validate local Markdown links and ADR metadata without third-party packages."""
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
ADR_DIR = ROOT / "docs" / "adr"
LINK_RE = re.compile(r"(?<!!)\[[^\]]*\]\((<[^>]+>|[^)]+)\)")
STATUS_RE = re.compile(
    r"^- (?:Status: (?:Proposed|Accepted|In Progress|Completed|Superseded|Rejected|Deprecated)|状态：(?:提案中|已批准未开始|正在进行|已完成|已被取代|已拒绝|已废弃))$",
    re.MULTILINE,
)
errors = []


def without_fenced_code(markdown_text: str) -> str:
    """Remove fenced code blocks so example links are not treated as live links."""
    lines = markdown_text.splitlines()
    output = []
    fence_char = None
    fence_length = 0

    for line in lines:
        match = re.match(r"^\s*(`{3,}|~{3,})", line)
        if fence_char is None:
            if match:
                fence_char = match.group(1)[0]
                fence_length = len(match.group(1))
                output.append("")
            else:
                output.append(line)
            continue

        if match and match.group(1)[0] == fence_char and len(match.group(1)) >= fence_length:
            fence_char = None
            fence_length = 0
        output.append("")

    return "\n".join(output)


for markdown in sorted(path for path in ROOT.rglob("*.md") if ".git" not in path.parts):
    text = without_fenced_code(markdown.read_text(encoding="utf-8"))
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

for adr in sorted(ADR_DIR.glob("*.md")):
    text = adr.read_text(encoding="utf-8")
    if not STATUS_RE.search(text):
        errors.append(f"{adr.relative_to(ROOT)}: missing or invalid Status metadata")

if errors:
    print("Documentation checks failed:")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print("Documentation checks passed.")
