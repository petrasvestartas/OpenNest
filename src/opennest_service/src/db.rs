//! SQLite store: accounts, jobs, the credit ledger and processed webhook events.
//! One connection behind a mutex; every statement is a short indexed lookup or write.

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
         PRAGMA foreign_keys=ON;
         CREATE TABLE IF NOT EXISTS accounts (
             id          TEXT PRIMARY KEY,
             email       TEXT NOT NULL UNIQUE,
             key_hash    TEXT NOT NULL UNIQUE,
             view_token  TEXT NOT NULL UNIQUE,
             credits     REAL NOT NULL DEFAULT 0,
             latest_job  TEXT,
             created     INTEGER NOT NULL
         );
         CREATE TABLE IF NOT EXISTS jobs (
             id          TEXT PRIMARY KEY,
             account_id  TEXT REFERENCES accounts(id),
             ip          TEXT NOT NULL,
             tier        TEXT NOT NULL,
             status      TEXT NOT NULL,          -- queued running ok partial cancelled failed
             created     INTEGER NOT NULL,
             started     INTEGER,
             finished    INTEGER,
             wall_cap    REAL NOT NULL,
             reserved    REAL NOT NULL DEFAULT 0,
             charged     REAL NOT NULL DEFAULT 0,
             part_copies INTEGER NOT NULL,
             n_sheets    INTEGER,
             unplaced    INTEGER,
             wall_s      REAL,
             error       TEXT,
             cancel_requested INTEGER NOT NULL DEFAULT 0
         );
         CREATE INDEX IF NOT EXISTS jobs_queue ON jobs(status, created);
         CREATE INDEX IF NOT EXISTS jobs_account ON jobs(account_id, created);
         CREATE TABLE IF NOT EXISTS ledger (
             id          INTEGER PRIMARY KEY AUTOINCREMENT,
             account_id  TEXT NOT NULL REFERENCES accounts(id),
             delta       REAL NOT NULL,
             reason      TEXT NOT NULL,
             job_id      TEXT,
             ts          INTEGER NOT NULL
         );
         CREATE TABLE IF NOT EXISTS webhook_events (
             id          TEXT PRIMARY KEY,
             ts          INTEGER NOT NULL
         );",
    )?;
    Ok(c)
}

#[derive(Clone, Debug, Serialize)]
pub struct Account {
    pub id: String,
    pub email: String,
    #[serde(skip)]
    pub view_token: String,
    pub credits: f64,
    pub latest_job: Option<String>,
    pub created: i64,
}

fn account_row(r: &rusqlite::Row) -> rusqlite::Result<Account> {
    Ok(Account {
        id: r.get("id")?,
        email: r.get("email")?,
        view_token: r.get("view_token")?,
        credits: r.get("credits")?,
        latest_job: r.get("latest_job")?,
        created: r.get("created")?,
    })
}

pub fn create_account(c: &Connection, id: &str, email: &str, key_hash: &str, view_token: &str, credits: f64)
    -> rusqlite::Result<()> {
    let tx = c.unchecked_transaction()?;
    tx.execute(
        "INSERT INTO accounts (id, email, key_hash, view_token, credits, created) VALUES (?1, ?2, ?3, ?4, 0, ?5)",
        params![id, email, key_hash, view_token, now()],
    )?;
    if credits != 0.0 {
        add_credits(&tx, id, credits, "signup", None)?;
    }
    tx.commit()
}

pub fn account_by_key_hash(c: &Connection, key_hash: &str) -> rusqlite::Result<Option<Account>> {
    c.query_row("SELECT * FROM accounts WHERE key_hash = ?1", [key_hash], account_row).optional()
}

pub fn account_by_id(c: &Connection, id: &str) -> rusqlite::Result<Option<Account>> {
    c.query_row("SELECT * FROM accounts WHERE id = ?1", [id], account_row).optional()
}

pub fn account_by_email(c: &Connection, email: &str) -> rusqlite::Result<Option<Account>> {
    c.query_row("SELECT * FROM accounts WHERE email = ?1", [email], account_row).optional()
}

pub fn account_by_view_token(c: &Connection, token: &str) -> rusqlite::Result<Option<Account>> {
    c.query_row("SELECT * FROM accounts WHERE view_token = ?1", [token], account_row).optional()
}

pub fn set_key_hash(c: &Connection, id: &str, key_hash: &str) -> rusqlite::Result<usize> {
    c.execute("UPDATE accounts SET key_hash = ?2 WHERE id = ?1", params![id, key_hash])
}

/// Change a balance and record why. Positive = top-up / refund, negative = reservation / charge.
pub fn add_credits(c: &Connection, account_id: &str, delta: f64, reason: &str, job_id: Option<&str>)
    -> rusqlite::Result<()> {
    c.execute("UPDATE accounts SET credits = credits + ?2 WHERE id = ?1", params![account_id, delta])?;
    c.execute(
        "INSERT INTO ledger (account_id, delta, reason, job_id, ts) VALUES (?1, ?2, ?3, ?4, ?5)",
        params![account_id, delta, reason, job_id, now()],
    )?;
    Ok(())
}

#[derive(Clone, Debug, Serialize)]
pub struct Job {
    pub id: String,
    #[serde(skip)]
    pub account_id: Option<String>,
    #[serde(skip)]
    pub ip: String,
    pub tier: String,
    pub status: String,
    pub created: i64,
    pub started: Option<i64>,
    pub finished: Option<i64>,
    pub wall_cap: f64,
    pub reserved: f64,
    pub charged: f64,
    pub part_copies: i64,
    pub n_sheets: Option<i64>,
    pub unplaced: Option<i64>,
    pub wall_s: Option<f64>,
    pub error: Option<String>,
    pub cancel_requested: bool,
}

fn job_row(r: &rusqlite::Row) -> rusqlite::Result<Job> {
    Ok(Job {
        id: r.get("id")?,
        account_id: r.get("account_id")?,
        ip: r.get("ip")?,
        tier: r.get("tier")?,
        status: r.get("status")?,
        created: r.get("created")?,
        started: r.get("started")?,
        finished: r.get("finished")?,
        wall_cap: r.get("wall_cap")?,
        reserved: r.get("reserved")?,
        charged: r.get("charged")?,
        part_copies: r.get("part_copies")?,
        n_sheets: r.get("n_sheets")?,
        unplaced: r.get("unplaced")?,
        wall_s: r.get("wall_s")?,
        error: r.get("error")?,
        cancel_requested: r.get::<_, i64>("cancel_requested")? != 0,
    })
}

/// Insert a queued job; for a billed job the reservation is taken in the same transaction, so two
/// concurrent submissions can never spend the same credit twice.
pub fn insert_job(c: &Connection, j: &Job) -> rusqlite::Result<bool> {
    let tx = c.unchecked_transaction()?;
    if let (Some(acc), true) = (&j.account_id, j.reserved > 0.0) {
        let n = tx.execute(
            "UPDATE accounts SET credits = credits - ?2 WHERE id = ?1 AND credits >= ?2",
            params![acc, j.reserved],
        )?;
        if n == 0 {
            return Ok(false);
        }
        tx.execute(
            "INSERT INTO ledger (account_id, delta, reason, job_id, ts) VALUES (?1, ?2, 'reserve', ?3, ?4)",
            params![acc, -j.reserved, j.id, now()],
        )?;
    }
    tx.execute(
        "INSERT INTO jobs (id, account_id, ip, tier, status, created, wall_cap, reserved, part_copies)
         VALUES (?1, ?2, ?3, ?4, 'queued', ?5, ?6, ?7, ?8)",
        params![j.id, j.account_id, j.ip, j.tier, j.created, j.wall_cap, j.reserved, j.part_copies],
    )?;
    tx.commit()?;
    Ok(true)
}

pub fn job(c: &Connection, id: &str) -> rusqlite::Result<Option<Job>> {
    c.query_row("SELECT * FROM jobs WHERE id = ?1", [id], job_row).optional()
}

pub fn jobs_of(c: &Connection, account_id: &str, limit: i64) -> rusqlite::Result<Vec<Job>> {
    let mut st = c.prepare("SELECT * FROM jobs WHERE account_id = ?1 ORDER BY created DESC LIMIT ?2")?;
    let rows = st.query_map(params![account_id, limit], job_row)?;
    rows.collect()
}

pub fn recent_jobs(c: &Connection, limit: i64) -> rusqlite::Result<Vec<Job>> {
    let mut st = c.prepare("SELECT * FROM jobs ORDER BY created DESC LIMIT ?1")?;
    let rows = st.query_map(params![limit], job_row)?;
    rows.collect()
}

/// Atomically take the oldest queued job and mark it running.
pub fn claim_next(c: &Connection) -> rusqlite::Result<Option<Job>> {
    let id: Option<String> = c
        .query_row(
            "UPDATE jobs SET status = 'running', started = ?1
             WHERE id = (SELECT id FROM jobs WHERE status = 'queued' ORDER BY created LIMIT 1)
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

pub fn count_active(c: &Connection, account_id: Option<&str>, ip: Option<&str>) -> rusqlite::Result<i64> {
    match (account_id, ip) {
        (Some(a), _) => c.query_row(
            "SELECT COUNT(*) FROM jobs WHERE account_id = ?1 AND status IN ('queued','running')",
            [a],
            |r| r.get(0),
        ),
        (None, Some(ip)) => c.query_row(
            "SELECT COUNT(*) FROM jobs WHERE account_id IS NULL AND ip = ?1 AND status IN ('queued','running')",
            [ip],
            |r| r.get(0),
        ),
        _ => Ok(0),
    }
}

pub fn queue_position(c: &Connection, created: i64) -> rusqlite::Result<i64> {
    c.query_row("SELECT COUNT(*) FROM jobs WHERE status = 'queued' AND created < ?1", [created], |r| r.get(0))
}

pub fn request_cancel(c: &Connection, id: &str) -> rusqlite::Result<usize> {
    c.execute("UPDATE jobs SET cancel_requested = 1 WHERE id = ?1 AND status IN ('queued','running')", [id])
}

/// A queued job that is cancelled never ran: close it and return the whole reservation.
pub fn cancel_queued(c: &Connection, id: &str) -> rusqlite::Result<bool> {
    let tx = c.unchecked_transaction()?;
    let j = match tx.query_row("SELECT * FROM jobs WHERE id = ?1 AND status = 'queued'", [id], job_row).optional()? {
        Some(j) => j,
        None => return Ok(false),
    };
    tx.execute("UPDATE jobs SET status = 'cancelled', finished = ?2 WHERE id = ?1", params![id, now()])?;
    if let (Some(acc), true) = (&j.account_id, j.reserved > 0.0) {
        add_credits(&tx, acc, j.reserved, "refund (cancelled before start)", Some(id))?;
    }
    tx.commit()?;
    Ok(true)
}

pub struct Finish<'a> {
    pub status: &'a str,
    pub charged: f64,
    pub n_sheets: Option<i64>,
    pub unplaced: Option<i64>,
    pub wall_s: Option<f64>,
    pub error: Option<String>,
    pub has_preview: bool,
}

/// Close a job, charge what it used and give back the rest of its reservation.
pub fn finish_job(c: &Connection, j: &Job, f: &Finish) -> rusqlite::Result<()> {
    let tx = c.unchecked_transaction()?;
    tx.execute(
        "UPDATE jobs SET status = ?2, finished = ?3, charged = ?4, n_sheets = ?5, unplaced = ?6, wall_s = ?7, error = ?8
         WHERE id = ?1",
        params![j.id, f.status, now(), f.charged, f.n_sheets, f.unplaced, f.wall_s, f.error],
    )?;
    if let Some(acc) = &j.account_id {
        let refund = j.reserved - f.charged;
        if refund > 0.0 {
            add_credits(&tx, acc, refund, "refund (unused reservation)", Some(&j.id))?;
        }
        if f.has_preview {
            tx.execute("UPDATE accounts SET latest_job = ?2 WHERE id = ?1", params![acc, j.id])?;
        }
    }
    tx.commit()
}

/// Jobs left 'running' by a previous process: their worker is gone, so close them and refund.
pub fn recover_interrupted(c: &Connection) -> rusqlite::Result<usize> {
    let mut st = c.prepare("SELECT * FROM jobs WHERE status = 'running'")?;
    let stale: Vec<Job> = st.query_map([], job_row)?.collect::<Result<_, _>>()?;
    for j in &stale {
        finish_job(
            c,
            j,
            &Finish {
                status: "failed",
                charged: 0.0,
                n_sheets: None,
                unplaced: None,
                wall_s: None,
                error: Some("the service restarted while this job was running; credits were refunded".into()),
                has_preview: false,
            },
        )?;
    }
    Ok(stale.len())
}

/// Record a webhook event id; false when it was already processed (providers retry deliveries).
pub fn first_webhook(c: &Connection, event_id: &str) -> rusqlite::Result<bool> {
    let n = c.execute("INSERT OR IGNORE INTO webhook_events (id, ts) VALUES (?1, ?2)", params![event_id, now()])?;
    Ok(n == 1)
}
