// OpenNest online: load DXF / SVG / job JSON, submit to the service, follow progress, show the result.
"use strict";

const $ = (id) => document.getElementById(id);
const API = "";            // same origin as this page
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
};

let INFO = null;            // /api/info
let PARTS = [];             // [{name, outer:[[x,y]], holes:[[[x,y]]]}]
let LOADED_JOB = null;      // a whole opennest.job/1 loaded from JSON
let CURRENT = null;         // {job, jobRecord, result}
let SOURCE = null;          // EventSource

function msg(text, bad = false) {
  $("log").hidden = false;
  const li = document.createElement("li");
  li.textContent = text;
  if (bad) li.className = "bad";
  $("messages").prepend(li);
}

function key() { return ($("apiKey").value || "").trim(); }
function authHeaders() { return key() ? { Authorization: "Bearer " + key() } : {}; }

async function api(path, opts = {}) {
  const r = await fetch(API + path, { ...opts, headers: { ...(opts.headers || {}), ...authHeaders() } });
  const text = await r.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
  if (!r.ok) throw new Error((body && body.error) || r.statusText);
  return body;
}

// ------------------------------------------------------------------------------------------------
// geometry helpers
// ------------------------------------------------------------------------------------------------
function area(r) {
  let a = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) a += r[j][0] * r[i][1] - r[i][0] * r[j][1];
  return a / 2;
}
function bbox(rs) {
  const b = [Infinity, Infinity, -Infinity, -Infinity];
  for (const r of rs) for (const [x, y] of r) {
    if (x < b[0]) b[0] = x; if (y < b[1]) b[1] = y; if (x > b[2]) b[2] = x; if (y > b[3]) b[3] = y;
  }
  return b;
}
function inside(pt, r) {
  let c = false;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
    const [xi, yi] = r[i], [xj, yj] = r[j];
    if ((yi > pt[1]) !== (yj > pt[1]) && pt[0] < ((xj - xi) * (pt[1] - yi)) / (yj - yi) + xi) c = !c;
  }
  return c;
}
function clean(r) {
  const out = [];
  for (const p of r) {
    const q = out[out.length - 1];
    if (!q || Math.hypot(p[0] - q[0], p[1] - q[1]) > 1e-9) out.push(p);
  }
  if (out.length > 1) {
    const a = out[0], z = out[out.length - 1];
    if (Math.hypot(a[0] - z[0], a[1] - z[1]) < 1e-9) out.pop();
  }
  return out;
}
// Closed rings -> parts. Even depth = a part outline, odd depth = a hole of its parent.
function ringsToParts(rings, baseName) {
  rings = rings.map(clean).filter((r) => r.length >= 3 && Math.abs(area(r)) > 1e-9);
  const items = rings.map((r) => ({ r, a: Math.abs(area(r)), parent: -1, depth: 0 }));
  const order = items.map((_, i) => i).sort((i, j) => items[j].a - items[i].a);
  for (let k = 0; k < order.length; k++) {
    const it = items[order[k]];
    for (let m = k - 1; m >= 0; m--) {            // smallest larger ring that contains it
      const cand = items[order[m]];
      if (cand.a > it.a && inside(it.r[0], cand.r) && inside(it.r[Math.floor(it.r.length / 2)], cand.r)) {
        it.parent = order[m]; it.depth = cand.depth + 1; break;
      }
    }
  }
  const parts = [];
  const byIndex = new Map();
  items.forEach((it, i) => {
    if (it.depth % 2 === 0) { const p = { name: `${baseName} ${parts.length + 1}`, outer: it.r, holes: [] }; byIndex.set(i, p); parts.push(p); }
  });
  items.forEach((it) => { if (it.depth % 2 === 1 && byIndex.has(it.parent)) byIndex.get(it.parent).holes.push(it.r); });
  return parts;
}
function arcPoints(cx, cy, r, a0, a1, tol) {   // radians, ccw from a0 to a1
  let sweep = a1 - a0;
  const n = Math.max(2, Math.ceil(Math.abs(sweep) / (2 * Math.acos(Math.max(-1, 1 - tol / Math.max(r, tol))))));
  const out = [];
  for (let i = 0; i <= n; i++) { const t = a0 + (sweep * i) / n; out.push([cx + r * Math.cos(t), cy + r * Math.sin(t)]); }
  return out;
}

// ------------------------------------------------------------------------------------------------
// DXF
// ------------------------------------------------------------------------------------------------
function parseDxf(text, tol) {
  const lines = text.split(/\r?\n/);
  const pairs = [];
  for (let i = 0; i + 1 < lines.length; i += 2) pairs.push([parseInt(lines[i].trim(), 10), lines[i + 1].trim()]);
  let i = pairs.findIndex((p, k) => p[0] === 2 && p[1] === "ENTITIES" && pairs[k - 1] && pairs[k - 1][1] === "SECTION");
  if (i < 0) i = 0;
  const closed = [], open = [];
  let skipped = 0;
  const entity = () => {             // read the codes of one entity starting at pairs[i] (code 0)
    const type = pairs[i][1]; const codes = []; i++;
    while (i < pairs.length && pairs[i][0] !== 0) { codes.push(pairs[i]); i++; }
    return { type, codes };
  };
  const bulgePts = (pts, bulges, isClosed) => {
    const out = [];
    const n = pts.length;
    for (let k = 0; k < n; k++) {
      const p = pts[k];
      if (!isClosed && k === n - 1) { out.push(p); break; }
      const q = pts[(k + 1) % n];
      const b = bulges[k] || 0;
      if (Math.abs(b) < 1e-12) { out.push(p); continue; }
      const theta = 4 * Math.atan(b);                       // included angle, sign = direction
      const d = Math.hypot(q[0] - p[0], q[1] - p[1]);
      const r = d / (2 * Math.sin(Math.abs(theta) / 2));
      const mx = (p[0] + q[0]) / 2, my = (p[1] + q[1]) / 2;
      const h = Math.sqrt(Math.max(0, r * r - (d / 2) ** 2)) * (Math.abs(theta) > Math.PI ? -1 : 1);
      const ux = -(q[1] - p[1]) / d, uy = (q[0] - p[0]) / d;      // left normal of p->q
      const s = b > 0 ? 1 : -1;
      const cx = mx + s * h * ux, cy = my + s * h * uy;
      const a0 = Math.atan2(p[1] - cy, p[0] - cx);
      const arc = arcPoints(cx, cy, r, a0, a0 + theta, tol);
      arc.pop();
      out.push(...arc);
    }
    return out;
  };
  while (i < pairs.length) {
    if (pairs[i][0] !== 0) { i++; continue; }
    const t = pairs[i][1];
    if (t === "ENDSEC" || t === "EOF") break;
    if (t === "POLYLINE") {
      const head = entity();
      const flags = +(head.codes.find((c) => c[0] === 70) || [0, 0])[1];
      const pts = [], bulges = [];
      while (i < pairs.length && pairs[i][1] === "VERTEX") {
        const v = entity();
        const g = (c) => +((v.codes.find((x) => x[0] === c) || [0, 0])[1]);
        pts.push([g(10), g(20)]); bulges.push(g(42));
      }
      if (i < pairs.length && pairs[i][1] === "SEQEND") entity();
      const ring = bulgePts(pts, bulges, (flags & 1) === 1);
      ((flags & 1) === 1 ? closed : open).push(ring);
      continue;
    }
    const e = entity();
    const g = (c) => +((e.codes.find((x) => x[0] === c) || [0, 0])[1]);
    if (e.type === "LWPOLYLINE") {
      const pts = [], bulges = [];
      for (const [c, v] of e.codes) {
        if (c === 10) { pts.push([+v, 0]); bulges.push(0); }
        else if (c === 20 && pts.length) pts[pts.length - 1][1] = +v;
        else if (c === 42 && pts.length) bulges[bulges.length - 1] = +v;
      }
      const isClosed = (g(70) & 1) === 1;
      (isClosed ? closed : open).push(bulgePts(pts, bulges, isClosed));
    } else if (e.type === "CIRCLE") {
      const c = arcPoints(g(10), g(20), g(40), 0, 2 * Math.PI, tol); c.pop(); closed.push(c);
    } else if (e.type === "ELLIPSE") {
      const cx = g(10), cy = g(20), mx = g(11), my = g(21), ratio = g(40) || 1;
      const t0 = g(41), t1 = e.codes.some((c) => c[0] === 42) ? g(42) : 2 * Math.PI;
      const a = Math.hypot(mx, my), ang = Math.atan2(my, mx);
      const n = Math.max(32, Math.ceil(((t1 - t0) * a) / Math.max(tol * 20, 1e-6)));
      const pts = [];
      for (let k = 0; k <= n; k++) {
        const t = t0 + ((t1 - t0) * k) / n, x = a * Math.cos(t), y = a * ratio * Math.sin(t);
        pts.push([cx + x * Math.cos(ang) - y * Math.sin(ang), cy + x * Math.sin(ang) + y * Math.cos(ang)]);
      }
      if (Math.abs(t1 - t0 - 2 * Math.PI) < 1e-6) { pts.pop(); closed.push(pts); } else open.push(pts);
    } else if (e.type === "LINE") {
      open.push([[g(10), g(20)], [g(11), g(21)]]);
    } else if (e.type === "ARC") {
      let a0 = (g(50) * Math.PI) / 180, a1 = (g(51) * Math.PI) / 180;
      if (a1 <= a0) a1 += 2 * Math.PI;
      open.push(arcPoints(g(10), g(20), g(40), a0, a1, tol));
    } else if (["SPLINE", "INSERT", "HATCH"].includes(e.type)) {
      skipped++;
    }
  }
  closed.push(...chain(open, Math.max(tol * 5, 1e-3)));
  return { rings: closed, skipped };
}
// Join open pieces (LINE, ARC, open polylines) end to end into closed loops.
function chain(pieces, tol) {
  const loops = [];
  const left = pieces.filter((p) => p.length >= 2);
  const near = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]) <= tol;
  while (left.length) {
    let cur = left.pop().slice();
    let grew = true;
    while (grew && !near(cur[0], cur[cur.length - 1])) {
      grew = false;
      for (let k = 0; k < left.length; k++) {
        const p = left[k], end = cur[cur.length - 1];
        if (near(p[0], end)) cur = cur.concat(p.slice(1));
        else if (near(p[p.length - 1], end)) cur = cur.concat(p.slice(0, -1).reverse());
        else continue;
        left.splice(k, 1); grew = true; break;
      }
    }
    if (cur.length >= 4 && near(cur[0], cur[cur.length - 1])) { cur.pop(); loops.push(cur); }
  }
  return loops;
}

// ------------------------------------------------------------------------------------------------
// SVG (exact vertices for straight segments, curves flattened to `tol`)
// ------------------------------------------------------------------------------------------------
function flattenPath(d, tol) {
  const toks = d.match(/[a-zA-Z]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?/g) || [];
  const subs = []; let cur = null;
  let i = 0, cmd = "", x = 0, y = 0, sx = 0, sy = 0, lcx = 0, lcy = 0, lq = null;
  const num = () => parseFloat(toks[i++]);
  const push = (px, py) => { cur.pts.push([px, py]); };
  const cubic = (x1, y1, x2, y2, x3, y3) => {
    const len = Math.hypot(x1 - x, y1 - y) + Math.hypot(x2 - x1, y2 - y1) + Math.hypot(x3 - x2, y3 - y2);
    const n = Math.min(512, Math.max(4, Math.ceil(Math.sqrt(len / Math.max(tol, 1e-6)) * 2)));
    for (let k = 1; k <= n; k++) {
      const t = k / n, u = 1 - t;
      push(u * u * u * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
           u * u * u * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3);
    }
  };
  const arc = (rx, ry, phi, large, sweep, x2, y2) => {
    if (rx === 0 || ry === 0) { push(x2, y2); return; }
    rx = Math.abs(rx); ry = Math.abs(ry); phi = (phi * Math.PI) / 180;
    const c = Math.cos(phi), s = Math.sin(phi);
    const dx = (x - x2) / 2, dy = (y - y2) / 2;
    const x1p = c * dx + s * dy, y1p = -s * dx + c * dy;
    let lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
    if (lam > 1) { rx *= Math.sqrt(lam); ry *= Math.sqrt(lam); }
    const sign = large === sweep ? -1 : 1;
    const num2 = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
    const co = sign * Math.sqrt(Math.max(0, num2 / (rx * rx * y1p * y1p + ry * ry * x1p * x1p)));
    const cxp = (co * rx * y1p) / ry, cyp = (-co * ry * x1p) / rx;
    const cx = c * cxp - s * cyp + (x + x2) / 2, cy = s * cxp + c * cyp + (y + y2) / 2;
    const ang = (ux, uy, vx, vy) => Math.atan2(ux * vy - uy * vx, ux * vx + uy * vy);
    const t1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
    let dt = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
    if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
    const r = Math.max(rx, ry);
    const n = Math.max(2, Math.ceil(Math.abs(dt) / (2 * Math.acos(Math.max(-1, 1 - tol / r)))));
    for (let k = 1; k <= n; k++) {
      const t = t1 + (dt * k) / n;
      push(cx + rx * Math.cos(t) * c - ry * Math.sin(t) * s, cy + rx * Math.cos(t) * s + ry * Math.sin(t) * c);
    }
  };
  while (i < toks.length) {
    if (/[a-zA-Z]/.test(toks[i])) cmd = toks[i++];
    const rel = cmd === cmd.toLowerCase() && cmd !== "z";
    const C = cmd.toUpperCase();
    const ox = rel ? x : 0, oy = rel ? y : 0;
    if (C === "M") {
      x = num() + ox; y = num() + oy; sx = x; sy = y;
      cur = { pts: [[x, y]], closed: false }; subs.push(cur);
      cmd = rel ? "l" : "L"; lq = null; lcx = x; lcy = y; continue;
    }
    if (!cur) { cur = { pts: [[x, y]], closed: false }; subs.push(cur); }
    if (C === "Z") { cur.closed = true; x = sx; y = sy; cur = null; lcx = x; lcy = y; lq = null; continue; }
    if (C === "L") { x = num() + ox; y = num() + oy; push(x, y); lcx = x; lcy = y; lq = null; }
    else if (C === "H") { x = num() + ox; push(x, y); lcx = x; lcy = y; lq = null; }
    else if (C === "V") { y = num() + oy; push(x, y); lcx = x; lcy = y; lq = null; }
    else if (C === "C") {
      const x1 = num() + ox, y1 = num() + oy, x2 = num() + ox, y2 = num() + oy, x3 = num() + ox, y3 = num() + oy;
      cubic(x1, y1, x2, y2, x3, y3); lcx = x2; lcy = y2; x = x3; y = y3; lq = null;
    } else if (C === "S") {
      const x1 = 2 * x - lcx, y1 = 2 * y - lcy, x2 = num() + ox, y2 = num() + oy, x3 = num() + ox, y3 = num() + oy;
      cubic(x1, y1, x2, y2, x3, y3); lcx = x2; lcy = y2; x = x3; y = y3; lq = null;
    } else if (C === "Q" || C === "T") {
      let qx, qy;
      if (C === "Q") { qx = num() + ox; qy = num() + oy; } else { qx = lq ? 2 * x - lq[0] : x; qy = lq ? 2 * y - lq[1] : y; }
      const x3 = num() + ox, y3 = num() + oy;
      cubic(x + (2 / 3) * (qx - x), y + (2 / 3) * (qy - y), x3 + (2 / 3) * (qx - x3), y3 + (2 / 3) * (qy - y3), x3, y3);
      lq = [qx, qy]; x = x3; y = y3; lcx = x; lcy = y;
    } else if (C === "A") {
      const rx = num(), ry = num(), phi = num(), large = num(), sweep = num(), x2 = num() + ox, y2 = num() + oy;
      arc(rx, ry, phi, !!large, !!sweep, x2, y2); x = x2; y = y2; lcx = x; lcy = y; lq = null;
    } else { i++; continue; }
  }
  return subs;
}
function parseSvg(text, tol) {
  const doc = new DOMParser().parseFromString(text, "image/svg+xml");
  const root = doc.documentElement;
  if (root.nodeName !== "svg") throw new Error("not an SVG file");
  const host = document.createElement("div");
  host.style.cssText = "position:absolute;left:-99999px;top:0;width:0;height:0;overflow:hidden";
  const svg = document.importNode(root, true);
  host.appendChild(svg); document.body.appendChild(host);
  const rings = [];
  try {
    const base = svg.getScreenCTM().inverse();
    for (const el of svg.querySelectorAll("path,polygon,polyline,rect,circle,ellipse")) {
      if (el.closest("defs,clipPath,mask,symbol")) continue;
      const m = base.multiply(el.getScreenCTM());
      const tf = ([px, py]) => [m.a * px + m.c * py + m.e, -(m.b * px + m.d * py + m.f)];   // y up
      const tag = el.nodeName.toLowerCase();
      let subs = [];
      if (tag === "path") subs = flattenPath(el.getAttribute("d") || "", tol).filter((s) => s.closed || s.pts.length > 3 &&
        Math.hypot(s.pts[0][0] - s.pts[s.pts.length - 1][0], s.pts[0][1] - s.pts[s.pts.length - 1][1]) < tol);
      else if (tag === "polygon") subs = [{ pts: Array.from(el.points, (p) => [p.x, p.y]) }];
      else if (tag === "rect") {
        const x = el.x.baseVal.value, y = el.y.baseVal.value, w = el.width.baseVal.value, h = el.height.baseVal.value;
        subs = [{ pts: [[x, y], [x + w, y], [x + w, y + h], [x, y + h]] }];
      } else if (tag === "circle" || tag === "ellipse") {
        const cx = el.cx.baseVal.value, cy = el.cy.baseVal.value;
        const rx = tag === "circle" ? el.r.baseVal.value : el.rx.baseVal.value;
        const ry = tag === "circle" ? rx : el.ry.baseVal.value;
        const pts = arcPoints(0, 0, 1, 0, 2 * Math.PI, tol / Math.max(rx, ry)).slice(0, -1).map(([a, b]) => [cx + rx * a, cy + ry * b]);
        subs = [{ pts }];
      } else if (tag === "polyline") {
        const pts = Array.from(el.points, (p) => [p.x, p.y]);
        if (pts.length > 3 && Math.hypot(pts[0][0] - pts[pts.length - 1][0], pts[0][1] - pts[pts.length - 1][1]) < tol) subs = [{ pts }];
      }
      for (const s of subs) rings.push(s.pts.map(tf));
    }
  } finally { host.remove(); }
  return rings;
}

// ------------------------------------------------------------------------------------------------
// drawing
// ------------------------------------------------------------------------------------------------
const NS = "http://www.w3.org/2000/svg";
function hue(i) { return `hsl(${(i * 137.508) % 360} 60% 45%)`; }
function pathD(r) { return "M" + r.map((p) => p[0].toFixed(3) + "," + (-p[1]).toFixed(3)).join("L") + "Z"; }
function drawInto(svg, groups) {   // groups: [{rings:[{r, stroke, fill, w}]}] already laid out, y up
  svg.innerHTML = "";
  const all = groups.flatMap((g) => g.rings.map((x) => x.r));
  if (!all.length) return;
  const b = bbox(all), pad = 0.03 * Math.max(b[2] - b[0], b[3] - b[1], 1);
  svg.setAttribute("viewBox", `${b[0] - pad} ${-b[3] - pad} ${b[2] - b[0] + 2 * pad} ${b[3] - b[1] + 2 * pad}`);
  const unit = Math.max(b[2] - b[0], b[3] - b[1]) / 800;
  for (const g of groups) for (const x of g.rings) {
    const p = document.createElementNS(NS, "path");
    p.setAttribute("d", pathD(x.r));
    p.setAttribute("fill", x.fill || "none");
    p.setAttribute("fill-rule", "evenodd");
    p.setAttribute("stroke", x.stroke);
    p.setAttribute("stroke-width", String((x.w || 1) * unit));
    svg.appendChild(p);
  }
}
function drawParts() {
  const groups = [];
  let xoff = 0;
  PARTS.slice(0, 400).forEach((p, i) => {
    const b = bbox([p.outer]);
    const sh = (r) => r.map(([x, y]) => [x - b[0] + xoff, y - b[1]]);
    groups.push({ rings: [{ r: sh(p.outer), stroke: hue(i), fill: "none" }, ...p.holes.map((h) => ({ r: sh(h), stroke: hue(i) }))] });
    xoff += (b[2] - b[0]) * 1.08;
  });
  drawInto($("partsView"), groups);
}
function place(r, a, tx, ty) {
  const c = Math.cos(a), s = Math.sin(a);
  return r.map(([x, y]) => [c * x - s * y + tx, s * x + c * y + ty]);
}
// Lay the used sheets out in a row (like the session viewer scene) and return drawable rings.
function layoutResult(job, result) {
  const sheetCopies = [];
  for (const s of job.sheets) for (let k = 0; k < (s.count ?? 1); k++) sheetCopies.push(s);
  const used = [...new Set(result.placements.filter((p) => p.sheet >= 0).map((p) => p.sheet))].sort((a, b) => a - b);
  const idIndex = new Map(job.parts.map((p, i) => [JSON.stringify(p.id ?? i), i]));
  const widest = Math.max(1, ...used.map((s) => { const b = bbox([sheetCopies[s].outer]); return b[2] - b[0]; }));
  const gap = 0.1 * widest;
  const sheets = [];
  let x = 0;
  for (const sid of used) {
    const sh = sheetCopies[sid], b = bbox([sh.outer]);
    const dx = x - b[0], dy = -b[1];
    const mv = (r) => r.map(([px, py]) => [px + dx, py + dy]);
    const rings = [{ r: mv(sh.outer), stroke: "var(--sheet)", w: 2 }];
    for (const h of sh.holes || []) rings.push({ r: mv(h), stroke: "var(--bad)", w: 1.5 });
    let partArea = 0, count = 0;
    for (const pl of result.placements.filter((p) => p.sheet === sid)) {
      const pi = idIndex.get(JSON.stringify(pl.part)); const part = job.parts[pi];
      const o = mv(place(part.outer, pl.angle, pl.tx, pl.ty));
      const hs = (part.holes || []).map((h) => mv(place(h, pl.angle, pl.tx, pl.ty)));
      rings.push({ r: o, stroke: hue(pi), fill: `color-mix(in srgb, ${hue(pi)} 18%, transparent)` });
      for (const h of hs) rings.push({ r: h, stroke: hue(pi) });
      partArea += Math.abs(area(part.outer)) - (part.holes || []).reduce((s, h) => s + Math.abs(area(h)), 0);
      count++;
    }
    const usable = Math.abs(area(sh.outer)) - (sh.holes || []).reduce((s, h) => s + Math.abs(area(h)), 0);
    sheets.push({ sid, rings, count, util: usable > 0 ? partArea / usable : 0, dx, dy });
    x += b[2] - b[0] + gap;
  }
  return { sheets, unplaced: result.placements.filter((p) => p.sheet < 0).length };
}

// ------------------------------------------------------------------------------------------------
// exports
// ------------------------------------------------------------------------------------------------
function download(name, text, type) {
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([text], { type }));
  a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}
function exportSvg() {
  const L = layoutResult(CURRENT.job, CURRENT.result);
  const rings = L.sheets.flatMap((s) => s.rings);
  const b = bbox(rings.map((x) => x.r));
  const body = rings.map((x) => `<path d="${pathD(x.r)}" fill="none" stroke="${x.stroke.startsWith("var") ? "#000" : x.stroke}" stroke-width="${x.w || 1}"/>`).join("\n");
  download("opennest_result.svg",
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${b[0]} ${-b[3]} ${b[2] - b[0]} ${b[3] - b[1]}" width="${b[2] - b[0]}mm" height="${b[3] - b[1]}mm">\n${body}\n</svg>\n`,
    "image/svg+xml");
}
function exportDxf() {   // AutoCAD R12: POLYLINE / VERTEX / SEQEND, one layer per sheet, parts on PARTS
  const L = layoutResult(CURRENT.job, CURRENT.result);
  const out = ["0", "SECTION", "2", "ENTITIES"];
  L.sheets.forEach((s, k) => s.rings.forEach((x, j) => {
    const layer = j === 0 ? `SHEET_${k + 1}` : (x.stroke.includes("bad") ? `KEEPOUT_${k + 1}` : `PARTS_${k + 1}`);
    out.push("0", "POLYLINE", "8", layer, "66", "1", "70", "1");
    for (const [px, py] of x.r) out.push("0", "VERTEX", "8", layer, "10", px.toFixed(6), "20", py.toFixed(6));
    out.push("0", "SEQEND", "8", layer);
  }));
  out.push("0", "ENDSEC", "0", "EOF");
  download("opennest_result.dxf", out.join("\r\n") + "\r\n", "application/dxf");
}

// ------------------------------------------------------------------------------------------------
// UI
// ------------------------------------------------------------------------------------------------
async function refreshAccount() {
  if (!key()) { $("accountInfo").textContent = "Without a key you can run free 30-second previews."; updateTiers(false); return; }
  try {
    const me = await api("/api/me");
    const a = me.account;
    $("accountInfo").innerHTML = "";
    const t = document.createElement("span");
    t.textContent = `${a.email} · ${Math.floor(a.credits)} credits (seconds of compute) · `;
    const link = document.createElement("a");
    link.href = me.latest_viewer; link.target = "_blank"; link.rel = "noopener";
    link.textContent = "your latest result in the session viewer";
    $("accountInfo").append(t, link);
    updateTiers(true);
    setupBuy(a);
  } catch (e) { $("accountInfo").textContent = "Key not accepted: " + e.message; updateTiers(false); }
}
function updateTiers(hasKey) {
  const sel = $("tier"); const prev = sel.value; sel.innerHTML = "";
  for (const t of (INFO && INFO.tiers) || []) {
    const o = document.createElement("option");
    o.value = t.name; o.disabled = t.billed && !hasKey;
    o.textContent = `${t.name}: ${t.description}${t.billed ? "" : " (free)"}${t.billed && !hasKey ? " (needs an account)" : ""}`;
    sel.appendChild(o);
  }
  sel.value = [...sel.options].some((o) => o.value === prev && !o.disabled) ? prev : (hasKey ? "standard" : "preview");
}
function setupBuy(account) {
  const box = $("buy");
  if (!INFO || !INFO.paddle || !INFO.paddle.prices.length) { box.hidden = true; return; }
  box.hidden = false; box.innerHTML = "";
  for (const p of INFO.paddle.prices) {
    const b = document.createElement("button");
    b.type = "button"; b.textContent = `Buy ${Math.round(p.credits / 60)} compute minutes`;
    b.onclick = () => window.Paddle && window.Paddle.Checkout.open({
      items: [{ priceId: p.id, quantity: 1 }], customer: { email: account.email }, customData: { account_id: account.id },
    });
    box.appendChild(b);
  }
  if (!window.Paddle) {
    const s = document.createElement("script");
    s.src = "https://cdn.paddle.com/paddle/v2/paddle.js";
    s.onload = () => window.Paddle.Initialize({ token: INFO.paddle.client_token });
    document.head.appendChild(s);
  }
}

async function loadFiles() {
  PARTS = []; LOADED_JOB = null;
  const tol = 0.01;
  const scale = parseFloat($("scale").value) || 1;
  for (const f of $("files").files) {
    const text = await f.text();
    try {
      if (/\.json$/i.test(f.name)) {
        const j = JSON.parse(text);
        if (j.schema !== "opennest.job/1") throw new Error("not an opennest.job/1 file");
        LOADED_JOB = j;
        PARTS.push(...j.parts.map((p, i) => ({ name: String(p.id ?? i), outer: p.outer, holes: p.holes || [], qty: p.qty ?? 1 })));
        continue;
      }
      const rings = /\.dxf$/i.test(f.name) ? parseDxf(text, tol) : { rings: parseSvg(text, tol), skipped: 0 };
      if (rings.skipped) msg(`${f.name}: skipped ${rings.skipped} SPLINE/INSERT/HATCH entities (explode or convert them to polylines)`, true);
      const scaled = rings.rings.map((r) => r.map(([x, y]) => [x * scale, y * scale]));
      const parts = ringsToParts(scaled, f.name.replace(/\.[^.]+$/, ""));
      if (!parts.length) msg(`${f.name}: no closed outlines found`, true);
      PARTS.push(...parts);
    } catch (e) { msg(`${f.name}: ${e.message}`, true); }
  }
  $("jobSheetsNote").hidden = !LOADED_JOB;
  const holes = PARTS.reduce((s, p) => s + p.holes.length, 0);
  $("partsInfo").textContent = PARTS.length ? `${PARTS.length} part outline(s), ${holes} hole(s).` : "No parts loaded.";
  $("run").disabled = !PARTS.length;
  drawParts();
}

function buildJob() {
  if (LOADED_JOB) return JSON.parse(JSON.stringify(LOADED_JOB));
  const W = +$("sheetW").value, H = +$("sheetH").value, N = Math.max(1, Math.round(+$("sheetN").value));
  if (!(W > 0 && H > 0)) throw new Error("set a sheet width and height");
  const qty = Math.max(1, Math.round(+$("qty").value || 1));
  const rot = +$("rotations").value;
  return {
    schema: "opennest.job/1", units: "mm",
    spacing: Math.max(0, +$("spacing").value || 0), margin: Math.max(0, +$("margin").value || 0),
    parts: PARTS.map((p, i) => ({ id: p.name || `part ${i + 1}`, qty: p.qty ?? qty, rotations: rot, outer: p.outer, holes: p.holes })),
    sheets: [{ id: `sheet ${W}x${H}`, count: N, outer: [[0, 0], [W, 0], [W, H], [0, H]] }],
    params: { iter_mode: 1, iter_budget: 4000, num_rotations: rot === 0 ? 360 : rot, seed: 1 },
  };
}

async function run() {
  let job;
  try { job = buildJob(); } catch (e) { msg(e.message, true); return; }
  const tier = $("tier").value;
  $("run").disabled = true; $("resultCard").hidden = true;
  $("progress").hidden = false; $("barFill").style.width = "0"; $("progressText").textContent = "Submitting…";
  let rec;
  try {
    rec = await api(`/api/jobs?tier=${encodeURIComponent(tier)}`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(job),
    });
  } catch (e) { msg("Submit failed: " + e.message, true); $("run").disabled = false; $("progress").hidden = true; return; }
  CURRENT = { job, jobRecord: rec, result: null };
  $("cancel").hidden = false;
  $("progressText").textContent = rec.queue_position ? `Queued (${rec.queue_position} ahead)…` : "Starting…";
  follow(rec);
}

function follow(rec) {
  if (SOURCE) SOURCE.close();
  SOURCE = new EventSource(rec.links.events);
  const cap = rec.wall_cap;
  SOURCE.addEventListener("progress", (e) => {
    const p = JSON.parse(e.data);
    $("barFill").style.width = Math.min(100, (100 * p.t) / cap) + "%";
    $("progressText").textContent = `Running · ${p.t.toFixed(0)} s of at most ${cap.toFixed(0)} s · ${p.rounds.toLocaleString()} solver rounds` +
      (p.placed ? ` · ${p.placed} placed on ${p.n_sheets} sheet(s) so far` : "");
  });
  SOURCE.addEventListener("cap", () => { $("progressText").textContent = "Time budget reached, keeping the best layout…"; });
  SOURCE.addEventListener("status", async (e) => {
    SOURCE.close(); SOURCE = null;
    const j = JSON.parse(e.data);
    $("cancel").hidden = true; $("run").disabled = false; $("barFill").style.width = "100%";
    if (j.status === "failed") { $("progressText").textContent = "Failed: " + (j.error || "unknown error"); msg("Job failed: " + (j.error || ""), true); return; }
    $("progressText").textContent = `Finished (${j.status}) in ${(j.wall_s || 0).toFixed(1)} s` + (j.charged ? ` · ${j.charged} credits used` : "");
    try {
      CURRENT.result = await api(`/api/jobs/${j.id}/result`);
      CURRENT.jobRecord = j;
      showResult();
    } catch (err) { msg("Could not load the result: " + err.message, true); }
    refreshAccount();
  });
  SOURCE.onerror = () => { /* EventSource reconnects by itself */ };
}

function showResult() {
  const { job, result, jobRecord } = CURRENT;
  const L = layoutResult(job, result);
  $("resultCard").hidden = false;
  const utils = L.sheets.map((s) => (100 * s.util).toFixed(1) + "%").join(", ");
  $("resultInfo").textContent = `${L.sheets.length} sheet(s) used · utilisation ${utils}` +
    (L.unplaced ? ` · ${L.unplaced} part(s) did not fit` : " · every part placed");
  $("openViewer").href = jobRecord.links.viewer;
  drawInto($("resultView"), L.sheets);
}

async function cancelJob() {
  if (!CURRENT) return;
  try { await api(`/api/jobs/${CURRENT.jobRecord.id}/cancel`, { method: "POST" }); $("progressText").textContent = "Cancelling, keeping the best layout so far…"; }
  catch (e) { msg("Cancel failed: " + e.message, true); }
}

async function signup() {
  const email = ($("email").value || "").trim();
  if (!email) { msg("Enter an email address first", true); return; }
  try {
    const r = await api("/api/signup", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email }) });
    $("apiKey").value = r.api_key; store.set("opennest_key", r.api_key);
    msg(`Account created with ${Math.floor(r.account.credits)} free credits. Your API key is saved in this browser; copy it somewhere safe, it is shown only once.`);
    refreshAccount();
  } catch (e) { msg("Signup failed: " + e.message, true); }
}

async function init() {
  $("apiKey").value = store.get("opennest_key") || "";
  $("saveKey").onclick = () => { store.set("opennest_key", key()); refreshAccount(); };
  $("signup").onclick = signup;
  $("files").onchange = loadFiles;
  $("scale").onchange = () => $("files").files.length && loadFiles();
  $("run").onclick = run;
  $("cancel").onclick = cancelJob;
  $("dlSvg").onclick = exportSvg;
  $("dlDxf").onclick = exportDxf;
  $("dlJson").onclick = () => download("opennest_result.json", JSON.stringify(CURRENT.result, null, 1), "application/json");
  try {
    INFO = await api("/api/info");
    $("svcInfo").textContent = `service ${INFO.version} · ${INFO.slots} worker slot(s) × ${INFO.threads_per_job} threads`;
  } catch (e) { msg("Service not reachable: " + e.message, true); }
  updateTiers(false);
  refreshAccount();
}
init();
