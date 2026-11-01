# opennest_service

The OpenNest web service. It accepts nesting jobs over HTTP, runs each one with the
[`opennest_job`](../opennest_job/README.md) worker, charges accounts in credits, and publishes every
result three ways: JSON for programs, a drawing on the built-in web page, and a
[session_viewer](https://petrasvestartas.github.io/session/) scene (`.pb` written with `session_rust`,
the viewer's own kernel).

```
browser page / script / session_viewer
        │ HTTPS (Caddy)
        ▼
opennest_service (Rust, axum)   SQLite: accounts, jobs, credit ledger
        │ one process per job, clean environment, wall cap, cancel file
        ▼
opennest_job (C++, static)  ->  result.json + events.ndjson
        │
        └─ result.pb + view.yaml for session_viewer
```

The compute runs on the server that hosts this service: a Linux machine you rent (see Deploy).

## Run locally

```sh
# 1. the worker (see ../opennest_job/README.md)
cmake -S src/opennest_job -B build/opennest_job -DCMAKE_BUILD_TYPE=Release -DOPENNEST_JOB_STATIC=ON
cmake --build build/opennest_job -j

# 2. the service
cd src/opennest_service
cargo build --release
OPENNEST_JOB_BIN=../../build/opennest_job/opennest_job OPENNEST_ADMIN_TOKEN=dev ./target/release/opennest_service
```

Open <http://localhost:8080>: load a DXF or SVG, set the sheet, press **Nest**.

## Configuration (environment)

| Variable | Default | Meaning |
| --- | --- | --- |
| `OPENNEST_BIND` | `127.0.0.1:8080` | listen address |
| `OPENNEST_DATA` | `data` | SQLite database and one folder per job |
| `OPENNEST_JOB_BIN` | `opennest_job` next to the service | the worker binary |
| `OPENNEST_WEB` | `web` | the static web page |
| `OPENNEST_SLOTS` | 1 | jobs that run at the same time; the rest queue |
| `OPENNEST_THREADS_PER_JOB` | 75% of cores / slots | solver threads per job |
| `OPENNEST_PUBLIC_URL` | `http://<bind>` | how browsers reach the service (used in links) |
| `OPENNEST_VIEWER_URL` | `https://petrasvestartas.github.io/session/` | session_viewer page |
| `OPENNEST_ADMIN_TOKEN` | unset (admin API off) | `X-Admin-Token` for `/api/admin/*` |
| `OPENNEST_SIGNUP_CREDITS` | 600 | free credits for a self-service signup |
| `OPENNEST_ANON_PREVIEW` | 1 | free 30 s previews without an account |
| `OPENNEST_TRUST_PROXY` | 0 | read the client IP from `X-Forwarded-For` (set behind Caddy) |
| `OPENNEST_PADDLE_SECRET` / `_CLIENT_TOKEN` / `_PRICES` | unset | Paddle Billing, see Payments |
| `OPENNEST_MAX_BODY_MB` | 32 | largest accepted job |

## Tiers and credits

1 credit = 1 second of wall time on one worker slot (`OPENNEST_THREADS_PER_JOB` threads).

| Tier | Wall cap | Cost | Default solver mode |
| --- | --- | --- | --- |
| `preview` | 30 s | free, rate limited | fit_mode 0 (fewest sheets) |
| `standard` | 5 min | seconds used | fit_mode 0 |
| `deep` | 30 min | seconds used | fit_mode 3 (group-and-fill, best sheet count) |

Submitting reserves the tier's whole cap; when the job ends it is charged the seconds it ran and the
rest is refunded. A job cancelled while still queued is refunded in full. The service also forces
`params.n_threads` and the wall cap; everything else in the job is the customer's.

## API

Authentication: `Authorization: Bearer onk_...`. A job id is an unguessable UUID and works as a
read-only link: status, progress, result and viewer files need no key, so a viewer link can be shared.

| Method | Path | |
| --- | --- | --- |
| GET | `/api/info` | tiers, limits, payment setup |
| POST | `/api/signup` `{"email"}` | create an account; returns the API key once |
| GET | `/api/me` | balance and the account's "latest result" viewer link |
| POST | `/api/jobs?tier=standard` | body: an `opennest.job/1` document (top-level `spacing`, `margin`) |
| GET | `/api/jobs` | your last 50 jobs |
| GET | `/api/jobs/{id}` | status, queue position, links, per-sheet utilisation |
| GET | `/api/jobs/{id}/events` | Server-Sent Events: `start`, `progress`, `cap`, `cancel`, `done`, final `status` |
| GET | `/api/jobs/{id}/result` | `opennest.result/1` |
| POST | `/api/jobs/{id}/cancel` | stop; the best layout so far is kept |
| GET | `/api/jobs/{id}/view.yaml`, `/result.pb` | session_viewer scene of the job |
| GET | `/live/{view_token}/view.yaml` | the account's latest finished result (stable link) |
| POST | `/api/admin/accounts` `{"email","credits"}` | create an account or issue a new key |
| POST | `/api/admin/accounts/{id}/credits` `{"delta","reason"}` | top up or correct a balance |
| GET | `/api/admin/jobs` | the last 200 jobs |
| POST | `/api/webhooks/paddle` | Paddle Billing webhook |

```sh
curl -X POST "$URL/api/jobs?tier=standard" -H "Authorization: Bearer $KEY" \
     -H "Content-Type: application/json" --data @job.json
curl -N "$URL/api/jobs/$ID/events"
curl "$URL/api/jobs/$ID/result" > result.json
```

## session_viewer preview

Each finished job gets `result.pb` (sheets in a row along +x, millimetres, one group per sheet with
its outline, keep-outs and parts) and `view.yaml`. The viewer opens it with its `?live=` mode, which
polls the scene, so a tab left on an account's `/live/{token}/view.yaml` link shows each new result as
it finishes:

```
https://petrasvestartas.github.io/session/?live=<url-encoded view.yaml URL>&poll=2
```

The web page's **Open in session viewer** button and `GET /api/me` give these links ready-made.

## Deploy (one Linux server)

1. Rent a Linux server with dedicated CPU cores (for example a Hetzner CCX instance) and install
   Docker. Point a DNS A record (e.g. `nest.example.com`) at it.
2. On the server:
   ```sh
   git clone https://github.com/petrasvestartas/OpenNest.git && cd OpenNest/src/opennest_service/deploy
   cp .env.example .env    # set OPENNEST_DOMAIN, OPENNEST_ADMIN_TOKEN, slots/threads
   docker compose up -d --build
   ```
   Caddy obtains the HTTPS certificate on first start.
3. Create your own account: `curl -X POST https://nest.example.com/api/admin/accounts -H "X-Admin-Token: ..." -H "Content-Type: application/json" -d '{"email":"you@example.com","credits":36000}'`.
4. Back up the `opennest-data` volume (the SQLite file holds balances).

Size `OPENNEST_SLOTS x OPENNEST_THREADS_PER_JOB` to about 75% of the cores; queued jobs wait for a free slot.
Packing quality grows with the threads a job gets (the solver explores that many layouts in parallel):
measured on the small example, 4 threads left a part unplaced that ~24 threads placed. More slots serve
more customers at once; fewer, wider slots give each customer a better nest.

## Payments (Paddle Billing)

1. Create a Paddle account (it acts as merchant of record and handles VAT), products and one-time
   prices for credit packs.
2. Set `OPENNEST_PADDLE_PRICES=pri_...=3600,pri_...=36000` (credit-seconds per pack),
   `OPENNEST_PADDLE_CLIENT_TOKEN` (client-side token) and `OPENNEST_PADDLE_SECRET` (the webhook
   secret), and register the webhook `https://<domain>/api/webhooks/paddle` for
   `transaction.completed`.
3. The web page then shows "Buy" buttons; the checkout passes the account id, and the webhook adds
   the credits once per event.

The webhook handler is unit-tested for the signature check but has not yet been run against
Paddle: test it end to end in Paddle's sandbox before taking real payments.

## Limits and safety

* Up to 5000 part copies, 2 000 000 vertices (copies counted) and 500 sheets per job; 32 MB body.
* At most 3 queued or running jobs per account, 1 per IP for anonymous previews.
* The worker runs with an empty environment, so customers cannot pass solver developer switches.
* Signup has no email verification yet: free credits are the abuse surface, so keep
  `OPENNEST_SIGNUP_CREDITS` small or set it to 0 and create accounts by hand.
