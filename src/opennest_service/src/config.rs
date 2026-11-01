//! Service configuration, read once from the environment (see README.md).

use std::path::PathBuf;

#[derive(Clone, Debug)]
pub struct Config {
    pub bind: String,
    pub data_dir: PathBuf,
    pub job_bin: PathBuf,
    pub web_dir: PathBuf,
    pub slots: usize,          // jobs running at the same time
    pub threads_per_job: i64,  // solver threads per job (NP_WORKERS and params.n_threads)
    pub public_url: String,    // how browsers reach this service, no trailing slash
    pub viewer_url: String,    // session_viewer page
    pub password: String,      // the one secret that unlocks the page and the API
    pub max_wall_seconds: f64, // the longest run a job may ask for
    pub default_wall_seconds: f64,
    pub trust_proxy: bool,     // take the client IP from X-Forwarded-For (behind Caddy / nginx)
    pub max_body_bytes: usize,
}

fn env(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|v| !v.trim().is_empty())
}

fn env_parse<T: std::str::FromStr>(name: &str, default: T) -> T {
    env(name).and_then(|v| v.parse().ok()).unwrap_or(default)
}

impl Config {
    pub fn from_env() -> Result<Self, String> {
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.to_path_buf()))
            .unwrap_or_else(|| PathBuf::from("."));
        let default_bin = exe_dir.join(if cfg!(windows) { "opennest_job.exe" } else { "opennest_job" });
        let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
        // Default: ONE job at a time on ~75% of the cores, the solver's own sweet spot (driver.hpp
        // resolve_n_workers). Packing quality grows with the threads a job gets.
        let slots: usize = env_parse("OPENNEST_SLOTS", 1usize).max(1);
        let threads_per_job: i64 = env_parse("OPENNEST_THREADS_PER_JOB", ((cores * 3 / 4) / slots).max(1) as i64);
        let bind = env("OPENNEST_BIND").unwrap_or_else(|| "127.0.0.1:8080".to_string());
        let public_url = env("OPENNEST_PUBLIC_URL")
            .unwrap_or_else(|| format!("http://{}", bind.replace("0.0.0.0", "localhost")))
            .trim_end_matches('/')
            .to_string();
        let password = env("OPENNEST_PASSWORD").ok_or("OPENNEST_PASSWORD is not set (any long random string)")?;
        if password.len() < 8 {
            return Err("OPENNEST_PASSWORD must be at least 8 characters".into());
        }
        let cfg = Config {
            bind,
            data_dir: PathBuf::from(env("OPENNEST_DATA").unwrap_or_else(|| "data".into())),
            job_bin: env("OPENNEST_JOB_BIN").map(PathBuf::from).unwrap_or(default_bin),
            web_dir: env("OPENNEST_WEB").map(PathBuf::from).unwrap_or_else(|| PathBuf::from("web")),
            slots,
            threads_per_job: threads_per_job.clamp(1, 256),
            public_url,
            viewer_url: env("OPENNEST_VIEWER_URL").unwrap_or_else(|| "https://petrasvestartas.github.io/session/".into()),
            password,
            max_wall_seconds: env_parse("OPENNEST_MAX_WALL", 3600.0_f64).max(10.0),
            default_wall_seconds: env_parse("OPENNEST_DEFAULT_WALL", 600.0_f64).max(10.0),
            trust_proxy: env_parse("OPENNEST_TRUST_PROXY", false),
            max_body_bytes: env_parse("OPENNEST_MAX_BODY_MB", 64usize) * 1024 * 1024,
        };
        if !cfg.job_bin.exists() {
            return Err(format!("opennest_job binary not found at {} (set OPENNEST_JOB_BIN)", cfg.job_bin.display()));
        }
        Ok(cfg)
    }
}
