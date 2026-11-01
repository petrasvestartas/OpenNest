"""End-to-end test of a running opennest_service (standard library only).

    python -I src/opennest_service/tests/e2e.py --url http://localhost:8080 --password <OPENNEST_PASSWORD>

Covers: the password, a session .pb job (settings from the query) with SSE progress, input / live /
result scenes, a JSON job, the latest-result link, cancelling running and queued jobs, validation,
the job list. Exit 0 when every check passes.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXAMPLES = HERE.parent.parent / "opennest_job" / "examples"
SESSION = HERE.parent / "examples" / "clearance_session.pb"
FAILS: list[str] = []
PW = ""


def check(cond: bool, what: str) -> None:
    print(("  ok    " if cond else "  FAIL  ") + what)
    if not cond:
        FAILS.append(what)


def call(url: str, method: str = "GET", body=None, password: str | None = None, content_type: str | None = None):
    if body is None:
        data = None
    elif isinstance(body, bytes):
        data = body
    else:
        data = json.dumps(body).encode()
        content_type = content_type or "application/json"
    req = urllib.request.Request(url, data=data, method=method)
    if content_type:
        req.add_header("Content-Type", content_type)
    pw = PW if password is None else password
    if pw:
        req.add_header("Authorization", f"Bearer {pw}")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw[:1] in (b"{", b"[") else raw)
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except ValueError:
            return e.code, raw


def sse(url: str, timeout: float = 600):
    out, ev = [], None
    with urllib.request.urlopen(url, timeout=timeout) as r:
        for raw in r:
            line = raw.decode().rstrip("\n")
            if line.startswith("event:"):
                ev = line[6:].strip()
            elif line.startswith("data:"):
                out.append((ev, json.loads(line[5:].strip())))
                if ev == "status":
                    return out
    return out


def wait_done(base: str, jid: str, timeout: float = 600) -> dict:
    t0 = time.time()
    while time.time() - t0 < timeout:
        _, j = call(f"{base}/api/jobs/{jid}")
        if j["status"] not in ("queued", "running"):
            return j
        time.sleep(1)
    raise TimeoutError(jid)


def main() -> int:
    global PW
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:8080")
    ap.add_argument("--password", default=os.environ.get("OPENNEST_PASSWORD", ""))
    a = ap.parse_args()
    base = a.url.rstrip("/")
    PW = a.password
    pb = SESSION.read_bytes()
    small = json.loads((EXAMPLES / "small.json").read_text())
    long = json.loads(json.dumps(small))
    long["params"]["iter_budget"] = 50_000_000
    long["limits"] = {"wall_seconds": 600}
    octet = "application/octet-stream"

    print("1. password")
    st, _ = call(f"{base}/api/login", "POST", password="")
    check(st == 401, f"no password -> 401 ({st})")
    st, _ = call(f"{base}/api/login", "POST", password="wrong-password")
    check(st == 401, f"wrong password -> 401 ({st})")
    st, r = call(f"{base}/api/login", "POST")
    check(st == 200 and r["ok"] and r["latest_viewer"].startswith("https://"), "right password -> ok + latest viewer link")
    st, _ = call(f"{base}/api/jobs?spacing=10&margin=15", "POST", pb, password="", content_type=octet)
    check(st == 401, "submit without password -> 401")

    print("2. a session .pb job with live progress")
    st, rec = call(f"{base}/api/jobs?spacing=10&margin=15&fit_mode=0&wall=120&seed=3&name=example", "POST", pb, content_type=octet)
    check(st == 201 and rec["status"] == "queued" and rec["wall_cap"] == 120, f"submit -> 201 queued, cap from the query ({st}: {rec if st != 201 else ''})")
    s = rec.get("summary", {})
    check(s.get("parts") == 23 and s.get("sheets") == 3 and s.get("spacing") == 10 and s.get("margin") == 15, f"summary {s}")
    check(rec.get("warnings") == [] and rec["name"] == "example" and rec["fit_mode"] == 0, "no warnings, name and fit_mode taken")
    st, ipb = call(f"{base}/api/jobs/{rec['id']}/input.pb")
    check(st == 200 and ipb == pb, "input.pb is the uploaded file")
    st, y0 = call(rec["links"]["view_yaml"])
    check(st == 200 and (b"input.pb" in y0 or b"live.pb" in y0 or b"result.pb" in y0), "view.yaml exists from the start")
    events = sse(rec["links"]["events"])
    kinds = [e for e, _ in events]
    check("start" in kinds and "done" in kinds and kinds[-1] == "status", f"SSE start..done..status ({kinds[:3]}..{kinds[-2:]})")
    layouts = [d for e, d in events if e == "progress" and d.get("layout")]
    check(bool(layouts) and len(layouts[0]["layout"]) == 23, "progress events carry the live layout")
    final = events[-1][1]
    check(final["status"] == "ok", f"finished ok ({final['status']})")
    st, res = call(rec["links"]["result"])
    check(st == 200 and res["schema"] == "opennest.result/1" and res["quality"]["clean"], "result.json clean")
    check(res["clearance"] == {"spacing": 10, "margin": 15}, "spacing/margin applied by the worker")
    ids = {p["part"] for p in res["placements"]}
    check("rect 200x100" in ids and "frame 300" in ids and "rect 200x100 (2)" in ids, "part ids are the polyline names")
    st, y = call(rec["links"]["view_yaml"])
    check(st == 200 and b"result.pb" in y and b"live: true" in y, "view.yaml points at result.pb once finished")
    st, rpb = call(rec["links"]["result_pb"])
    check(st == 200 and isinstance(rpb, bytes) and len(rpb) > 1000, f"result.pb served ({len(rpb) if isinstance(rpb, bytes) else rpb} bytes)")
    st, lp = call(f"{base}/api/jobs/{rec['id']}/live.pb")
    check(st == 200 and isinstance(lp, bytes) and len(lp) > 100, "live.pb was written while the job ran")
    st, jj = call(rec["links"]["job"])
    check(st == 200 and jj["schema"] == "opennest.job/1" and len(jj["sheets"]) == 3 and len(jj["sheets"][0]["holes"]) == 1, "job.json: 3 sheets with a keep-out each")
    st, log = call(f"{base}/api/jobs/{rec['id']}/solver.log")
    check(st == 200, "solver.log served")
    st, j = call(f"{base}/api/jobs/{rec['id']}")
    check(st == 200 and len(j.get("sheets", [])) == j["n_sheets"] and j["has_preview"], "per-sheet utilisation + preview flag")
    st, ly = call(r["latest_view_yaml"])
    check(st == 200 and rec["id"].encode() in ly, "/live/<token>/view.yaml names the latest job")
    st, _ = call(f"{base}/live/0000000000000000000000000000dead/view.yaml")
    check(st == 404, "wrong view token -> 404")

    print("3. a JSON job still works")
    st, rec = call(f"{base}/api/jobs", "POST", small)
    check(st == 201 and rec["summary"]["parts"] == 23, "opennest.job/1 JSON accepted")
    j = wait_done(base, rec["id"])
    check(j["status"] in ("ok", "partial"), f"JSON job finished ({j['status']})")

    print("4. cancel a running job: best layout kept")
    st, rec = call(f"{base}/api/jobs", "POST", long)
    check(st == 201, "long job queued")
    time.sleep(4)
    st, c = call(f"{base}/api/jobs/{rec['id']}/cancel", "POST")
    check(st == 200 and c["status"] in ("cancelling", "cancelled"), f"cancel accepted ({c})")
    j = wait_done(base, rec["id"])
    check(j["status"] == "cancelled" and (j["n_sheets"] or 0) >= 1, f"cancelled with a layout ({j['status']}, {j['n_sheets']} sheet)")
    st, _ = call(f"{base}/api/jobs/{rec['id']}/cancel", "POST", password="")
    check(st == 401, "cancel without password -> 401")

    print("5. queue: a second job waits, cancelling it while queued closes it")
    st, r1 = call(f"{base}/api/jobs", "POST", long)
    st, r2 = call(f"{base}/api/jobs", "POST", long)
    time.sleep(2)
    _, j2 = call(f"{base}/api/jobs/{r2['id']}")
    if j2["status"] == "queued":
        call(f"{base}/api/jobs/{r2['id']}/cancel", "POST")
        _, j2 = call(f"{base}/api/jobs/{r2['id']}")
        check(j2["status"] == "cancelled" and j2["wall_s"] is None, "queued job cancelled without running")
    else:
        check(False, f"expected the second job to queue behind the first (got {j2['status']}; slots > 1?)")
    call(f"{base}/api/jobs/{r1['id']}/cancel", "POST")
    wait_done(base, r1["id"])
    wait_done(base, r2["id"])

    print("6. validation")
    st, e = call(f"{base}/api/jobs", "POST", b"not a session at all", content_type=octet)
    check(st == 400 and "session" in str(e), f"garbage body -> 400 ({st}: {e})")
    st, e = call(f"{base}/api/jobs", "POST", b"{not json", content_type="application/json")
    check(st == 400, f"invalid JSON -> 400 ({st})")
    st, e = call(f"{base}/api/jobs", "POST", {"schema": "x"})
    check(st == 400, f"wrong schema -> 400 ({st})")
    bad = json.loads(json.dumps(small)); bad["parts"][0]["qyt"] = 3
    st, rec = call(f"{base}/api/jobs", "POST", bad)
    j = wait_done(base, rec["id"]) if st == 201 else {}
    check(j.get("status") == "failed" and "qyt" in (j.get("error") or ""), f"worker rejects an unknown key ({j.get('status')}: {j.get('error')})")
    st, e = call(f"{base}/api/jobs/not-a-uuid")
    check(st == 404, "bad job id -> 404")
    st, rec = call(f"{base}/api/jobs?wall=1000000000", "POST", pb, content_type=octet)
    _, info = call(f"{base}/api/info")
    check(st == 201 and rec["wall_cap"] == info["max_wall_seconds"], f"wall cap clamped to the server maximum ({rec.get('wall_cap')})")
    call(f"{base}/api/jobs/{rec['id']}/cancel", "POST")
    wait_done(base, rec["id"])

    print("7. job list")
    st, lst = call(f"{base}/api/jobs")
    check(st == 200 and len(lst["jobs"]) >= 7 and all("links" in j for j in lst["jobs"]), f"list ({len(lst.get('jobs', []))} jobs)")
    st, _ = call(f"{base}/api/jobs", password="")
    check(st == 401, "list without password -> 401")

    print()
    print("PASS" if not FAILS else f"FAIL: {len(FAILS)} check(s)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())
