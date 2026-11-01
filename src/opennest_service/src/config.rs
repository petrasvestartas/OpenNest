//! Service configuration, read once from the environment (see README.md for every variable).

use std::path::PathBuf;

#[derive(Clone, Debug)]
pub struct Tier {
    pub name: &'static str,
    pub wall_seconds: f64,     // hard wall-clock cap per job
    pub billed: bool,          // false = free (rate limited instead)
    pub default_fit_mode: i64, // used when the job does not set params.fit_mode
    pub max_part_copies: usize,
    pub description: &'static str,
}

pub const TIERS: &[Tier] = &[
    Tier {
        name: "preview",
        wall_seconds: 30.0,
        billed: false,
        default_fit_mode: 0,
        max_part_copies: 300,
        description: "Free quick look: 30 s, fewest-sheets mode",
    },
    Tier {
        name: "standard",
        wall_seconds: 300.0,
        billed: true,
        default_fit_mode: 0,
        max_part_copies: 5000,
        description: "Up to 5 minutes, fewest-sheets mode",
    },
    Tier {
        name: "deep",
        wall_seconds: 1800.0,
        billed: true,
        default_fit_mode: 3,
        max_part_copies: 5000,
        description: "Up to 30 minutes, group-and-fill mode (best sheet count)",
    },
];

pub fn tier(name: &str) -> Option<&'static Tier> {
    TIERS.iter().find(|t| t.name == name)
}

#[derive(Clone, Debug)]
pub struct Config {
    pub bind: String,
    pub data_dir: PathBuf,
    pub job_bin: PathBuf,
    pub web_dir: PathBuf,
    pub slots: usize,            // jobs running at the same time
    pub threads_per_job: i64,    // solver threads per job (fit_mode 3 portfolio, NP_WORKERS)
    pub public_url: String,      // how browsers reach this service, no trailing slash
    pub viewer_url: String,      // session_viewer page
    pub admin_token: Option<String>,
    pub signup_credits: f64,     // credit-seconds a new account starts with
    pub anonymous_preview: bool, // allow preview jobs without an account
    pub trust_proxy: bool,       // take the client IP from X-Forwarded-For (behind Caddy / nginx)
    pub paddle_secret: Option<String>,
    pub paddle_client_token: Option<String>,
    pub paddle_prices: Vec<(String, f64)>, // price id -> credit-seconds per unit
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
        // resolve_n_workers). Packing quality grows with the threads a job gets, so more slots buy
        // throughput at the cost of weaker layouts per job.
        let slots: usize = env_parse("OPENNEST_SLOTS", 1usize).max(1);
        let threads_per_job: i64 = env_parse("OPENNEST_THREADS_PER_JOB", ((cores * 3 / 4) / slots).max(1) as i64);
        let bind = env("OPENNEST_BIND").unwrap_or_else(|| "127.0.0.1:8080".to_string());
        let public_url = env("OPENNEST_PUBLIC_URL")
            .unwrap_or_else(|| format!("http://{}", bind.replace("0.0.0.0", "localhost")))
            .trim_end_matches('/')
            .to_string();
        let mut paddle_prices = Vec::new();
        if let Some(spec) = env("OPENNEST_PADDLE_PRICES") {
            for item in spec.split(',') {
                let (id, secs) = item
                    .split_once('=')
                    .ok_or_else(|| format!("OPENNEST_PADDLE_PRICES: expected price_id=seconds, got '{item}'"))?;
                let secs: f64 = secs.trim().parse().map_err(|_| format!("OPENNEST_PADDLE_PRICES: bad seconds in '{item}'"))?;
                paddle_prices.push((id.trim().to_string(), secs));
            }
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
            admin_token: env("OPENNEST_ADMIN_TOKEN"),
            signup_credits: env_parse("OPENNEST_SIGNUP_CREDITS", 600.0),
            anonymous_preview: env_parse("OPENNEST_ANON_PREVIEW", true),
            trust_proxy: env_parse("OPENNEST_TRUST_PROXY", false),
            paddle_secret: env("OPENNEST_PADDLE_SECRET"),
            paddle_client_token: env("OPENNEST_PADDLE_CLIENT_TOKEN"),
            paddle_prices,
            max_body_bytes: env_parse("OPENNEST_MAX_BODY_MB", 32usize) * 1024 * 1024,
        };
        if !cfg.job_bin.exists() {
            return Err(format!(
                "opennest_job binary not found at {} (set OPENNEST_JOB_BIN)",
                cfg.job_bin.display()
            ));
        }
        Ok(cfg)
    }
}
