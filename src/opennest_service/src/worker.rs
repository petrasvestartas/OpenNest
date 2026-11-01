//! Job runner: a fixed number of slots, each running one `opennest_job` process at a time.
//!
//! The process gets a CLEAN environment (only NP_WORKERS, set by the service, plus what Windows needs to
//! start a process), a wall cap enforced twice (`--max-wall` inside the solver, a hard kill a minute
//! later out here) and a cancel file the API creates on request.

use crate::db::{self, Finish, Job};
use crate::preview;
use crate::AppState;
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::Semaphore;

pub fn job_dir(data: &Path, id: &str) -> PathBuf {
    data.join("jobs").join(id)
}

pub async fn run(state: Arc<AppState>) {
    let slots = Arc::new(Semaphore::new(state.cfg.slots));
    loop {
        let permit = slots.clone().acquire_owned().await.expect("semaphore closed");
        let next = {
            let c = state.db.lock().await;
            db::claim_next(&c)
        };
        match next {
            Ok(Some(job)) => {
                let st = state.clone();
                tokio::spawn(async move {
                    let id = job.id.clone();
                    if let Err(e) = run_one(&st, job).await {
                        tracing::error!(job = %id, "job runner failed: {e}");
                    }
                    drop(permit);
                    st.wake.notify_one();
                });
            }
            Ok(None) => {
                drop(permit);
                let _ = tokio::time::timeout(Duration::from_secs(2), state.wake.notified()).await;
            }
            Err(e) => {
                drop(permit);
                tracing::error!("queue: {e}");
                tokio::time::sleep(Duration::from_secs(2)).await;
            }
        }
    }
}

async fn run_one(state: &Arc<AppState>, job: Job) -> Result<(), String> {
    let dir = job_dir(&state.cfg.data_dir, &job.id);
    let (job_file, result_file, events_file, cancel_file) =
        (dir.join("job.json"), dir.join("result.json"), dir.join("events.ndjson"), dir.join("cancel.flag"));
    if job.cancel_requested {
        let _ = tokio::fs::write(&cancel_file, b"").await;
    }
    tracing::info!(job = %job.id, fit_mode = job.fit_mode, cap = job.wall_cap, "start");

    let mut cmd = tokio::process::Command::new(&state.cfg.job_bin);
    cmd.arg(&job_file)
        .arg(&result_file)
        .arg("--events")
        .arg(&events_file)
        .arg("--events-layout")
        .arg("--interval")
        .arg("1")
        .arg("--max-wall")
        .arg(format!("{}", job.wall_cap))
        .arg("--cancel-file")
        .arg(&cancel_file)
        .arg("--quiet")
        .current_dir(&dir)
        .env_clear()
        .env("NP_WORKERS", state.cfg.threads_per_job.to_string())
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::from(std::fs::File::create(dir.join("solver.log")).map_err(|e| e.to_string())?))
        .kill_on_drop(true);
    #[cfg(windows)]
    for k in ["SystemRoot", "SYSTEMROOT", "TEMP", "TMP"] {
        if let Ok(v) = std::env::var(k) {
            cmd.env(k, v);
        }
    }

    let job_json: Arc<Option<Value>> =
        Arc::new(tokio::fs::read(&job_file).await.ok().and_then(|b| serde_json::from_slice(&b).ok()));

    let mut child = cmd.spawn().map_err(|e| format!("cannot start {}: {e}", state.cfg.job_bin.display()))?;
    let hard_limit = Duration::from_secs_f64(job.wall_cap + 60.0);
    let started = std::time::Instant::now();
    // Live scene for session_viewer: every LIVE_EVERY the newest progress layout becomes live.pb and
    // view.yaml points at it (with a version query, so the viewer's live poll sees a change).
    const LIVE_EVERY: Duration = Duration::from_secs(3);
    let mut ev_offset = 0usize;
    let mut latest_layout: Option<String> = None;
    let mut last_live = std::time::Instant::now() - LIVE_EVERY;
    let mut live_version = 0u32;
    let mut live_task: Option<tokio::task::JoinHandle<()>> = None;
    let status = loop {
        tokio::select! {
            s = child.wait() => break s.map_err(|e| e.to_string()),
            _ = tokio::time::sleep(Duration::from_millis(500)) => {
                if let Ok(bytes) = tokio::fs::read(&events_file).await {
                    if bytes.len() > ev_offset {
                        if let Some(last_nl) = bytes[ev_offset..].iter().rposition(|b| *b == b'\n') {
                            let text = String::from_utf8_lossy(&bytes[ev_offset..ev_offset + last_nl]).to_string();
                            if let Some(line) = text.lines().rev().find(|l| l.contains("\"layout\":")) {
                                latest_layout = Some(line.to_string());
                            }
                            ev_offset += last_nl + 1;
                        }
                    }
                }
                let idle = live_task.as_ref().map(|t| t.is_finished()).unwrap_or(true);
                if idle && last_live.elapsed() >= LIVE_EVERY {
                    if let (Some(line), Some(jj)) = (latest_layout.take(), job_json.as_ref().clone()) {
                        live_version += 1;
                        last_live = std::time::Instant::now();
                        let (d2, id2, v) = (dir.clone(), job.id.clone(), live_version);
                        live_task = Some(tokio::task::spawn_blocking(move || {
                            if let Err(e) = write_live(&jj, &line, &d2, &id2, v) {
                                tracing::debug!(job = %id2, "live preview: {e}");
                            }
                        }));
                    }
                }
                if started.elapsed() > hard_limit {
                    let _ = child.kill().await;
                    break Err("killed: exceeded the wall cap by more than 60 s".to_string());
                }
                let wants_cancel = {
                    let c = state.db.lock().await;
                    db::job(&c, &job.id).ok().flatten().map(|j| j.cancel_requested).unwrap_or(false)
                };
                if wants_cancel && !cancel_file.exists() {
                    let _ = tokio::fs::write(&cancel_file, b"").await;
                }
            }
        }
    };
    let elapsed = started.elapsed().as_secs_f64();

    if let Some(t) = live_task.take() {
        let _ = t.await;
    }
    let result: Option<Value> =
        tokio::fs::read(&result_file).await.ok().and_then(|b| serde_json::from_slice(&b).ok());
    let mut fin = Finish { status: "failed", n_sheets: None, unplaced: None, wall_s: Some(elapsed), error: None, has_preview: false };
    match (&status, &result) {
        (_, Some(r)) if r["status"] != "error" => {
            fin.status = match r["status"].as_str() {
                Some("ok") => "ok",
                Some("partial") => "partial",
                Some("cancelled") => "cancelled",
                _ => "failed",
            };
            fin.n_sheets = r["n_sheets"].as_i64();
            fin.unplaced = r["quality"]["unplaced"].as_i64();
            fin.wall_s = Some(r["wall_s"].as_f64().unwrap_or(elapsed));
            if let Some(jj) = job_json.as_ref().clone() {
                let (r2, d2, id2) = (r.clone(), dir.clone(), job.id.clone());
                match tokio::task::spawn_blocking(move || write_preview(&jj, &r2, &d2, &id2)).await {
                    Ok(Ok(())) => fin.has_preview = true,
                    Ok(Err(e)) => tracing::warn!(job = %job.id, "preview: {e}"),
                    Err(e) => tracing::warn!(job = %job.id, "preview task: {e}"),
                }
            }
        }
        (_, Some(r)) => fin.error = Some(r["error"].as_str().unwrap_or("solver error").to_string()),
        (Err(e), None) => fin.error = Some(e.clone()),
        (Ok(s), None) => fin.error = Some(format!("solver exited ({s}) without a result")),
    }
    let c = state.db.lock().await;
    db::finish_job(&c, &job.id, &fin).map_err(|e| e.to_string())?;
    tracing::info!(job = %job.id, status = fin.status, wall = elapsed, "done");
    Ok(())
}

/// The in-progress layout as a scene: one `progress` event line with `layout: [[sheet, tx, ty, angle], ...]`
/// per part copy in job order (part-major, qty copies), the same order the result's placements use.
fn write_live(job: &Value, event_line: &str, dir: &Path, id: &str, version: u32) -> Result<(), String> {
    let ev: Value = serde_json::from_str(event_line).map_err(|e| e.to_string())?;
    let layout = ev["layout"].as_array().ok_or("no layout")?;
    let mut placements = Vec::with_capacity(layout.len());
    let mut k = 0usize;
    for (i, p) in job["parts"].as_array().ok_or("no parts")?.iter().enumerate() {
        let qty = p.get("qty").and_then(|q| q.as_u64()).unwrap_or(1) as usize;
        for c in 0..qty {
            let l = layout.get(k).and_then(|l| l.as_array()).ok_or("layout shorter than the part list")?;
            placements.push(serde_json::json!({
                "part": p.get("id").cloned().unwrap_or(Value::from(i as u64)), "copy": c,
                "sheet": l.first().and_then(|v| v.as_i64()).unwrap_or(-1),
                "tx": l.get(1).and_then(|v| v.as_f64()).unwrap_or(0.0),
                "ty": l.get(2).and_then(|v| v.as_f64()).unwrap_or(0.0),
                "angle": l.get(3).and_then(|v| v.as_f64()).unwrap_or(0.0),
            }));
            k += 1;
        }
    }
    let result = serde_json::json!({"placements": placements});
    let p = preview::build(job, &result, &format!("opennest {id} (running)"))?;
    let tmp = dir.join("live.pb.tmp");
    std::fs::write(&tmp, &p.pb).map_err(|e| e.to_string())?;
    std::fs::rename(&tmp, dir.join("live.pb")).map_err(|e| e.to_string())?;
    std::fs::write(dir.join("view.yaml"), preview::manifest(&format!("OpenNest job {id} (running)"), &format!("live.pb?v={version}"), &p.texts))
        .map_err(|e| e.to_string())
}

fn write_preview(job: &Value, result: &Value, dir: &Path, id: &str) -> Result<(), String> {
    let p = preview::build(job, result, &format!("opennest {id}"))?;
    std::fs::write(dir.join("result.pb"), &p.pb).map_err(|e| e.to_string())?;
    std::fs::write(dir.join("view.yaml"), preview::manifest(&format!("OpenNest job {id}"), "result.pb", &p.texts))
        .map_err(|e| e.to_string())?;
    std::fs::write(dir.join("preview.json"), serde_json::to_vec(&serde_json::json!({
        "sheets": p.sheets,
        "texts": p.texts.iter().map(|(t, at, h)| serde_json::json!({"text": t, "at": at, "height": h})).collect::<Vec<_>>(),
    })).unwrap_or_default())
    .map_err(|e| e.to_string())
}
