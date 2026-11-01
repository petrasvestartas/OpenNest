// Runs the web page's DXF parser, part builder, result layout and DXF export in Node against a live
// service, without a browser:   node src/opennest_service/tests/web_parsers.mjs [http://localhost:8080]
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const base = process.argv[2] || "http://localhost:8080";
let fails = 0;
const check = (ok, what) => { console.log((ok ? "  ok    " : "  FAIL  ") + what); if (!ok) fails++; };

// Minimal DOM stand-ins: the page script only needs them for init() and downloads.
const el = () => new Proxy({ style: {}, files: [], options: [], value: "", append() {}, appendChild() {}, prepend() {}, setAttribute() {}, addEventListener() {} },
  { get: (t, k) => (k in t ? t[k] : () => {}), set: (t, k, v) => ((t[k] = v), true) });
let downloaded = null;
const ctx = {
  console, Math, JSON, Map, Set, Array, Object, Number, String, Promise, setTimeout, Blob,
  localStorage: { getItem: () => null, setItem() {} },
  fetch: (u, o) => fetch(new URL(u, base), o),
  URL: { createObjectURL: (b) => { downloaded = b; return "blob:x"; }, revokeObjectURL() {} },
  document: { getElementById: el, createElement: el, createElementNS: el, head: el(), body: el() },
  window: {},
};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(here, "..", "web", "app.js"), "utf8"), ctx);

const dxf = [
  // 1) 200 x 100 plate (LWPOLYLINE) with a circular hole r=20 (CIRCLE) and a bulged slot hole
  "0", "SECTION", "2", "ENTITIES",
  "0", "LWPOLYLINE", "8", "0", "90", "4", "70", "1", "10", "0", "20", "0", "10", "200", "20", "0", "10", "200", "20", "100", "10", "0", "20", "100",
  "0", "CIRCLE", "8", "0", "10", "50", "20", "50", "40", "20",
  "0", "LWPOLYLINE", "8", "0", "90", "4", "70", "1",
  "10", "120", "20", "40", "42", "0", "10", "160", "20", "40", "42", "1", "10", "160", "20", "60", "42", "0", "10", "120", "20", "60", "42", "1",
  // 2) a stadium made of 2 LINEs and 2 ARCs (chained into one loop)
  "0", "LINE", "8", "0", "10", "300", "20", "0", "11", "400", "21", "0",
  "0", "ARC", "8", "0", "10", "400", "20", "25", "40", "25", "50", "270", "51", "90",
  "0", "LINE", "8", "0", "10", "400", "20", "50", "11", "300", "21", "50",
  "0", "ARC", "8", "0", "10", "300", "20", "25", "40", "25", "50", "90", "51", "270",
  // 3) a part lying inside the plate's round hole (depth 2 => its own part)
  "0", "LWPOLYLINE", "8", "0", "90", "3", "70", "1", "10", "45", "20", "45", "10", "55", "20", "45", "10", "50", "20", "55",
  "0", "SPLINE", "8", "0",
  "0", "ENDSEC", "0", "EOF", "",
].join("\n");

console.log("1. DXF parsing");
const parsed = ctx.parseDxf(dxf, 0.01);
check(parsed.skipped === 1, `SPLINE reported as skipped (${parsed.skipped})`);
const parts = ctx.ringsToParts(parsed.rings, "t");
check(parts.length === 3, `3 parts: plate, stadium, triangle in the hole (${parts.length})`);
const plate = parts.find((p) => Math.abs(ctx.area(p.outer)) > 19000);
check(plate && plate.holes.length === 2, `plate has 2 holes (${plate && plate.holes.length})`);
const circle = plate.holes.map((h) => Math.abs(ctx.area(h))).sort((a, b) => b - a);
check(Math.abs(circle[0] - Math.PI * 400) / (Math.PI * 400) < 0.002, `round hole area ${circle[0].toFixed(1)} ~ ${(Math.PI * 400).toFixed(1)}`);
const slot = 40 * 20 + Math.PI * 100;      // 40 straight + two r=10 half circles
check(Math.abs(circle[1] - slot) / slot < 0.003, `bulged slot area ${circle[1].toFixed(1)} ~ ${slot.toFixed(1)}`);
const stadium = parts.find((p) => { const b = ctx.bbox([p.outer]); return b[0] > 250; });
const sa = 100 * 50 + Math.PI * 625;
check(stadium && Math.abs(Math.abs(ctx.area(stadium.outer)) - sa) / sa < 0.002, `LINE+ARC loop chained, area ${stadium && Math.abs(ctx.area(stadium.outer)).toFixed(1)} ~ ${sa.toFixed(1)}`);

console.log("2. nest it on the service (free preview)");
const job = {
  schema: "opennest.job/1", units: "mm", spacing: 4, margin: 6,
  parts: parts.map((p, i) => ({ id: p.name, qty: 3, rotations: 0, outer: p.outer, holes: p.holes })),
  sheets: [{ id: "s", count: 3, outer: [[0, 0], [600, 0], [600, 300], [0, 300]] }],
  params: { iter_mode: 1, iter_budget: 2000, num_rotations: 360, seed: 1 },
};
const sub = await (await fetch(`${base}/api/jobs?tier=preview`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(job) })).json();
check(!!sub.id, `submitted ${sub.id || JSON.stringify(sub)}`);
let rec;
for (let k = 0; k < 120; k++) {
  rec = await (await fetch(`${base}/api/jobs/${sub.id}`)).json();
  if (!["queued", "running"].includes(rec.status)) break;
  await new Promise((r) => setTimeout(r, 1000));
}
check(rec.status === "ok", `finished ${rec.status} on ${rec.n_sheets} sheet(s), ${rec.unplaced} unplaced`);
const result = await (await fetch(`${base}/api/jobs/${sub.id}/result`)).json();
fs.writeFileSync(path.join(process.env.TEMP || "/tmp", "web_parsers_job.json"), JSON.stringify(job));
fs.writeFileSync(path.join(process.env.TEMP || "/tmp", "web_parsers_result.json"), JSON.stringify(result));

console.log("3. page layout and DXF export");
const L = ctx.layoutResult(job, result);
check(L.sheets.length === result.sheets_used.length && L.unplaced === 0, `${L.sheets.length} sheet(s) laid out, 0 unplaced`);
const placedArea = parts.reduce((s, p) => s + 3 * (Math.abs(ctx.area(p.outer)) - p.holes.reduce((t, h) => t + Math.abs(ctx.area(h)), 0)), 0);
const utilArea = L.sheets.reduce((s, sh) => s + sh.util * 600 * 300, 0);
check(Math.abs(utilArea - placedArea) / placedArea < 1e-6, `utilisation accounts for all part area (${utilArea.toFixed(0)} = ${placedArea.toFixed(0)})`);
vm.runInContext("CURRENT = { job: " + JSON.stringify(job) + ", result: " + JSON.stringify(result) + " }", ctx);
ctx.exportDxf();
const text = downloaded ? await downloaded.text() : "";
const back = ctx.parseDxf(text, 0.01);
const expectRings = L.sheets.reduce((s, sh) => s + sh.rings.length, 0);
check(back.rings.length === expectRings, `exported DXF reads back: ${back.rings.length} closed rings = ${expectRings}`);
console.log(`\nresult files for the clearance check: ${process.env.TEMP || "/tmp"}/web_parsers_{job,result}.json`);
console.log(fails ? `FAIL: ${fails}` : "PASS");
process.exit(fails ? 1 : 0);
