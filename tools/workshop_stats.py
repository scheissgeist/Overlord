#!/usr/bin/env python3
"""Track a Steam Workshop item's public stats over time.

Scrapes the PUBLIC item page (no auth) and appends a timestamped row to a CSV,
so you get a time-series of unique visitors / subscribers / favorites. Run it on
a schedule (Task Scheduler / cron) to build a growth curve.

WHAT THIS CAN SEE (public, no login):
  - unique visitors (lifetime)
  - current subscribers
  - current favorites
  - posted / updated dates
  - removal/moderation notice (so you know if the item got pulled)

WHAT IT CANNOT SEE (owner-only, no public API):
  - downloads-per-day, views-per-day, referrers  -> those live on the owner
    "Stats" tab behind your logged-in Steam session. No clean API exists.

USAGE:
  python tools/workshop_stats.py                      # default item 3760983440 (Overlord)
  python tools/workshop_stats.py --id 3760983440
  python tools/workshop_stats.py --csv data/workshop_stats.csv
  python tools/workshop_stats.py --report            # print the log as a table, no fetch

The CSV columns: timestamp_utc, item_id, unique_visitors, subscribers, favorites,
posted, updated, removed(bool), title.
"""
import argparse
import csv
import datetime as dt
import os
import re
import sys
import urllib.request

DEFAULT_ID = "3760983440"  # Overlord
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/126.0 Safari/537.36")


def fetch(item_id: str) -> str:
    url = f"https://steamcommunity.com/sharedfiles/filedetails/?id={item_id}"
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read().decode("utf-8", errors="replace")


def _num(html: str, label: str):
    """Steam's stats table is plain: <td>277</td><td>Unique Visitors</td>.

    Match the value cell immediately followed by the label cell — this is the
    reliable pairing. (An earlier version guessed at stats_table_value CSS
    classes that Steam does not use, and the loose fallback grabbed the wrong
    adjacent number: 277/8/5 came back as 8/5/5. Verified against the live page
    2026-07-10.)
    """
    m = re.search(
        r'<td>\s*([\d,]+)\s*</td>\s*<td>\s*' + re.escape(label),
        html, re.IGNORECASE)
    return int(m.group(1).replace(",", "")) if m else None


def _date(html: str, label: str):
    # "Posted" / "Updated" appear near their date text in the details block.
    m = re.search(re.escape(label) + r'\s*</div>\s*<div[^>]*>\s*([^<]+?)\s*</div>',
                  html, re.IGNORECASE)
    return m.group(1).strip() if m else None


def parse(html: str) -> dict:
    tm = re.search(r'<div class="workshopItemTitle">\s*([^<]+?)\s*</div>', html)
    title = tm.group(1).strip() if tm else None
    removed = "has been removed from the community" in html.lower()
    return {
        "title": title,
        "unique_visitors": _num(html, "Unique Visitors"),
        "subscribers": _num(html, "Current Subscribers"),
        "favorites": _num(html, "Current Favorites"),
        "posted": _date(html, "Posted"),
        "updated": _date(html, "Updated"),
        "removed": removed,
    }


def append_row(csv_path: str, item_id: str, data: dict):
    os.makedirs(os.path.dirname(os.path.abspath(csv_path)) or ".", exist_ok=True)
    new = not os.path.exists(csv_path)
    with open(csv_path, "a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["timestamp_utc", "item_id", "unique_visitors",
                        "subscribers", "favorites", "posted", "updated",
                        "removed", "title"])
        w.writerow([
            dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            item_id, data["unique_visitors"], data["subscribers"],
            data["favorites"], data["posted"], data["updated"],
            int(bool(data["removed"])), data["title"],
        ])


def report(csv_path: str):
    if not os.path.exists(csv_path):
        print(f"No log yet at {csv_path}")
        return
    with open(csv_path, encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        print("Log is empty.")
        return
    print(f"{'timestamp':<21} {'visitors':>8} {'subs':>5} {'favs':>5} {'removed':>7}")
    prev = None
    for r in rows:
        v = r["unique_visitors"] or "0"
        delta = ""
        if prev is not None and r["unique_visitors"] and prev.isdigit():
            d = int(r["unique_visitors"]) - int(prev)
            delta = f" (+{d})" if d >= 0 else f" ({d})"
        print(f"{r['timestamp_utc']:<21} {v:>8}{delta:<7} {r['subscribers'] or '-':>5} "
              f"{r['favorites'] or '-':>5} {('YES' if r['removed']=='1' else 'no'):>7}")
        prev = r["unique_visitors"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--id", default=DEFAULT_ID)
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap.add_argument("--csv", default=os.path.join(here, "data", "workshop_stats.csv"))
    ap.add_argument("--report", action="store_true", help="print the log, no fetch")
    args = ap.parse_args()

    if args.report:
        report(args.csv)
        return

    try:
        html = fetch(args.id)
    except Exception as e:
        print(f"fetch failed: {e}", file=sys.stderr)
        sys.exit(1)
    data = parse(html)
    append_row(args.csv, args.id, data)
    status = "REMOVED" if data["removed"] else "live"
    print(f"[{status}] {data['title']}: "
          f"visitors={data['unique_visitors']} subs={data['subscribers']} "
          f"favs={data['favorites']} -> logged to {args.csv}")


if __name__ == "__main__":
    main()
