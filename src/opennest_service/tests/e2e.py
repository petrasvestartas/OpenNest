"""End-to-end test of a running opennest_service (standard library only).

    python -I src/opennest_service/tests/e2e.py --url http://localhost:8080 --password <OPENNEST_PASSWORD>

Covers: password check, a job with SSE progress (with live layouts), the result and viewer files, the
latest-result link, cancelling a running and a queued job, input validation, the job list.
Exit 0 when every check passes.
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
FAILS: list[str] = []
PW = ""


def check(cond: bool, what: str) -> None:
    print(("  ok    " if cond else "  FAIL  ") + what)
    if not cond:
        FAILS.append(what)


def call(url: str, method: str = "GET", body=None, password: str | None = None):
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
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
    small = json.loads((EXAMPLES / "small.json").read_text())
    clearance = json.loads((EXAMPLES / "clearance.json").read_text())
    long = json.loads(json.dumps(small))
    long["params"]["iter_budget"] = 50_000_000
    long["limits"] = {"wall_seconds": 600}

    print("1. password")
    st, _ = call(f"{base}/api/login", "POST", password="")
    check(st == 401, f"no password -> 401 ({st})")
    st, _ = call(f"{base}/api/login", "POST", password="wrong-password")
    check(st == 401, f"wrong password -> 401 ({st})")
    st, r = call(f"{base}/api/login", "POST")
    check(st == 200 and r["ok"] and r["latest_viewer"].startswith("https://"), "right password -> ok + latest viewer link")
    st, _ = call(f"{base}/api/jobs", "POST", small, password="")
    check(st == 401, "submit without password -> 401")

    print("2. a job with live progress")
    st, rec = call(f"{base}/api/jobs", "POST", clearance)
    check(st == 201 and rec["status"] == "queued" and rec["wall_cap"] == 120, f"submit -> 201 queued, cap from the job ({st}, {rec.get('wall_cap')})")
    events = sse(rec["links"]["events"])
    kinds = [e for e, _ in events]
    check("start" in kinds and "done" in kinds and kinds[-1] == "status", f"SSE start..done..status ({kinds[:3]}..{kinds[-2:]})")
    layouts = [d for e, d in events if e == "progress" and d.get("layout")]
    check(bool(layouts) and len(layouts[0]["layout"]) == 26, "progress events carry the live layout")
    final = events[-1][1]
    check(final["status"] == "ok", f"finished ok ({final['status']})")
    st, res = call(rec["links"]["result"])
    check(st == 200 and res["schema"] == "opennest.result/1" and res["quality"]["clean"], "result.json clean")
    check(res["clearance"] == {"spacing": 10, "margin": 15}, "spacing/margin applied by the worker")
    st, y = call(rec["links"]["view_yaml"])
    check(st == 200 and b"result.pb" in y and b"live: true" in y, "view.yaml served")
    st, pb = call(rec["links"]["result_pb"])
    check(st == 200 and isinstance(pb, bytes) and len(pb) > 1000, f"result.pb served ({len(pb) if isinstance(pb, bytes) else pb} bytes)")
    st, jj = call(rec["links"]["job"])
    check(st == 200 and jj["schema"] == "opennest.job/1", "job.json served")
    st, log = call(f"{base}/api/jobs/{rec['id']}/solver.log")
    check(st == 200, "solver.log served")
    st, j = call(f"{base}/api/jobs/{rec['id']}")
    check(st == 200 and len(j.get("sheets", [])) == j["n_sheets"] and j["has_preview"], "per-sheet utilisation + preview flag")
    st, ly = call(r["latest_view_yaml"])
    check(st == 200 and rec["id"].encode() in ly, "/live/<token>/view.yaml names the latest job")
    st, _ = call(f"{base}/live/0000000000000000000000000000dead/view.yaml")
    check(st == 404, "wrong view token -> 404")

    print("3. cancel a running job: best layout kept")
    st, rec = call(f"{base}/api/jobs", "POST", long)
    check(st == 201, "long job queued")
    time.sleep(4)
    st, c = call(f"{base}/api/jobs/{rec['id']}/cancel", "POST")
    check(st == 200 and c["status"] in ("cancelling", "cancelled"), f"cancel accepted ({c})")
    j = wait_done(base, rec["id"])
    check(j["status"] == "cancelled" and (j["n_sheets"] or 0) >= 1, f"cancelled with a layout ({j['status']}, {j['n_sheets']} sheet)")
    st, _ = call(f"{base}/api/jobs/{rec['id']}/cancel", "POST", password="")
    check(st == 401, "cancel without password -> 401")

    print("4. queue: a second job waits, cancelling it while queued closes it")
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

    print("5. validation")
    st, e = call(f"{base}/api/jobs", "POST", b"{not json")
    check(st == 400, f"invalid JSON -> 400 ({st})")
    st, e = call(f"{base}/api/jobs", "POST", {"schema": "x"})
    check(st == 400, f"wrong schema -> 400 ({st})")
    bad = json.loads(json.dumps(small)); bad["parts"][0]["qyt"] = 3
    st, rec = call(f"{base}/api/jobs", "POST", bad)
    j = wait_done(base, rec["id"]) if st == 201 else {}
    check(j.get("status") == "failed" and "qyt" in (j.get("error") or ""), f"worker rejects an unknown key ({j.get('status')}: {j.get('error')})")
    st, e = call(f"{base}/api/jobs/not-a-uuid")
    check(st == 404, "bad job id -> 404")
    huge = json.loads(json.dumps(small)); huge["limits"] = {"wall_seconds": 10 ** 9}
    st, rec = call(f"{base}/api/jobs", "POST", huge)
    _, info = call(f"{base}/api/info")
    check(st == 201 and rec["wall_cap"] == info["max_wall_seconds"], f"wall cap clamped to the server maximum ({rec.get('wall_cap')})")
    call(f"{base}/api/jobs/{rec['id']}/cancel", "POST")
    wait_done(base, rec["id"])

    print("6. job list")
    st, lst = call(f"{base}/api/jobs")
    check(st == 200 and len(lst["jobs"]) >= 6 and all("links" in j for j in lst["jobs"]), f"list ({len(lst.get('jobs', []))} jobs)")
    st, _ = call(f"{base}/api/jobs", password="")
    check(st == 401, "list without password -> 401")

    print()
    print("PASS" if not FAILS else f"FAIL: {len(FAILS)} check(s)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())
