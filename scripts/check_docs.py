#!/usr/bin/env python3
"""Validate local Markdown links and ADR metadata without third-party packages."""
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
ADR_DIR = ROOT / "docs" / "adr"
LINK_RE = re.compile(r"(?<!!)\[[^\]]*\]\((<[^>]+>|[^)]+)\)")

# ADR 文件头使用双状态模型（Decision + Implementation），见 docs/adr/README.md。
ADR_FILENAME_RE = re.compile(r"^(?P<number>\d{4})-[a-z0-9-]+\.md$")
DECISION_RE = re.compile(
    r"^- Decision: (?P<value>Proposed|Accepted|Rejected|Superseded|Deprecated)$",
    re.MULTILINE,
)
IMPLEMENTATION_RE = re.compile(
    r"^- Implementation: (?P<value>Not Started|In Progress|Implemented|Verified|N/A)$",
    re.MULTILINE,
)
DATE_RE = re.compile(r"^- Date: (?P<value>\d{4}-\d{2}-\d{2}|Unknown)$", re.MULTILINE)
SCOPE_RE = re.compile(r"^- Scope: \S", re.MULTILINE)
README_ROW_RE = re.compile(
    r"^\|\s*\[(?P<number>\d{4})[^\]]*\]\((?P<target>[^)]+)\)\s*"
    r"\|\s*(?P<decision>[^|]+?)\s*\|\s*(?P<implementation>[^|]+?)\s*\|",
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

adr_headers = {}
for adr in sorted(ADR_DIR.glob("*.md")):
    if adr.name == "README.md":
        continue

    relative = adr.relative_to(ROOT)
    filename = ADR_FILENAME_RE.match(adr.name)
    if not filename:
        errors.append(f"{relative}: ADR 文件名必须是 NNNN-kebab-case.md")
        continue

    text = adr.read_text(encoding="utf-8")
    decision = DECISION_RE.search(text)
    implementation = IMPLEMENTATION_RE.search(text)
    if not decision:
        errors.append(f"{relative}: missing or invalid '- Decision:' metadata")
    if not implementation:
        errors.append(f"{relative}: missing or invalid '- Implementation:' metadata")
    if not DATE_RE.search(text):
        errors.append(f"{relative}: missing or invalid '- Date: YYYY-MM-DD' metadata")
    if not SCOPE_RE.search(text):
        errors.append(f"{relative}: missing '- Scope:' metadata")
    if decision and implementation:
        adr_headers[filename.group("number")] = (
            adr.name,
            decision.group("value"),
            implementation.group("value"),
        )

index_path = ADR_DIR / "README.md"
index_text = index_path.read_text(encoding="utf-8")
index_rows = {}
for match in README_ROW_RE.finditer(index_text):
    number = match.group("number")
    if number in index_rows:
        errors.append(f"{index_path.relative_to(ROOT)}: ADR {number} 在索引中重复")
        continue
    index_rows[number] = (
        match.group("target").strip(),
        match.group("decision").strip(),
        match.group("implementation").strip(),
    )

for number, (filename, decision, implementation) in sorted(adr_headers.items()):
    row = index_rows.get(number)
    if row is None:
        errors.append(f"{index_path.relative_to(ROOT)}: 缺少 ADR {number} 的索引行")
        continue
    target, row_decision, row_implementation = row
    if target != filename:
        errors.append(
            f"{index_path.relative_to(ROOT)}: ADR {number} 索引链接指向 {target}，实际文件为 {filename}"
        )
    if row_decision != decision:
        errors.append(
            f"{index_path.relative_to(ROOT)}: ADR {number} 索引 Decision 为 {row_decision}，文件头为 {decision}"
        )
    if row_implementation != implementation:
        errors.append(
            f"{index_path.relative_to(ROOT)}: ADR {number} 索引 Implementation 为 {row_implementation}，文件头为 {implementation}"
        )

for number in sorted(set(index_rows) - set(adr_headers)):
    errors.append(f"{index_path.relative_to(ROOT)}: 索引中的 ADR {number} 不存在对应文件")

if errors:
    print("Documentation checks failed:")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print("Documentation checks passed.")
