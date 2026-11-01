//! HTTP API. One password unlocks everything that changes state (`Authorization: Bearer <password>`
//! or `X-Password`). A job id is a random UUID and works as a read-only link: status, progress,
//! result and the viewer files of a job need no password, so a viewer link can be shared.

use crate::db::{self, Job};
use crate::worker::job_dir;
use crate::AppState;
use axum::body::Bytes;
use axum::extract::{ConnectInfo, Path, State};
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::sse::{Event, KeepAlive, Sse};
use axum::response::{IntoResponse, Response};
use axum::Json;
use futures_util::stream::Stream;
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

/// Constant-time comparison.
fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

/// Read-only link to the latest result: derived from the password, not reversible, safe in a URL.
pub fn view_token(password: &str) -> String {
    hex::encode(Sha256::digest(format!("opennest-view:{password}").as_bytes()))[..32].to_string()
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

async fn require_password(state: &AppState, headers: &HeaderMap, ip: &str) -> ApiResult<()> {
    let given = headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .map(|v| v.strip_prefix("Bearer ").unwrap_or(v).trim())
        .or_else(|| headers.get("x-password").and_then(|v| v.to_str().ok()))
        .unwrap_or("");
    if ct_eq(given.as_bytes(), state.cfg.password.as_bytes()) {
        return Ok(());
    }
    // Slow down guessing: 10 wrong tries per IP per 10 minutes.
    let mut m = state.limiter.lock().await;
    let hits = m.entry(format!("pw:{ip}")).or_default();
    let now = Instant::now();
    hits.retain(|t| now.duration_since(*t) < Duration::from_secs(600));
    hits.push(now);
    if hits.len() > 10 {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "too many wrong passwords; wait 10 minutes".into()));
    }
    Err(ApiError(StatusCode::UNAUTHORIZED, "wrong password".into()))
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
        "job": format!("{base}/job.json"),
        "view_yaml": format!("{base}/view.yaml"),
        "result_pb": format!("{base}/result.pb"),
        "viewer": viewer_link(state, &format!("{base}/view.yaml")),
    });
    v
}

// ---------------------------------------------------------------------------------------------------
pub async fn health() -> &'static str {
    "ok"
}

/// Public: what the page needs before login. `login` checks the password.
pub async fn info(State(state): State<Arc<AppState>>) -> Json<Value> {
    Json(json!({
        "service": "opennest",
        "version": env!("CARGO_PKG_VERSION"),
        "threads_per_job": state.cfg.threads_per_job,
        "slots": state.cfg.slots,
        "max_wall_seconds": state.cfg.max_wall_seconds,
        "default_wall_seconds": state.cfg.default_wall_seconds,
        "viewer_url": state.cfg.viewer_url,
    }))
}

pub async fn login(State(state): State<Arc<AppState>>, ConnectInfo(addr): ConnectInfo<SocketAddr>, headers: HeaderMap)
    -> ApiResult<Json<Value>> {
    let ip = client_ip(&state, &headers, &addr);
    require_password(&state, &headers, &ip).await?;
    let latest = format!("{}/live/{}/view.yaml", state.cfg.public_url, view_token(&state.cfg.password));
    Ok(Json(json!({
        "ok": true,
        "latest_view_yaml": latest,
        "latest_viewer": viewer_link(&state, &latest),
    })))
}

const MAX_VERTICES: usize = 4_000_000;
const MAX_SHEET_COPIES: u64 = 1000;
const MAX_PART_COPIES: usize = 20_000;

pub async fn submit(
    State(state): State<Arc<AppState>>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    body: Bytes,
) -> ApiResult<Response> {
    let ip = client_ip(&state, &headers, &addr);
    require_password(&state, &headers, &ip).await?;

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
    if copies > MAX_PART_COPIES {
        return Err(bad(format!("{copies} part copies exceed the limit of {MAX_PART_COPIES}")));
    }
    if verts > MAX_VERTICES {
        return Err(bad(format!("{verts} vertices (counting copies) exceed the limit of {MAX_VERTICES}; simplify the outlines")));
    }
    if sheet_copies == 0 || sheet_copies > MAX_SHEET_COPIES {
        return Err(bad(format!("between 1 and {MAX_SHEET_COPIES} sheets are allowed")));
    }

    if !job["params"].is_object() {
        job["params"] = json!({});
    }
    job["params"]["n_threads"] = json!(state.cfg.threads_per_job);
    let fit_mode = job["params"]["fit_mode"].as_i64().unwrap_or(0);
    let asked = job["limits"]["wall_seconds"].as_f64().filter(|w| *w > 0.0).unwrap_or(state.cfg.default_wall_seconds);
    let wall_cap = asked.min(state.cfg.max_wall_seconds);
    job["limits"] = json!({"wall_seconds": wall_cap});
    let name: String = job["name"].as_str().unwrap_or("").chars().take(80).collect();

    let id = uuid::Uuid::new_v4().to_string();
    let dir = job_dir(&state.cfg.data_dir, &id);
    tokio::fs::create_dir_all(&dir).await.map_err(|e| ApiError(StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    tokio::fs::write(dir.join("job.json"), serde_json::to_vec(&job).unwrap_or_default())
        .await
        .map_err(|e| ApiError(StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    // an empty scene from the first second, so a viewer opened on the job's link just waits
    let _ = tokio::fs::write(dir.join("view.yaml"), crate::preview::empty_manifest(&format!("OpenNest job {id} (queued)"))).await;
    let j = Job {
        id: id.clone(),
        name,
        ip,
        status: "queued".into(),
        created: db::now(),
        started: None,
        finished: None,
        wall_cap,
        fit_mode,
        part_copies: copies as i64,
        n_sheets: None,
        unplaced: None,
        wall_s: None,
        error: None,
        has_preview: false,
        cancel_requested: false,
    };
    let pos = {
        let c = state.db.lock().await;
        db::insert_job(&c, &j)?;
        db::queue_position(&c, j.created)?
    };
    state.wake.notify_one();
    Ok((StatusCode::CREATED, Json(job_json(&state, &j, Some(pos)))).into_response())
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

pub async fn list_jobs(State(state): State<Arc<AppState>>, ConnectInfo(addr): ConnectInfo<SocketAddr>, headers: HeaderMap)
    -> ApiResult<Json<Value>> {
    let ip = client_ip(&state, &headers, &addr);
    require_password(&state, &headers, &ip).await?;
    let c = state.db.lock().await;
    let jobs = db::recent_jobs(&c, 100)?;
    let active = db::count_active(&c)?;
    Ok(Json(json!({"active": active, "jobs": jobs.iter().map(|j| job_json(&state, j, None)).collect::<Vec<_>>()})))
}

pub async fn cancel(State(state): State<Arc<AppState>>, ConnectInfo(addr): ConnectInfo<SocketAddr>, headers: HeaderMap, Path(id): Path<String>)
    -> ApiResult<Json<Value>> {
    let ip = client_ip(&state, &headers, &addr);
    require_password(&state, &headers, &ip).await?;
    load_job(&state, &id).await?;
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
        "live.pb" => ("live.pb", "application/octet-stream"),
        "job.json" => ("job.json", "application/json"),
        "solver.log" => ("solver.log", "text/plain; charset=utf-8"),
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

/// The latest finished result, for a session_viewer tab left open on `?live=`.
pub async fn latest_manifest(State(state): State<Arc<AppState>>, Path(token): Path<String>) -> ApiResult<Response> {
    if !ct_eq(token.as_bytes(), view_token(&state.cfg.password).as_bytes()) {
        return Err(ApiError(StatusCode::NOT_FOUND, "unknown view link".into()));
    }
    let job = {
        let c = state.db.lock().await;
        db::latest_with_preview(&c)?
    }
    .ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "no finished job yet".into()))?;
    let dir = job_dir(&state.cfg.data_dir, &job.id);
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
    let file = format!("../../api/jobs/{}/result.pb", job.id);
    let body = crate::preview::manifest(&format!("OpenNest latest ({})", job.id), &file, &texts);
    let mut r = body.into_response();
    r.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static("application/yaml"));
    r.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-cache"));
    Ok(r)
}

// ---------------------------------------------------------------------------------------------------
// parts-only scene: what was loaded, before nesting (the page shows it in session_viewer)
// ---------------------------------------------------------------------------------------------------
pub async fn preview(
    State(state): State<Arc<AppState>>,
    ConnectInfo(addr): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    body: Bytes,
) -> ApiResult<Json<Value>> {
    let ip = client_ip(&state, &headers, &addr);
    require_password(&state, &headers, &ip).await?;
    let job: Value = serde_json::from_slice(&body).map_err(|e| bad(format!("invalid JSON: {e}")))?;
    let parts = job["parts"].as_array().ok_or_else(|| bad("job.parts must be an array"))?;
    if parts.is_empty() || parts.len() > 20_000 {
        return Err(bad("between 1 and 20000 parts"));
    }
    let mut placements = Vec::new();
    for (i, p) in parts.iter().enumerate() {
        let id = p.get("id").cloned().unwrap_or(Value::from(i as u64));
        placements.push(json!({"part": id, "copy": 0, "sheet": -1, "tx": 0.0, "ty": 0.0, "angle": 0.0}));
    }
    let scene = json!({"parts": parts, "sheets": []});
    let result = json!({"placements": placements});
    let id = uuid::Uuid::new_v4().to_string();
    let dir = state.cfg.data_dir.join("previews").join(&id);
    let (d2, id2) = (dir.clone(), id.clone());
    tokio::task::spawn_blocking(move || -> Result<(), String> {
        let p = crate::preview::build(&scene, &result, &format!("parts {id2}"))?;
        std::fs::create_dir_all(&d2).map_err(|e| e.to_string())?;
        std::fs::write(d2.join("parts.pb"), &p.pb).map_err(|e| e.to_string())?;
        std::fs::write(d2.join("view.yaml"), crate::preview::manifest("loaded parts", "parts.pb", &p.texts)).map_err(|e| e.to_string())
    })
    .await
    .map_err(|e| ApiError(StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?
    .map_err(bad)?;
    let base = format!("{}/api/previews/{}", state.cfg.public_url, id);
    Ok(Json(json!({"id": id, "links": {"view_yaml": format!("{base}/view.yaml"), "viewer": viewer_link(&state, &format!("{base}/view.yaml"))}})))
}

pub async fn preview_file(State(state): State<Arc<AppState>>, Path((id, file)): Path<(String, String)>) -> ApiResult<Response> {
    if uuid::Uuid::parse_str(&id).is_err() {
        return Err(ApiError(StatusCode::NOT_FOUND, "no such preview".into()));
    }
    let (name, ct) = match file.as_str() {
        "view.yaml" => ("view.yaml", "application/yaml"),
        "parts.pb" => ("parts.pb", "application/octet-stream"),
        _ => return Err(ApiError(StatusCode::NOT_FOUND, "no such file".into())),
    };
    file_response(state.cfg.data_dir.join("previews").join(id).join(name), ct).await
}
