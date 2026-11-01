//! HTTP API. Authentication is a bearer API key (`Authorization: Bearer onk_...`). A job id is a random
//! UUID and works as a read-only capability: status, progress, result and the viewer files of a job can
//! be fetched with the id alone, so a viewer link can be shared. Writes (cancel) need the owner's key.

use crate::config::{self, TIERS};
use crate::db::{self, Account, Job};
use crate::worker::job_dir;
use crate::AppState;
use axum::body::Bytes;
use axum::extract::{ConnectInfo, Path, Query, State};
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::Json;
use base64::Engine;
use futures_util::stream::Stream;
use rand::RngCore;
use serde::Deserialize;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::{Duration, Instant};

pub struct ApiError(pub StatusCode, pub String);

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        (self.0, Json(json!({"error": self.1}))).into_response()
    }
}

impl From<rusqlite::Error> for ApiError {
    fn from(e: rusqlite::Error) -> Self {
        tracing::error!("db: {e}");
        ApiError(StatusCode::INTERNAL_SERVER_ERROR, "database error".into())
    }
}

type ApiResult<T> = Result<T, ApiError>;

fn bad(msg: impl Into<String>) -> ApiError {
    ApiError(StatusCode::BAD_REQUEST, msg.into())
}

pub fn hash_key(key: &str) -> String {
    hex::encode(Sha256::digest(key.as_bytes()))
}

fn random_token(bytes: usize) -> String {
    let mut b = vec![0u8; bytes];
    rand::thread_rng().fill_bytes(&mut b);
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(b)
}

fn new_api_key() -> String {
    format!("onk_{}", random_token(32))
}

pub fn client_ip(state: &AppState, headers: &HeaderMap, addr: &SocketAddr) -> String {
    if state.cfg.trust_proxy {
        if let Some(v) = headers.get("x-forwarded-for").and_then(|v| v.to_str().ok()) {
            if let Some(first) = v.split(',').next() {
                return first.trim().to_string();
            }
        }
    }
    addr.ip().to_string()
}

async fn account(state: &AppState, headers: &HeaderMap) -> ApiResult<Option<Account>> {
    let Some(v) = headers.get(header::AUTHORIZATION).and_then(|v| v.to_str().ok()) else { return Ok(None) };
    let key = v.strip_prefix("Bearer ").unwrap_or(v).trim();
    if key.is_empty() {
        return Ok(None);
    }
    let c = state.db.lock().await;
    match db::account_by_key_hash(&c, &hash_key(key))? {
        Some(a) => Ok(Some(a)),
        None => Err(ApiError(StatusCode::UNAUTHORIZED, "unknown API key".into())),
    }
}

async fn require_account(state: &AppState, headers: &HeaderMap) -> ApiResult<Account> {
    account(state, headers).await?.ok_or_else(|| ApiError(StatusCode::UNAUTHORIZED, "an API key is required".into()))
}

/// Sliding-window limiter: at most `max` hits per `window` for a key.
pub async fn rate_ok(state: &AppState, key: String, max: usize, window: Duration) -> bool {
    let mut m = state.limiter.lock().await;
    let now = Instant::now();
    let hits = m.entry(key).or_default();
    hits.retain(|t| now.duration_since(*t) < window);
    if hits.len() >= max {
        return false;
    }
    hits.push(now);
    true
}

fn viewer_link(state: &AppState, manifest_url: &str) -> String {
    let enc: String = manifest_url
        .bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => (b as char).to_string(),
            _ => format!("%{b:02X}"),
        })
        .collect();
    format!("{}?live={}&poll=2", state.cfg.viewer_url, enc)
}

fn job_json(state: &AppState, j: &Job, queue_position: Option<i64>) -> Value {
    let base = format!("{}/api/jobs/{}", state.cfg.public_url, j.id);
    let mut v = serde_json::to_value(j).unwrap_or_default();
    v["queue_position"] = json!(queue_position);
    v["links"] = json!({
        "self": base,
        "events": format!("{base}/events"),
        "result": format!("{base}/result"),
        "view_yaml": format!("{base}/view.yaml"),
        "result_pb": format!("{base}/result.pb"),
        "viewer": viewer_link(state, &format!("{base}/view.yaml")),
    });
    v
}

// ---------------------------------------------------------------------------------------------------
// public info
// ---------------------------------------------------------------------------------------------------
pub async fn health() -> &'static str {
    "ok"
}

pub async fn info(State(state): State<Arc<AppState>>) -> Json<Value> {
    Json(json!({
        "service": "opennest",
        "version": env!("CARGO_PKG_VERSION"),
        "credit_unit": "1 credit = 1 second of wall time on one worker slot",
        "threads_per_job": state.cfg.threads_per_job,
        "slots": state.cfg.slots,
        "anonymous_preview": state.cfg.anonymous_preview,
        "tiers": TIERS.iter().map(|t| json!({
            "name": t.name, "wall_seconds": t.wall_seconds, "billed": t.billed,
            "default_fit_mode": t.default_fit_mode, "max_part_copies": t.max_part_copies,
            "description": t.description,
        })).collect::<Vec<_>>(),
        "paddle": state.cfg.paddle_client_token.as_ref().map(|t| json!({
            "client_token": t,
            "prices": state.cfg.paddle_prices.iter().map(|(id, s)| json!({"id": id, "credits": s})).collect::<Vec<_>>(),
        })),
        "viewer_url": state.cfg.viewer_url,
    }))
}

// ---------------------------------------------------------------------------------------------------
// accounts
// ---------------------------------------------------------------------------------------------------
#[derive(Deserialize)]
pub struct SignupBody {
    email: String,
}

fn valid_email(e: &str) -> bool {
    let e = e.trim();
    e.len() <= 254 && e.contains('@') && !e.starts_with('@') && !e.ends_with('@') && !e.contains(char::is_whitespace)
}

/// Self-service signup: returns the API key ONCE (only its hash is stored). Rate limited per IP.
pub async fn signup(
    State(state): State<Arc<AppState>>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    Json(body): Json<SignupBody>,
) -> ApiResult<Json<Value>> {
    let email = body.email.trim().to_lowercase();
    if !valid_email(&email) {
        return Err(bad("a valid email address is required"));
    }
    let ip = client_ip(&state, &headers, &addr);
    if !rate_ok(&state, format!("signup:{ip}"), 5, Duration::from_secs(3600)).await {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "too many signups from this address; try later".into()));
    }
    let key = new_api_key();
    let c = state.db.lock().await;
    if db::account_by_email(&c, &email)?.is_some() {
        return Err(ApiError(StatusCode::CONFLICT, "an account with this email exists; ask the operator for a new key".into()));
    }
    let id = uuid::Uuid::new_v4().to_string();
    db::create_account(&c, &id, &email, &hash_key(&key), &random_token(16), state.cfg.signup_credits)?;
    let a = db::account_by_id(&c, &id)?.expect("just inserted");
    Ok(Json(json!({"api_key": key, "account": a, "note": "store this key: it is shown only once"})))
}

pub async fn me(State(state): State<Arc<AppState>>, headers: HeaderMap) -> ApiResult<Json<Value>> {
    let a = require_account(&state, &headers).await?;
    let latest = format!("{}/live/{}/view.yaml", state.cfg.public_url, a.view_token);
    Ok(Json(json!({
        "account": a,
        "latest_view_yaml": latest,
        "latest_viewer": viewer_link(&state, &latest),
    })))
}

// ---------------------------------------------------------------------------------------------------
// jobs
// ---------------------------------------------------------------------------------------------------
#[derive(Deserialize)]
pub struct SubmitQuery {
    tier: Option<String>,
}

const MAX_VERTICES: usize = 2_000_000;
const MAX_SHEET_COPIES: u64 = 500;

pub async fn submit(
    State(state): State<Arc<AppState>>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    Query(q): Query<SubmitQuery>,
    body: Bytes,
) -> ApiResult<Response> {
    let acc = account(&state, &headers).await?;
    let ip = client_ip(&state, &headers, &addr);
    let tier_name = q.tier.unwrap_or_else(|| if acc.is_some() { "standard".into() } else { "preview".into() });
    let tier = config::tier(&tier_name).ok_or_else(|| bad(format!("unknown tier '{tier_name}'")))?;
    if acc.is_none() && (tier.billed || !state.cfg.anonymous_preview) {
        return Err(ApiError(StatusCode::UNAUTHORIZED, "an API key is required for this tier".into()));
    }

    let mut job: Value = serde_json::from_slice(&body).map_err(|e| bad(format!("invalid JSON: {e}")))?;
    if job["schema"] != "opennest.job/1" {
        return Err(bad("job.schema must be \"opennest.job/1\""));
    }
    let parts = job["parts"].as_array().ok_or_else(|| bad("job.parts must be an array"))?;
    let sheets = job["sheets"].as_array().ok_or_else(|| bad("job.sheets must be an array"))?;
    let mut copies = 0usize;
    let mut verts = 0usize;
    for p in parts {
        let qty = p.get("qty").and_then(|v| v.as_u64()).unwrap_or(1) as usize;
        copies += qty;
        let ring_len = |r: &Value| r.as_array().map(|a| a.len()).unwrap_or(0);
        let n = ring_len(&p["outer"]) + p["holes"].as_array().map(|h| h.iter().map(ring_len).sum()).unwrap_or(0);
        verts = verts.saturating_add(n.saturating_mul(qty.max(1)));
    }
    let sheet_copies: u64 = sheets.iter().map(|s| s.get("count").and_then(|v| v.as_u64()).unwrap_or(1)).sum();
    if copies == 0 {
        return Err(bad("the job has no parts"));
    }
    if copies > tier.max_part_copies {
        return Err(bad(format!("{copies} part copies exceed the {} tier limit of {}", tier.name, tier.max_part_copies)));
    }
    if verts > MAX_VERTICES {
        return Err(bad(format!("{verts} vertices (counting copies) exceed the limit of {MAX_VERTICES}; simplify the outlines")));
    }
    if sheet_copies == 0 || sheet_copies > MAX_SHEET_COPIES {
        return Err(bad(format!("between 1 and {MAX_SHEET_COPIES} sheets are allowed")));
    }

    // Server-owned settings: threads, the wall cap and a default solver mode per tier.
    if !job["params"].is_object() {
        job["params"] = json!({});
    }
    job["params"]["n_threads"] = json!(state.cfg.threads_per_job);
    if job["params"].get("fit_mode").is_none() {
        job["params"]["fit_mode"] = json!(tier.default_fit_mode);
    }
    let asked = job["limits"]["wall_seconds"].as_f64().filter(|w| *w > 0.0).unwrap_or(tier.wall_seconds);
    let wall_cap = asked.min(tier.wall_seconds);
    job["limits"] = json!({"wall_seconds": wall_cap});

    {
        let c = state.db.lock().await;
        let active = db::count_active(&c, acc.as_ref().map(|a| a.id.as_str()), Some(&ip))?;
        let max_active = if acc.is_some() { 3 } else { 1 };
        if active >= max_active {
            return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, format!("at most {max_active} queued or running job(s) at a time")));
        }
    }
    if acc.is_none() && !rate_ok(&state, format!("anon:{ip}"), 20, Duration::from_secs(3600)).await {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "free preview limit reached for this hour".into()));
    }

    let id = uuid::Uuid::new_v4().to_string();
    let dir = job_dir(&state.cfg.data_dir, &id);
    tokio::fs::create_dir_all(&dir).await.map_err(|e| ApiError(StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    tokio::fs::write(dir.join("job.json"), serde_json::to_vec(&job).unwrap_or_default())
        .await
        .map_err(|e| ApiError(StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    let reserved = if tier.billed { wall_cap.ceil() } else { 0.0 };
    let j = Job {
        id: id.clone(),
        account_id: acc.as_ref().map(|a| a.id.clone()),
        ip,
        tier: tier.name.to_string(),
        status: "queued".into(),
        created: db::now(),
        started: None,
        finished: None,
        wall_cap,
        reserved,
        charged: 0.0,
        part_copies: copies as i64,
        n_sheets: None,
        unplaced: None,
        wall_s: None,
        error: None,
        cancel_requested: false,
    };
    let (inserted, pos) = {
        let c = state.db.lock().await;
        let ok = db::insert_job(&c, &j)?;
        (ok, if ok { Some(db::queue_position(&c, j.created)?) } else { None })
    };
    if !inserted {
        let _ = tokio::fs::remove_dir_all(&dir).await;
        let have = acc.map(|a| a.credits).unwrap_or(0.0);
        return Err(ApiError(
            StatusCode::PAYMENT_REQUIRED,
            format!("the {} tier reserves {reserved:.0} credits; the account has {have:.0}", tier.name),
        ));
    }
    state.wake.notify_one();
    Ok((StatusCode::CREATED, Json(job_json(&state, &j, pos))).into_response())
}

async fn load_job(state: &AppState, id: &str) -> ApiResult<Job> {
    if uuid::Uuid::parse_str(id).is_err() {
        return Err(ApiError(StatusCode::NOT_FOUND, "no such job".into()));
    }
    let c = state.db.lock().await;
    db::job(&c, id)?.ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "no such job".into()))
}

pub async fn get_job(State(state): State<Arc<AppState>>, Path(id): Path<String>) -> ApiResult<Json<Value>> {
    let j = load_job(&state, &id).await?;
    let pos = if j.status == "queued" {
        let c = state.db.lock().await;
        Some(db::queue_position(&c, j.created)?)
    } else {
        None
    };
    let mut v = job_json(&state, &j, pos);
    if let Ok(b) = tokio::fs::read(job_dir(&state.cfg.data_dir, &id).join("preview.json")).await {
        if let Ok(p) = serde_json::from_slice::<Value>(&b) {
            v["sheets"] = p["sheets"].clone();
        }
    }
    Ok(Json(v))
}

pub async fn list_jobs(State(state): State<Arc<AppState>>, headers: HeaderMap) -> ApiResult<Json<Value>> {
    let a = require_account(&state, &headers).await?;
    let c = state.db.lock().await;
    let jobs = db::jobs_of(&c, &a.id, 50)?;
    Ok(Json(json!({"jobs": jobs.iter().map(|j| job_json(&state, j, None)).collect::<Vec<_>>()})))
}

pub async fn cancel(State(state): State<Arc<AppState>>, headers: HeaderMap, Path(id): Path<String>) -> ApiResult<Json<Value>> {
    let j = load_job(&state, &id).await?;
    let owner_ok = match (&j.account_id, account(&state, &headers).await?) {
        (Some(owner), Some(a)) => *owner == a.id,
        (None, _) => true, // anonymous preview: the id is the capability
        _ => false,
    };
    if !owner_ok {
        return Err(ApiError(StatusCode::FORBIDDEN, "not your job".into()));
    }
    let c = state.db.lock().await;
    if db::cancel_queued(&c, &id)? {
        return Ok(Json(json!({"status": "cancelled"})));
    }
    db::request_cancel(&c, &id)?;
    Ok(Json(json!({"status": "cancelling"})))
}

async fn file_response(path: std::path::PathBuf, content_type: &'static str) -> ApiResult<Response> {
    match tokio::fs::read(&path).await {
        Ok(b) => {
            let mut r = b.into_response();
            r.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static(content_type));
            r.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
            Ok(r)
        }
        Err(_) => Err(ApiError(StatusCode::NOT_FOUND, "not available (yet)".into())),
    }
}

pub async fn result(State(state): State<Arc<AppState>>, Path(id): Path<String>) -> ApiResult<Response> {
    load_job(&state, &id).await?;
    file_response(job_dir(&state.cfg.data_dir, &id).join("result.json"), "application/json").await
}

pub async fn job_file(State(state): State<Arc<AppState>>, Path((id, file)): Path<(String, String)>) -> ApiResult<Response> {
    load_job(&state, &id).await?;
    let (name, ct) = match file.as_str() {
        "view.yaml" => ("view.yaml", "application/yaml"),
        "result.pb" => ("result.pb", "application/octet-stream"),
        "job.json" => ("job.json", "application/json"),
        _ => return Err(ApiError(StatusCode::NOT_FOUND, "no such file".into())),
    };
    file_response(job_dir(&state.cfg.data_dir, &id).join(name), ct).await
}

/// Progress as Server-Sent Events: one event per line of the worker's events.ndjson, then a final
/// `status` event with the job record when it is finished.
pub async fn events(
    State(state): State<Arc<AppState>>,
    Path(id): Path<String>,
) -> ApiResult<Sse<impl Stream<Item = Result<Event, Infallible>>>> {
    load_job(&state, &id).await?;
    let path = job_dir(&state.cfg.data_dir, &id).join("events.ndjson");
    struct S {
        state: Arc<AppState>,
        id: String,
        path: std::path::PathBuf,
        offset: usize,
        pending: std::collections::VecDeque<String>,
        finished: bool,
        started: Instant,
    }
    let init = S { state: state.clone(), id, path, offset: 0, pending: Default::default(), finished: false, started: Instant::now() };
    let stream = futures_util::stream::unfold(init, |mut s| async move {
        loop {
            if let Some(line) = s.pending.pop_front() {
                let kind = serde_json::from_str::<Value>(&line).ok()
                    .and_then(|v| v["event"].as_str().map(|e| e.to_string()))
                    .unwrap_or_else(|| "message".into());
                return Some((Ok(Event::default().event(kind).data(line)), s));
            }
            if s.finished {
                return None;
            }
            if let Ok(bytes) = tokio::fs::read(&s.path).await {
                if bytes.len() > s.offset {
                    let chunk = &bytes[s.offset..];
                    if let Some(last_nl) = chunk.iter().rposition(|b| *b == b'\n') {
                        let text = String::from_utf8_lossy(&chunk[..last_nl]);
                        s.pending.extend(text.lines().filter(|l| !l.trim().is_empty()).map(|l| l.to_string()));
                        s.offset += last_nl + 1;
                        continue;
                    }
                }
            }
            let job = { let c = s.state.db.lock().await; db::job(&c, &s.id).ok().flatten() };
            match job {
                Some(j) if j.status != "queued" && j.status != "running" => {
                    s.finished = true;
                    let v = job_json(&s.state, &j, None);
                    return Some((Ok(Event::default().event("status").data(v.to_string())), s));
                }
                None => return None,
                _ => {}
            }
            if s.started.elapsed() > Duration::from_secs(3 * 3600) {
                return None;
            }
            tokio::time::sleep(Duration::from_millis(500)).await;
        }
    });
    Ok(Sse::new(stream).keep_alive(KeepAlive::default()))
}

// ---------------------------------------------------------------------------------------------------
// the account's latest result, for a session_viewer tab left open on `?live=`
// ---------------------------------------------------------------------------------------------------
pub async fn latest_manifest(State(state): State<Arc<AppState>>, Path(token): Path<String>) -> ApiResult<Response> {
    let a = {
        let c = state.db.lock().await;
        db::account_by_view_token(&c, &token)?
    }
    .ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "unknown view link".into()))?;
    let Some(job_id) = a.latest_job else {
        return Err(ApiError(StatusCode::NOT_FOUND, "no finished job yet".into()));
    };
    let dir = job_dir(&state.cfg.data_dir, &job_id);
    let texts: Vec<(String, [f64; 3], f64)> = tokio::fs::read(dir.join("preview.json"))
        .await
        .ok()
        .and_then(|b| serde_json::from_slice::<Value>(&b).ok())
        .and_then(|v| v["texts"].as_array().cloned())
        .unwrap_or_default()
        .iter()
        .filter_map(|t| {
            let at = t["at"].as_array()?;
            Some((t["text"].as_str()?.to_string(), [at.first()?.as_f64()?, at.get(1)?.as_f64()?, at.get(2)?.as_f64()?], t["height"].as_f64()?))
        })
        .collect();
    // The file path names the job, so the manifest itself changes with every new result and the
    // viewer's live poll reloads the scene.
    let file = format!("../../api/jobs/{job_id}/result.pb");
    let body = crate::preview::manifest(&format!("OpenNest latest ({job_id})"), &file, &texts);
    let mut r = body.into_response();
    r.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static("application/yaml"));
    r.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
    Ok(r)
}

// ---------------------------------------------------------------------------------------------------
// operator endpoints (X-Admin-Token)
// ---------------------------------------------------------------------------------------------------
fn require_admin(state: &AppState, headers: &HeaderMap) -> ApiResult<()> {
    let given = headers.get("x-admin-token").and_then(|v| v.to_str().ok()).unwrap_or("");
    match &state.cfg.admin_token {
        Some(t) if !given.is_empty() && crate::billing::ct_eq(given.as_bytes(), t.as_bytes()) => Ok(()),
        _ => Err(ApiError(StatusCode::UNAUTHORIZED, "admin token required".into())),
    }
}

#[derive(Deserialize)]
pub struct AdminAccountBody {
    email: String,
    #[serde(default)]
    credits: f64,
}

pub async fn admin_create_account(State(state): State<Arc<AppState>>, headers: HeaderMap, Json(b): Json<AdminAccountBody>)
    -> ApiResult<Json<Value>> {
    require_admin(&state, &headers)?;
    let email = b.email.trim().to_lowercase();
    if !valid_email(&email) {
        return Err(bad("a valid email address is required"));
    }
    let key = new_api_key();
    let c = state.db.lock().await;
    let a = match db::account_by_email(&c, &email)? {
        Some(a) => {
            db::set_key_hash(&c, &a.id, &hash_key(&key))?; // issues a NEW key for an existing account
            if b.credits != 0.0 {
                db::add_credits(&c, &a.id, b.credits, "admin", None)?;
            }
            db::account_by_id(&c, &a.id)?.expect("exists")
        }
        None => {
            let id = uuid::Uuid::new_v4().to_string();
            db::create_account(&c, &id, &email, &hash_key(&key), &random_token(16), 0.0)?;
            if b.credits != 0.0 {
                db::add_credits(&c, &id, b.credits, "admin", None)?;
            }
            db::account_by_id(&c, &id)?.expect("just inserted")
        }
    };
    Ok(Json(json!({"api_key": key, "account": a})))
}

#[derive(Deserialize)]
pub struct AdminCreditsBody {
    delta: f64,
    #[serde(default)]
    reason: String,
}

pub async fn admin_credits(State(state): State<Arc<AppState>>, headers: HeaderMap, Path(id): Path<String>, Json(b): Json<AdminCreditsBody>)
    -> ApiResult<Json<Value>> {
    require_admin(&state, &headers)?;
    let c = state.db.lock().await;
    db::account_by_id(&c, &id)?.ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "no such account".into()))?;
    db::add_credits(&c, &id, b.delta, if b.reason.is_empty() { "admin" } else { &b.reason }, None)?;
    Ok(Json(json!({"account": db::account_by_id(&c, &id)?})))
}

pub async fn admin_jobs(State(state): State<Arc<AppState>>, headers: HeaderMap) -> ApiResult<Json<Value>> {
    require_admin(&state, &headers)?;
    let c = state.db.lock().await;
    let jobs = db::recent_jobs(&c, 200)?;
    Ok(Json(json!({"jobs": jobs.iter().map(|j| {
        let mut v = job_json(&state, j, None);
        v["account_id"] = json!(j.account_id);
        v["ip"] = json!(j.ip);
        v
    }).collect::<Vec<_>>()})))
}
