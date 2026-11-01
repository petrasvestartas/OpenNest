//! OpenNest web service: accepts nesting jobs over HTTP, runs them with the `opennest_job` worker,
//! bills accounts in credits, and publishes each result as JSON and as a session_viewer scene.
//! Configuration is environment-only; see README.md.

mod api;
mod billing;
mod config;
mod db;
mod preview;
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

#[tokio::main]
async fn main() {
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
        Ok(n) => tracing::warn!("closed and refunded {n} job(s) interrupted by the last shutdown"),
        Err(e) => tracing::error!("recovery: {e}"),
    }
    if cfg.admin_token.is_none() {
        tracing::warn!("OPENNEST_ADMIN_TOKEN is not set: the admin endpoints are disabled");
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

    // Bearer-token auth, no cookies: any origin may call the API (the docs site, session_viewer, scripts).
    let cors = CorsLayer::new().allow_origin(Any).allow_methods(Any).allow_headers(Any);
    let app = Router::new()
        .route("/healthz", get(api::health))
        .route("/api/info", get(api::info))
        .route("/api/signup", post(api::signup))
        .route("/api/me", get(api::me))
        .route("/api/jobs", post(api::submit).get(api::list_jobs))
        .route("/api/jobs/{id}", get(api::get_job))
        .route("/api/jobs/{id}/cancel", post(api::cancel))
        .route("/api/jobs/{id}/events", get(api::events))
        .route("/api/jobs/{id}/result", get(api::result))
        .route("/api/jobs/{id}/{file}", get(api::job_file))
        .route("/live/{token}/view.yaml", get(api::latest_manifest))
        .route("/api/admin/accounts", post(api::admin_create_account))
        .route("/api/admin/accounts/{id}/credits", post(api::admin_credits))
        .route("/api/admin/jobs", get(api::admin_jobs))
        .route("/api/webhooks/paddle", post(billing::paddle_webhook))
        .fallback_service(ServeDir::new(&cfg.web_dir))
        .layer(RequestBodyLimitLayer::new(cfg.max_body_bytes))
        .layer(cors)
        .with_state(state.clone());

    let listener = tokio::net::TcpListener::bind(&cfg.bind).await.expect("cannot bind");
    let server = axum::serve(listener, app.into_make_service_with_connect_info::<SocketAddr>())
        .with_graceful_shutdown(async {
            let _ = tokio::signal::ctrl_c().await;
        });
    if let Err(e) = server.await {
        tracing::error!("server: {e}");
    }
}
