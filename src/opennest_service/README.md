# opennest_service

The OpenNest web service, single-user edition: one password, a job queue that runs the
[`opennest_job`](../opennest_job/README.md) worker, live progress, and every result as JSON, as a
drawing on the built-in page, and as a [session_viewer](https://petrasvestartas.github.io/session/)
scene (`.pb` written with `session_rust`, the viewer's own kernel).

```
browser page / script / session_viewer
        │ HTTPS (Caddy)
        ▼
opennest_service (Rust, axum)   SQLite: the job table
        │ one process per job, clean environment, wall cap, cancel file
        ▼
opennest_job (C++, static)  ->  result.json + events.ndjson (with live layouts)
        │
        └─ result.pb + view.yaml for session_viewer
```

## Run locally

```sh
cmake -S src/opennest_job -B build/opennest_job -DCMAKE_BUILD_TYPE=Release -DOPENNEST_JOB_STATIC=ON
cmake --build build/opennest_job -j
cd src/opennest_service && cargo build --release
OPENNEST_PASSWORD=choose-something-long OPENNEST_JOB_BIN=../../build/opennest_job/opennest_job ./target/release/opennest_service
```

Open <http://localhost:8080>, enter the password, choose a session `.pb`, set spacing / margin, a
strategy and a time budget, press **Nest**. The uploaded session, the layout while the solver runs
and the final nest are shown in session_viewer.

## Input: a session file

The job is one serialized Session (`session_proto.Session`, the `.pb` that session_py / session_rust /
session_cpp write and session_viewer reads):

* every closed polyline is an outline; polylines under a tree group named **`sheets`** are the sheets,
  all others are parts;
* a closed polyline inside another one is its hole (part hole / sheet keep-out), by containment;
* one polyline = one copy; draw a part twice to nest two; the part id is the polyline's name;
* the session's transforms are applied; z is dropped;
* optional settings can travel in the file as a `Component` named `opennest` whose `json_data` holds
  `{"spacing": 10, "margin": 15, "params": {...}, "limits": {...}}`; the page's fields override them.

`opennest_service --example-session out.pb` writes a reference file (`examples/clearance_session.pb`).
The result is `result.pb`, a session with one group per used sheet (outline, keep-outs, placed parts).

## Configuration (environment)

| Variable | Default | Meaning |
| --- | --- | --- |
| `OPENNEST_PASSWORD` | required | the one secret; at least 8 characters |
| `OPENNEST_BIND` | `127.0.0.1:8080` | listen address |
| `OPENNEST_DATA` | `data` | SQLite database and one folder per job |
| `OPENNEST_JOB_BIN` | `opennest_job` next to the service | the worker binary |
| `OPENNEST_WEB` | `web` | the static web page |
| `OPENNEST_SLOTS` | 1 | jobs that run at the same time; the rest queue |
| `OPENNEST_THREADS_PER_JOB` | 75% of cores / slots | solver threads per job |
| `OPENNEST_MAX_WALL` | 3600 | longest run a job may ask for, seconds |
| `OPENNEST_DEFAULT_WALL` | 600 | used when a job gives no `limits.wall_seconds` |
| `OPENNEST_PUBLIC_URL` | `http://<bind>` | how browsers reach the service (used in links) |
| `OPENNEST_VIEWER_URL` | `https://petrasvestartas.github.io/session/` | session_viewer page |
| `OPENNEST_TRUST_PROXY` | 0 | read the client IP from `X-Forwarded-For` (set behind Caddy) |
| `OPENNEST_MAX_BODY_MB` | 64 | largest accepted job |

Packing quality grows with the threads a job gets (the solver explores that many layouts in
parallel), so keep `OPENNEST_SLOTS` at 1 unless you need to run jobs side by side.

## API

Writes need the password: `Authorization: Bearer <password>` (or `X-Password`). A job id is an
unguessable UUID and works as a read-only link, so a result or viewer link can be shared.

| Method | Path | |
| --- | --- | --- |
| GET | `/api/info` | limits and settings (public) |
| POST | `/api/login` | checks the password; returns the latest-result viewer link |
| POST | `/api/jobs?spacing=&margin=&rotations=&fit_mode=&wall=&seed=&name=` | body: a session `.pb` (settings from the query), or an `opennest.job/1` JSON document (`Content-Type: application/json`) |
| GET | `/api/jobs` | the last 100 jobs |
| GET | `/api/jobs/{id}` | status, queue position, links, per-sheet utilisation |
| GET | `/api/jobs/{id}/events` | Server-Sent Events: `start`, `progress` (with `layout`), `cap`, `cancel`, `done`, final `status` |
| GET | `/api/jobs/{id}/result` | `opennest.result/1` |
| GET | `/api/jobs/{id}/input.pb`, `/job.json`, `/solver.log`, `/view.yaml`, `/live.pb`, `/result.pb` | the job's files |
| POST | `/api/jobs/{id}/cancel` | stop; the best layout so far is kept |
| GET | `/live/{view_token}/view.yaml` | the latest finished result (stable link; token derived from the password) |

```sh
curl -X POST "$URL/api/jobs?spacing=10&margin=15&fit_mode=3&wall=900" -H "Authorization: Bearer $PW" --data-binary @parts.pb
curl -X POST "$URL/api/jobs" -H "Authorization: Bearer $PW" -H "Content-Type: application/json" --data @job.json
curl -N "$URL/api/jobs/$ID/events"
curl "$URL/api/jobs/$ID/result" > result.json
```

## session_viewer preview

The page shows the job's scene in a session_viewer frame: its `view.yaml` points at the uploaded
`input.pb` while queued, at `live.pb` (rewritten every 3 s from the solver's in-progress layout)
while it runs and at `result.pb` (sheets in a row along +x, millimetres, one group per sheet with its
outline, keep-outs and parts) when it is done. The viewer opens it with its `?live=` mode, which
polls the scene, so a tab left on the latest-result link shows each new result as it finishes:

```
https://petrasvestartas.github.io/session/?live=<url-encoded view.yaml URL>&poll=2
```

## Deploy (one Linux server)

```sh
git clone https://github.com/petrasvestartas/OpenNest.git && cd OpenNest/src/opennest_service/deploy
cp .env.example .env     # set OPENNEST_DOMAIN and OPENNEST_PASSWORD
docker compose up -d --build
```

Caddy gets the HTTPS certificate for `OPENNEST_DOMAIN` on first start (an `sslip.io` name works
without owning a domain). Update with `git pull` and the same `docker compose up -d --build`. The
SQLite file and the job folders live in the `opennest-data` volume.

## Tests

```sh
python -I src/opennest_service/tests/e2e.py --url http://localhost:8080 --password ...
cargo test --release      # session import of the example, preview .pb round-trip through session_rust
```
