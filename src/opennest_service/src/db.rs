//! SQLite store: the job table. One connection behind a mutex; every statement is a short indexed
//! lookup or write.

use rusqlite::{params, Connection, OptionalExtension};
use serde::Serialize;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

pub fn now() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

pub fn open(path: &Path) -> rusqlite::Result<Connection> {
    let c = Connection::open(path)?;
    c.execute_batch(
        "PRAGMA journal_mode=WAL;
         CREATE TABLE IF NOT EXISTS jobs2 (
             id          TEXT PRIMARY KEY,
             name        TEXT NOT NULL DEFAULT '',
             ip          TEXT NOT NULL,
             status      TEXT NOT NULL,          -- queued running ok partial cancelled failed
             created     INTEGER NOT NULL,
             started     INTEGER,
             finished    INTEGER,
             wall_cap    REAL NOT NULL,
             fit_mode    INTEGER NOT NULL DEFAULT 0,
             part_copies INTEGER NOT NULL,
             n_sheets    INTEGER,
             unplaced    INTEGER,
             wall_s      REAL,
             error       TEXT,
             has_preview INTEGER NOT NULL DEFAULT 0,
             cancel_requested INTEGER NOT NULL DEFAULT 0
         );
         CREATE INDEX IF NOT EXISTS jobs2_queue ON jobs2(status, created);
         CREATE INDEX IF NOT EXISTS jobs2_created ON jobs2(created);",
    )?;
    // added later; ignore "duplicate column" on databases that already have it
    let _ = c.execute_batch("ALTER TABLE jobs2 ADD COLUMN note TEXT");
    Ok(c)
}

#[derive(Clone, Debug, Serialize)]
pub struct Job {
    pub id: String,
    pub name: String,
    #[serde(skip)]
    pub ip: String,
    pub status: String,
    pub created: i64,
    pub started: Option<i64>,
    pub finished: Option<i64>,
    pub wall_cap: f64,
    pub fit_mode: i64,
    pub part_copies: i64,
    pub n_sheets: Option<i64>,
    pub unplaced: Option<i64>,
    pub wall_s: Option<f64>,
    pub error: Option<String>,
    pub note: Option<String>,
    pub has_preview: bool,
    pub cancel_requested: bool,
}

fn job_row(r: &rusqlite::Row) -> rusqlite::Result<Job> {
    Ok(Job {
        id: r.get("id")?,
        name: r.get("name")?,
        ip: r.get("ip")?,
        status: r.get("status")?,
        created: r.get("created")?,
        started: r.get("started")?,
        finished: r.get("finished")?,
        wall_cap: r.get("wall_cap")?,
        fit_mode: r.get("fit_mode")?,
        part_copies: r.get("part_copies")?,
        n_sheets: r.get("n_sheets")?,
        unplaced: r.get("unplaced")?,
        wall_s: r.get("wall_s")?,
        error: r.get("error")?,
        note: r.get("note")?,
        has_preview: r.get::<_, i64>("has_preview")? != 0,
        cancel_requested: r.get::<_, i64>("cancel_requested")? != 0,
    })
}

pub fn insert_job(c: &Connection, j: &Job) -> rusqlite::Result<()> {
    c.execute(
        "INSERT INTO jobs2 (id, name, ip, status, created, wall_cap, fit_mode, part_copies)
         VALUES (?1, ?2, ?3, 'queued', ?4, ?5, ?6, ?7)",
        params![j.id, j.name, j.ip, j.created, j.wall_cap, j.fit_mode, j.part_copies],
    )?;
    Ok(())
}

pub fn job(c: &Connection, id: &str) -> rusqlite::Result<Option<Job>> {
    c.query_row("SELECT * FROM jobs2 WHERE id = ?1", [id], job_row).optional()
}

pub fn recent_jobs(c: &Connection, limit: i64) -> rusqlite::Result<Vec<Job>> {
    let mut st = c.prepare("SELECT * FROM jobs2 ORDER BY created DESC LIMIT ?1")?;
    let rows = st.query_map(params![limit], job_row)?;
    rows.collect()
}

/// The newest finished job that has a session_viewer preview.
pub fn latest_with_preview(c: &Connection) -> rusqlite::Result<Option<Job>> {
    c.query_row("SELECT * FROM jobs2 WHERE has_preview = 1 ORDER BY finished DESC LIMIT 1", [], job_row).optional()
}

/// Atomically take the oldest queued job and mark it running.
pub fn claim_next(c: &Connection) -> rusqlite::Result<Option<Job>> {
    let id: Option<String> = c
        .query_row(
            "UPDATE jobs2 SET status = 'running', started = ?1
             WHERE id = (SELECT id FROM jobs2 WHERE status = 'queued' ORDER BY created LIMIT 1)
             RETURNING id",
            [now()],
            |r| r.get(0),
        )
        .optional()?;
    match id {
        Some(id) => job(c, &id),
        None => Ok(None),
    }
}

pub fn count_active(c: &Connection) -> rusqlite::Result<i64> {
    c.query_row("SELECT COUNT(*) FROM jobs2 WHERE status IN ('queued','running')", [], |r| r.get(0))
}

pub fn queue_position(c: &Connection, created: i64) -> rusqlite::Result<i64> {
    c.query_row("SELECT COUNT(*) FROM jobs2 WHERE status = 'queued' AND created < ?1", [created], |r| r.get(0))
}

pub fn request_cancel(c: &Connection, id: &str) -> rusqlite::Result<usize> {
    c.execute("UPDATE jobs2 SET cancel_requested = 1 WHERE id = ?1 AND status IN ('queued','running')", [id])
}

/// A queued job that is cancelled never ran: close it.
pub fn cancel_queued(c: &Connection, id: &str) -> rusqlite::Result<bool> {
    let n = c.execute(
        "UPDATE jobs2 SET status = 'cancelled', finished = ?2 WHERE id = ?1 AND status = 'queued'",
        params![id, now()],
    )?;
    Ok(n == 1)
}

pub struct Finish<'a> {
    pub status: &'a str,
    pub n_sheets: Option<i64>,
    pub unplaced: Option<i64>,
    pub wall_s: Option<f64>,
    pub error: Option<String>,
    pub note: Option<String>,
    pub has_preview: bool,
}

pub fn finish_job(c: &Connection, id: &str, f: &Finish) -> rusqlite::Result<()> {
    c.execute(
        "UPDATE jobs2 SET status = ?2, finished = ?3, n_sheets = ?4, unplaced = ?5, wall_s = ?6, error = ?7, has_preview = ?8, note = ?9
         WHERE id = ?1",
        params![id, f.status, now(), f.n_sheets, f.unplaced, f.wall_s, f.error, f.has_preview as i64, f.note],
    )?;
    Ok(())
}

/// Jobs left 'running' by a previous process: their worker is gone, so close them.
pub fn recover_interrupted(c: &Connection) -> rusqlite::Result<usize> {
    let n = c.execute(
        "UPDATE jobs2 SET status = 'failed', finished = ?1,
             error = 'the service restarted while this job was running'
         WHERE status = 'running'",
        [now()],
    )?;
    Ok(n)
}
