"""Builds docs/COMPATIBILITY.md from hardware reports.

Sources:
  * GitHub issues labelled "hardware-report" (opened from Kelvin's "Report my hardware" button),
    excluding any labelled "invalid";
  * docs/compatibility-seed.json, for hardware the maintainers tested themselves.

Only the structured tables Kelvin writes are read. The reporter's own text is never copied, and
every value is escaped, so a report can't inject anything into the page.

Run locally:  python .github/scripts/build_compat.py --issues issues.json
In CI:        GITHUB_TOKEN and GITHUB_REPOSITORY are used to fetch issues.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import urllib.request
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SEED = ROOT / "docs" / "compatibility-seed.json"
OUT = ROOT / "docs" / "COMPATIBILITY.md"
MAX = 80  # longest value we'll print


def clean(value: str) -> str:
    """Plain hardware names only: no markup, no links (GitHub auto-links bare URLs), no table breaks."""
    value = re.sub(r"[\r\n\t]+", " ", value)
    value = re.sub(r"\S*(?:://|www\.)\S*", "", value, flags=re.I)        # drop anything URL-like
    value = re.sub(r"\S+\.(?:com|net|org|io|dev|xyz|ru|cn|example)\b\S*", "", value, flags=re.I)
    value = re.sub(r"[^\w\s\-.()/+&,']", "", value)                      # letters, digits, simple punctuation
    value = re.sub(r"\s{2,}", " ", value).strip()
    return value[:MAX]


def device_name(vendor: str, name: str) -> str:
    vendor, name = clean(vendor), clean(name)
    return name if not vendor or name.lower().startswith(vendor.lower()) else f"{vendor} {name}"


def sections(body: str) -> dict[str, str]:
    parts = re.split(r"^###\s+(.+)$", body, flags=re.M)
    return {parts[i].strip().lower(): parts[i + 1] for i in range(1, len(parts) - 1, 2)}


def table(text: str) -> list[list[str]]:
    rows = []
    for line in text.splitlines():
        line = line.strip()
        if not line.startswith("|") or re.match(r"^\|[\s\-|:]+\|$", line):
            continue
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip("|"))]
        rows.append(cells)
    return rows[1:] if rows else []  # drop the header row


def parse_report(body: str) -> dict | None:
    s = sections(body or "")
    if "detected hardware" not in s:
        return None
    system = s.get("system", "")
    if "simulated hardware" in system:
        return None  # demo or simulated runs say nothing about real hardware

    hw = table(s["detected hardware"])
    def first(kind_prefix: str) -> str:
        for kind, name in (r[:2] for r in hw if len(r) >= 2):
            if kind.replace("↳", "").strip().lower().startswith(kind_prefix):
                return clean(name)
        return ""

    if not first("motherboard"):
        return None  # nothing usable to list (garbled or hostile report)

    fans = [r for r in table(s.get("fans", "")) if len(r) >= 4]
    controllable = sum(1 for r in fans if r[3].strip().lower() == "yes")
    lights = [r for r in table(s.get("lighting (openrgb)", "")) if len(r) >= 3]
    version = re.search(r"Kelvin\s+(\d+\.\d+\.\d+)", system)
    laptop = re.search(r"Laptop fan modes:\s*([^\n:]+)", system)

    return {
        "board": first("motherboard") or "Unknown board",
        "chip": first("superio") or first("embeddedcontroller"),
        "cpu": first("cpu"),
        "fans_total": len(fans),
        "fans_controllable": controllable,
        "laptop_modes": clean(laptop.group(1)) if laptop else "",
        "lighting": sorted({device_name(r[0], r[1]) for r in lights} - {""}),
        "version": version.group(1) if version else "",
    }


def fetch_issues() -> list[dict]:
    token = os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GITHUB_REPOSITORY", "nanwer/kelvin")
    issues, page = [], 1
    while True:
        url = f"https://api.github.com/repos/{repo}/issues?labels=hardware-report&state=all&per_page=100&page={page}"
        req = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json", **({"Authorization": f"Bearer {token}"} if token else {})})
        with urllib.request.urlopen(req, timeout=30) as r:
            batch = json.load(r)
        if not batch:
            return issues
        issues += [i for i in batch if "pull_request" not in i]
        page += 1


def build(issues: list[dict]) -> str:
    entries = defaultdict(list)
    seed = json.loads(SEED.read_text(encoding="utf-8")) if SEED.exists() else []
    for e in seed:
        entries[clean(e["board"])].append({**e, "source": "Tested by the maintainers"})

    for issue in issues:
        labels = {l["name"].lower() for l in issue.get("labels", [])}
        if "invalid" in labels:
            continue
        parsed = parse_report(issue.get("body") or "")
        if parsed:
            entries[parsed["board"]].append({**parsed, "source": f"[#{issue['number']}]({issue['html_url']})"})

    def yes(n: int, total: int) -> str:
        if total == 0:
            return "–"
        return f"✓ {n} of {total}" if n else "✗ read only"

    lines = [
        "# Hardware compatibility",
        "",
        "What Kelvin detected on real PCs, from hardware reports people sent with **Settings → Report my hardware**",
        "and from the maintainers' own machines. This page is rebuilt automatically when reports arrive.",
        "",
        "Fan control comes from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)",
        "and lighting from [OpenRGB](https://openrgb.org), so hardware they support usually works even if it isn't listed yet.",
        "",
        f"**{len(entries)} {'board' if len(entries) == 1 else 'boards'}** so far.",
        "",
        "| Board | Fan chip | Fan control | Laptop fan modes | Lighting devices | Kelvin | Reports |",
        "|---|---|---|---|---|---|---|",
    ]
    for board in sorted(entries, key=str.lower):
        reps = entries[board]
        best = max(reps, key=lambda r: (r.get("fans_controllable", 0), r.get("version", "")))
        # Merge across reports, ignoring case, keeping the first spelling seen.
        seen: dict[str, str] = {}
        for d in (clean(x) for r in reps for x in r.get("lighting", [])):
            if d:
                seen.setdefault(d.lower(), d)
        lighting = sorted(seen.values(), key=str.lower)
        lighting_text = ", ".join(lighting[:6]) + (f" and {len(lighting) - 6} more" if len(lighting) > 6 else "") if lighting else "–"
        lines.append("| " + " | ".join([
            board,
            best.get("chip") or "–",
            yes(best.get("fans_controllable", 0), best.get("fans_total", 0)),
            best.get("laptop_modes") or "–",
            lighting_text,
            best.get("version") or "–",
            ", ".join(r["source"] for r in reps),
        ]) + " |")
    lines += [
        "",
        "Missing your hardware? In Kelvin, open **Settings → Report my hardware**. It takes a few seconds",
        "and helps everyone with the same parts.",
        "",
    ]
    return "\n".join(lines)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--issues", help="read issues from a JSON file instead of the GitHub API")
    args = ap.parse_args()
    issues = json.loads(Path(args.issues).read_text(encoding="utf-8")) if args.issues else fetch_issues()
    OUT.write_text(build(issues), encoding="utf-8", newline="\n")
    print(f"wrote {OUT.relative_to(ROOT)}")
