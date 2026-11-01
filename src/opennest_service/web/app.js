// OpenNest online: upload a session .pb, nest it, watch and open the result in session_viewer.
"use strict";

const $ = (id) => document.getElementById(id);
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
};
const FIT_NAMES = { 0: "sheet by sheet", 1: "one sheet", 2: "global bin pack", 3: "group-and-fill" };
let CURRENT = null;   // the job record being followed
let SOURCE = null;    // EventSource
let UNLOCKED = false;

function msg(text, bad = false) {
  $("log").hidden = false;
  const li = document.createElement("li");
  li.textContent = text;
  if (bad) li.className = "bad";
  $("messages").prepend(li);
}

function password() { return ($("password").value || "").trim(); }

async function api(path, opts = {}) {
  const headers = { ...(opts.headers || {}) };
  if (password()) headers.Authorization = "Bearer " + password();
  const r = await fetch(path, { ...opts, headers });
  const text = await r.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
  if (!r.ok) throw new Error((body && body.error) || r.statusText);
  return body;
}

async function unlock() {
  try {
    const r = await api("/api/login", { method: "POST" });
    UNLOCKED = true;
    store.set("opennest_pw", password());
    $("loginCard").hidden = true; $("app").hidden = false;
    $("latestViewer").href = r.latest_viewer; $("latestViewer").hidden = false;
    refreshJobs();
  } catch (e) { $("loginInfo").textContent = e.message; UNLOCKED = false; }
}

function showFrame(rec) {
  const frame = $("jobFrame");
  if (frame.src !== rec.links.viewer) frame.src = rec.links.viewer;
  frame.hidden = false;
}

async function run() {
  const file = $("file").files[0];
  if (!file) { msg("Choose a session .pb file first", true); return; }
  const q = new URLSearchParams({
    spacing: $("spacing").value || "0", margin: $("margin").value || "0", rotations: $("rotations").value,
    fit_mode: $("fitMode").value, wall: $("wall").value, seed: $("seed").value || "0", name: file.name,
  });
  $("run").disabled = true; $("resultLinks").hidden = true;
  $("progress").hidden = false; $("barFill").style.width = "0"; $("status").textContent = "Uploading…";
  let rec;
  try {
    rec = await api("/api/jobs?" + q, { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: await file.arrayBuffer() });
  } catch (e) { msg("Submit failed: " + e.message, true); $("run").disabled = false; $("progress").hidden = true; return; }
  for (const w of rec.warnings || []) msg(w, true);
  const s = rec.summary || {};
  msg(`${file.name}: ${s.parts} part(s), ${s.sheets} sheet(s), spacing ${s.spacing}, margin ${s.margin}`);
  CURRENT = rec;
  $("cancel").hidden = false;
  $("status").textContent = rec.queue_position ? `Queued (${rec.queue_position} ahead)…` : "Starting…";
  showFrame(rec);
  refreshJobs();
  follow(rec);
}

function follow(rec) {
  if (SOURCE) SOURCE.close();
  SOURCE = new EventSource(rec.links.events);
  const cap = rec.wall_cap;
  SOURCE.addEventListener("progress", (e) => {
    const p = JSON.parse(e.data);
    $("barFill").style.width = Math.min(100, (100 * p.t) / cap) + "%";
    $("status").textContent = `Running ${p.stage || ""} · ${p.t.toFixed(0)} s of at most ${cap.toFixed(0)} s · ${p.rounds.toLocaleString()} rounds` +
      (p.placed ? ` · ${p.placed} placed on ${p.n_sheets} sheet(s) so far` : "");
  });
  SOURCE.addEventListener("cap", () => { $("status").textContent = "Time budget reached, keeping the best layout…"; });
  SOURCE.addEventListener("status", (e) => {
    SOURCE.close(); SOURCE = null;
    const j = JSON.parse(e.data);
    $("cancel").hidden = true; $("run").disabled = false; $("barFill").style.width = "100%";
    refreshJobs();
    if (j.status === "failed") { $("status").textContent = "Failed: " + (j.error || "unknown error"); msg("Job failed: " + (j.error || ""), true); return; }
    showResult(j);
  });
}

function showResult(j) {
  CURRENT = j;
  const utils = (j.sheets || []).map((s) => (100 * s.utilization).toFixed(1) + "%").join(", ");
  const unplaced = !j.unplaced ? "" : j.status === "cancelled" ? ` · ${j.unplaced} part(s) not placed yet when it stopped` : ` · ${j.unplaced} part(s) did not fit`;
  $("status").textContent = (j.status === "cancelled" ? "stopped at the time budget" : j.status) + ` · ${j.n_sheets} sheet(s)` +
    (utils ? ` · ${utils}` : "") + unplaced + ` · ${(j.wall_s || 0).toFixed(0)} s` + (j.note ? ` · ${j.note}` : "");
  $("openViewer").href = j.links.viewer;
  $("dlPb").href = j.links.result_pb;
  $("dlJson").href = j.links.result;
  $("dlLog").href = `/api/jobs/${j.id}/solver.log`;
  $("resultLinks").hidden = false;
  $("progress").hidden = false; $("barFill").style.width = "100%";
  showFrame(j);
}

async function openJob(id) {
  try { showResult(await api(`/api/jobs/${id}`)); } catch (e) { msg("Could not open the job: " + e.message, true); }
}

async function cancelJob() {
  if (!CURRENT) return;
  try { await api(`/api/jobs/${CURRENT.id}/cancel`, { method: "POST" }); $("status").textContent = "Cancelling, keeping the best layout so far…"; }
  catch (e) { msg("Cancel failed: " + e.message, true); }
}

async function refreshJobs() {
  if (!UNLOCKED) return;
  try {
    const r = await api("/api/jobs");
    const tb = $("jobs").tBodies[0]; tb.innerHTML = "";
    for (const j of r.jobs.slice(0, 30)) {
      const tr = document.createElement("tr");
      const cells = [
        new Date(j.created * 1000).toLocaleString(), j.name || "(no name)", `${j.part_copies} parts`, FIT_NAMES[j.fit_mode] || j.fit_mode,
        j.status + (j.n_sheets != null ? ` · ${j.n_sheets} sheet(s)` : "") + (j.unplaced ? ` · ${j.unplaced} unplaced` : "") + (j.wall_s ? ` · ${Math.round(j.wall_s)} s` : ""),
      ];
      cells.forEach((t, i) => { const td = document.createElement("td"); td.textContent = t; if (i === 1) td.className = "name"; tr.appendChild(td); });
      const td = document.createElement("td");
      const a = document.createElement("a"); a.href = "#";
      if (["queued", "running"].includes(j.status)) {
        a.textContent = "follow";
        a.onclick = (ev) => { ev.preventDefault(); CURRENT = j; $("progress").hidden = false; $("cancel").hidden = false; showFrame(j); follow(j); };
      } else {
        a.textContent = "show";
        a.onclick = (ev) => { ev.preventDefault(); openJob(j.id); };
      }
      td.appendChild(a); tr.appendChild(td); tb.appendChild(tr);
    }
  } catch (e) { msg("Could not list jobs: " + e.message, true); }
}

async function init() {
  $("password").value = store.get("opennest_pw") || "";
  $("login").onclick = unlock;
  $("password").addEventListener("keydown", (e) => { if (e.key === "Enter") unlock(); });
  $("file").onchange = () => { $("run").disabled = !$("file").files.length; };
  $("run").onclick = run;
  $("cancel").onclick = cancelJob;
  try {
    const info = await api("/api/info");
    for (const o of $("wall").options) if (+o.value > info.max_wall_seconds) o.disabled = true;
  } catch (e) { msg("Service not reachable: " + e.message, true); }
  if (password()) unlock();
}
init();
