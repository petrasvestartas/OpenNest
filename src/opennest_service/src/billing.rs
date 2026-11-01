//! Credit top-ups from Paddle Billing webhooks.
//!
//! Paddle signs each delivery: header `Paddle-Signature: ts=<unix>;h1=<hex>` where
//! h1 = HMAC-SHA256(endpoint secret, "<ts>:<raw body>"). On `transaction.completed` the account named in
//! the checkout's `custom_data.account_id` gets, per line item, quantity x the credit-seconds configured
//! for that price id (OPENNEST_PADDLE_PRICES). Event ids are recorded, so a retried delivery is applied once.
//!
//! NOT YET EXERCISED AGAINST PADDLE: verify with Paddle's sandbox and its webhook simulator before taking
//! real payments.

use crate::api::ApiError;
use crate::db;
use crate::AppState;
use axum::body::Bytes;
use axum::extract::State;
use axum::http::{HeaderMap, StatusCode};
use axum::Json;
use hmac::{Hmac, Mac};
use serde_json::{json, Value};
use sha2::Sha256;
use std::sync::Arc;

/// Constant-time comparison.
pub fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

pub fn verify_paddle(secret: &str, header: &str, body: &[u8], now: i64) -> Result<(), &'static str> {
    let mut ts = None;
    let mut sigs = Vec::new();
    for part in header.split(';') {
        match part.split_once('=') {
            Some(("ts", v)) => ts = Some(v.trim()),
            Some(("h1", v)) => sigs.push(v.trim()),
            _ => {}
        }
    }
    let ts = ts.ok_or("signature without ts")?;
    let t: i64 = ts.parse().map_err(|_| "bad ts")?;
    if (now - t).abs() > 300 {
        return Err("signature timestamp outside the 5 minute window");
    }
    let mut mac = Hmac::<Sha256>::new_from_slice(secret.as_bytes()).map_err(|_| "bad secret")?;
    mac.update(ts.as_bytes());
    mac.update(b":");
    mac.update(body);
    let want = hex::encode(mac.finalize().into_bytes());
    if sigs.iter().any(|s| ct_eq(s.as_bytes(), want.as_bytes())) {
        Ok(())
    } else {
        Err("signature mismatch")
    }
}

pub async fn paddle_webhook(State(state): State<Arc<AppState>>, headers: HeaderMap, body: Bytes)
    -> Result<Json<Value>, ApiError> {
    let secret = state
        .cfg
        .paddle_secret
        .as_deref()
        .ok_or_else(|| ApiError(StatusCode::NOT_FOUND, "payments are not configured".into()))?;
    let sig = headers.get("paddle-signature").and_then(|v| v.to_str().ok()).unwrap_or("");
    verify_paddle(secret, sig, &body, db::now()).map_err(|e| ApiError(StatusCode::UNAUTHORIZED, e.into()))?;
    let ev: Value = serde_json::from_slice(&body).map_err(|_| ApiError(StatusCode::BAD_REQUEST, "invalid JSON".into()))?;
    let event_id = ev["event_id"].as_str().unwrap_or("").to_string();
    if ev["event_type"] != "transaction.completed" || event_id.is_empty() {
        return Ok(Json(json!({"ignored": ev["event_type"]})));
    }
    let data = &ev["data"];
    let account_id = data["custom_data"]["account_id"].as_str().unwrap_or("").to_string();
    let mut credits = 0.0;
    for item in data["items"].as_array().cloned().unwrap_or_default() {
        let price = item["price"]["id"].as_str().or_else(|| item["price_id"].as_str()).unwrap_or("");
        let qty = item["quantity"].as_f64().unwrap_or(1.0);
        if let Some((_, secs)) = state.cfg.paddle_prices.iter().find(|(id, _)| id == price) {
            credits += secs * qty;
        }
    }
    let c = state.db.lock().await;
    if !db::first_webhook(&c, &event_id)? {
        return Ok(Json(json!({"duplicate": event_id})));
    }
    if db::account_by_id(&c, &account_id)?.is_none() {
        tracing::error!(event = %event_id, account = %account_id, "paid transaction for an unknown account: credit by hand");
        return Ok(Json(json!({"error": "unknown account; recorded for manual review"})));
    }
    if credits > 0.0 {
        let txn = data["id"].as_str().unwrap_or("");
        db::add_credits(&c, &account_id, credits, &format!("paddle {txn}"), None)?;
    }
    tracing::info!(event = %event_id, account = %account_id, credits, "top-up");
    Ok(Json(json!({"credited": credits})))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paddle_signature_roundtrip() {
        let body = br#"{"event_id":"evt_1"}"#;
        let mut mac = Hmac::<Sha256>::new_from_slice(b"pdl_ntfset_secret").unwrap();
        mac.update(b"1700000000:");
        mac.update(body);
        let h1 = hex::encode(mac.finalize().into_bytes());
        let header = format!("ts=1700000000;h1={h1}");
        assert!(verify_paddle("pdl_ntfset_secret", &header, body, 1700000010).is_ok());
        assert!(verify_paddle("pdl_ntfset_secret", &header, b"tampered", 1700000010).is_err());
        assert!(verify_paddle("wrong", &header, body, 1700000010).is_err());
        assert!(verify_paddle("pdl_ntfset_secret", &header, body, 1700009999).is_err());
    }
}
