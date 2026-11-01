"""End-to-end test of a running opennest_service (standard library only).

    python -I src/opennest_service/tests/e2e.py --url http://localhost:8080 --admin-token dev-admin

Covers: anonymous preview with SSE progress, signup, a billed job with reservation / charge / refund,
cancellation (running and queued), insufficient credits, input validation, the viewer files and the
account's latest-result link. Exit 0 when every check passes.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
EXAMPLES = HERE.parent.parent / "opennest_job" / "examples"
FAILS: list[str] = []


def check(cond: bool, what: str) -> None:
    print(("  ok    " if cond else "  FAIL  ") + what)
    if not cond:
        FAILS.append(what)


def call(url: str, method: str = "GET", body=None, key: str | None = None, admin: str | None = None):
    data = None if body is None else (body if isinstance(body, bytes) else json.dumps(body).encode())
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if key:
        req.add_header("Authorization", f"Bearer {key}")
    if admin:
        req.add_header("X-Admin-Token", admin)
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
    """Read Server-Sent Events until the final `status` event; returns [(event, data)]."""
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
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:8080")
    ap.add_argument("--admin-token", default="dev-admin")
    a = ap.parse_args()
    base = a.url.rstrip("/")
    small = json.loads((EXAMPLES / "small.json").read_text())
    small.pop("limits", None)          # let the tier cap decide the reservation
    clearance = json.loads((EXAMPLES / "clearance.json").read_text())

    print("1. anonymous free preview with live progress")
    st, rec = call(f"{base}/api/jobs?tier=preview", "POST", clearance)
    check(st == 201 and rec["status"] == "queued", f"submit preview -> 201 queued ({st})")
    events = sse(rec["links"]["events"])
    kinds = [e for e, _ in events]
    check("start" in kinds and "done" in kinds and kinds[-1] == "status", f"SSE start..done..status ({kinds[:3]}..{kinds[-2:]})")
    final = events[-1][1]
    check(final["status"] == "ok" and final["charged"] == 0, f"preview finished ok and free ({final['status']}, {final['charged']})")
    st, res = call(rec["links"]["result"])
    check(st == 200 and res["schema"] == "opennest.result/1" and res["quality"]["clean"], "result.json clean")
    check(res["clearance"] == {"spacing": 10, "margin": 15}, "spacing/margin applied by the worker")
    st, y = call(rec["links"]["view_yaml"])
    check(st == 200 and b"result.pb" in y and b"live: true" in y, "view.yaml served")
    st, pb = call(rec["links"]["result_pb"])
    check(st == 200 and isinstance(pb, bytes) and len(pb) > 1000, f"result.pb served ({len(pb) if isinstance(pb, bytes) else pb} bytes)")
    check(rec["links"]["viewer"].startswith("https://petrasvestartas.github.io/session/?live="), "viewer link")
    st, j = call(f"{base}/api/jobs/{rec['id']}")
    check(st == 200 and len(j.get("sheets", [])) == j["n_sheets"], "per-sheet utilisation in the job record")

    print("2. anonymous limits")
    st, e = call(f"{base}/api/jobs?tier=standard", "POST", small)
    check(st == 401, f"billed tier without a key -> 401 ({st})")

    print("3. signup and account")
    email = f"e2e-{int(time.time())}@example.com"
    st, s = call(f"{base}/api/signup", "POST", {"email": email})
    check(st == 200 and s["api_key"].startswith("onk_") and s["account"]["credits"] == 600, "signup gives a key and 600 credits")
    key = s["api_key"]
    st, _ = call(f"{base}/api/signup", "POST", {"email": email})
    check(st == 409, f"duplicate email -> 409 ({st})")
    st, _ = call(f"{base}/api/me", key="onk_wrong")
    check(st == 401, "unknown key -> 401")

    print("4. billed job: reserve the cap, charge the seconds used, refund the rest")
    st, rec = call(f"{base}/api/jobs?tier=standard", "POST", small, key=key)
    check(st == 201 and rec["reserved"] == 300, f"standard job queued with 300 reserved ({st})")
    _, me = call(f"{base}/api/me", key=key)
    bal = me["account"]["credits"]
    # 300 while the job is queued/running; already refunded if it finished first (it takes ~1 s)
    check(abs(bal - 300) < 1e-9 or 590 <= bal < 600, f"balance after reserving 300 = {bal}")
    j = wait_done(base, rec["id"])
    check(j["status"] in ("ok", "partial") and 1 <= j["charged"] <= 5, f"charged the seconds used ({j['charged']} for {j['wall_s']:.2f} s)")
    _, me = call(f"{base}/api/me", key=key)
    check(abs(me["account"]["credits"] - (600 - j["charged"])) < 1e-9, f"refund leaves {me['account']['credits']}")
    check(me["account"]["latest_job"] == rec["id"], "latest_job updated")
    st, ly = call(me["latest_view_yaml"])
    check(st == 200 and rec["id"].encode() in ly, "/live/<token>/view.yaml names the latest job")

    print("5. cancel a running job: best layout kept, charged only the seconds used")
    long = json.loads(json.dumps(small))
    long["params"]["iter_budget"] = 50_000_000
    st, rec = call(f"{base}/api/jobs?tier=standard", "POST", long, key=key)
    check(st == 201, "long job queued")
    time.sleep(4)
    st, c = call(f"{base}/api/jobs/{rec['id']}/cancel", "POST", key=key)
    check(st == 200 and c["status"] in ("cancelling", "cancelled"), f"cancel accepted ({c})")
    j = wait_done(base, rec["id"])
    check(j["status"] == "cancelled" and j["charged"] <= 10 and j["n_sheets"] >= 1, f"cancelled with a layout ({j['status']}, charged {j['charged']}, {j['n_sheets']} sheet)")

    print("6. insufficient credits")
    st, e = call(f"{base}/api/jobs?tier=deep", "POST", small, key=key)
    check(st == 402, f"deep reserves 1800 > balance -> 402 ({st}: {e.get('error') if isinstance(e, dict) else e})")

    print("7. validation")
    st, e = call(f"{base}/api/jobs?tier=preview", "POST", b"{not json")
    check(st == 400, f"invalid JSON -> 400 ({st})")
    st, e = call(f"{base}/api/jobs?tier=preview", "POST", {"schema": "x"})
    check(st == 400, f"wrong schema -> 400 ({st})")
    bad = json.loads(json.dumps(small)); bad["parts"][0]["qyt"] = 3
    st, rec = call(f"{base}/api/jobs?tier=standard", "POST", bad, key=key)
    j = wait_done(base, rec["id"]) if st == 201 else {}
    check(j.get("status") == "failed" and "qyt" in (j.get("error") or "") and j.get("charged") == 0,
          f"worker rejects an unknown key, nothing charged ({j.get('status')}: {j.get('error')})")
    st, e = call(f"{base}/api/jobs?tier=nope", "POST", small, key=key)
    check(st == 400, "unknown tier -> 400")
    st, e = call(f"{base}/api/jobs/not-a-uuid")
    check(st == 404, "bad job id -> 404")

    print("8. admin")
    st, e = call(f"{base}/api/admin/jobs", admin="wrong")
    check(st == 401, "wrong admin token -> 401")
    st, acc = call(f"{base}/api/admin/accounts", "POST", {"email": f"ops-{int(time.time())}@example.com", "credits": 3600}, admin=a.admin_token)
    check(st == 200 and acc["account"]["credits"] == 3600, "admin creates an account with credits")
    st, jl = call(f"{base}/api/admin/jobs", admin=a.admin_token)
    check(st == 200 and len(jl["jobs"]) >= 4, f"admin job list ({len(jl.get('jobs', []))})")

    print("9. queued-job cancel refunds in full")
    k2 = acc["api_key"]
    recs = []
    for _ in range(3):
        st, r = call(f"{base}/api/jobs?tier=standard", "POST", long, key=k2)
        recs.append(r)
    _, before = call(f"{base}/api/me", key=k2)
    queued = [r for r in recs if call(f"{base}/api/jobs/{r['id']}")[1]["status"] == "queued"]
    if queued:
        call(f"{base}/api/jobs/{queued[-1]['id']}/cancel", "POST", key=k2)
        _, j = call(f"{base}/api/jobs/{queued[-1]['id']}")
        _, after = call(f"{base}/api/me", key=k2)
        check(j["status"] == "cancelled" and abs(after["account"]["credits"] - before["account"]["credits"] - 300) < 1e-9,
              "queued job cancelled and its 300 credits returned")
    else:
        check(False, "expected a queued job behind the 2 running slots")
    for r in recs:
        call(f"{base}/api/jobs/{r['id']}/cancel", "POST", key=k2)
    for r in recs:
        wait_done(base, r["id"])

    print()
    print("PASS" if not FAILS else f"FAIL: {len(FAILS)} check(s)")
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())
