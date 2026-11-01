//! OpenNest web service, single-user edition: one password, a job queue running the `opennest_job`
//! worker, live progress, results as JSON and as session_viewer scenes. Configuration is
//! environment-only; see README.md.

mod api;
mod config;
mod db;
mod preview;
mod session_import;
mod worker;

use axum::routing::{get, post};
use axum::Router;
use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Instant;
use tokio::sync::{Mutex, Notify};
use tower_http::cors::{Any, CorsLayer};
use tower_http::limit::RequestBodyLimitLayer;
use tower_http::services::ServeDir;

pub struct AppState {
    pub cfg: config::Config,
    pub db: Mutex<rusqlite::Connection>,
    pub wake: Notify,
    pub limiter: Mutex<HashMap<String, Vec<Instant>>>,
}

/// Ctrl+C, or SIGTERM from a platform restart (docker stop, systemd). Jobs still running at that
/// moment are closed by `db::recover_interrupted` on the next start.
async fn shutdown_signal() {
    #[cfg(unix)]
    {
        let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()).expect("SIGTERM handler");
        tokio::select! {
            _ = tokio::signal::ctrl_c() => {}
            _ = term.recv() => {}
        }
    }
    #[cfg(not(unix))]
    let _ = tokio::signal::ctrl_c().await;
    tracing::info!("shutting down");
}

#[tokio::main]
async fn main() {
    // `opennest_service --example-session out.pb` writes the format reference session and exits.
    let args: Vec<String> = std::env::args().collect();
    if args.get(1).map(|a| a == "--example-session").unwrap_or(false) {
        let out = args.get(2).cloned().unwrap_or_else(|| "example_session.pb".into());
        std::fs::write(&out, session_import::example_session()).expect("cannot write the example");
        println!("wrote {out}");
        return;
    }
    // `opennest_service --job-to-session job.json out.pb` converts an opennest.job/1 document.
    if args.get(1).map(|a| a == "--job-to-session").unwrap_or(false) {
        let (inp, out) = (args.get(2).expect("job.json"), args.get(3).expect("out.pb"));
        let job: serde_json::Value = serde_json::from_slice(&std::fs::read(inp).expect("cannot read the job")).expect("invalid JSON");
        std::fs::write(out, session_import::job_to_session(&job).expect("conversion failed")).expect("cannot write");
        println!("wrote {out}");
        return;
    }
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
        .init();
    let cfg = match config::Config::from_env() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("opennest_service: {e}");
            std::process::exit(2);
        }
    };
    std::fs::create_dir_all(cfg.data_dir.join("jobs")).expect("cannot create the data directory");
    let conn = db::open(&cfg.data_dir.join("opennest.sqlite")).expect("cannot open the database");
    match db::recover_interrupted(&conn) {
        Ok(0) => {}
        Ok(n) => tracing::warn!("closed {n} job(s) interrupted by the last shutdown"),
        Err(e) => tracing::error!("recovery: {e}"),
    }
    tracing::info!(
        bind = %cfg.bind, slots = cfg.slots, threads_per_job = cfg.threads_per_job,
        job_bin = %cfg.job_bin.display(), public_url = %cfg.public_url, "starting"
    );
    let state = Arc::new(AppState {
        cfg: cfg.clone(),
        db: Mutex::new(conn),
        wake: Notify::new(),
        limiter: Mutex::new(HashMap::new()),
    });
    tokio::spawn(worker::run(state.clone()));

    let cors = CorsLayer::new().allow_origin(Any).allow_methods(Any).allow_headers(Any);
    let app = Router::new()
        .route("/healthz", get(api::health))
        .route("/api/info", get(api::info))
        .route("/api/login", post(api::login))
        .route("/api/jobs", post(api::submit).get(api::list_jobs))
        .route("/api/jobs/{id}", get(api::get_job))
        .route("/api/jobs/{id}/cancel", post(api::cancel))
        .route("/api/jobs/{id}/events", get(api::events))
        .route("/api/jobs/{id}/result", get(api::result))
        .route("/api/jobs/{id}/{file}", get(api::job_file))
        .route("/live/{token}/view.yaml", get(api::latest_manifest))
        .fallback_service(ServeDir::new(&cfg.web_dir))
        .layer(RequestBodyLimitLayer::new(cfg.max_body_bytes))
        .layer(cors)
        .with_state(state.clone());

    let listener = tokio::net::TcpListener::bind(&cfg.bind).await.expect("cannot bind");
    let server = axum::serve(listener, app.into_make_service_with_connect_info::<SocketAddr>())
        .with_graceful_shutdown(shutdown_signal());
    if let Err(e) = server.await {
        tracing::error!("server: {e}");
    }
}
