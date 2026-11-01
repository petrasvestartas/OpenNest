//! A nesting job from a serialized Session (session_proto `Session`, the `.pb` the session kernels
//! write and session_viewer reads).
//!
//! Convention:
//! * every closed polyline is a ring; a polyline whose tree ancestry contains a group named `sheets`
//!   (case-insensitive) is a SHEET ring, every other one a PART ring;
//! * within each class, a ring inside another ring is a hole of it (part hole / sheet keep-out):
//!   nesting depth even = outline, odd = hole;
//! * one polyline = one copy (draw a part twice to nest two);
//! * `Session.xforms` (local transforms along the tree) are applied, z is dropped;
//! * a `Component` whose type_name or name is `opennest` may carry `json_data` with the job's
//!   `spacing`, `margin`, `params` and `limits`; explicit `Opts` win over it.
//! The part id is the polyline's name (made unique when repeated), so results and the viewer scene
//! name the same things the author did.

use prost::Message;
use serde_json::{json, Value};
use session_rust::proto::{Polyline, Session, TreeNode};
use std::collections::HashMap;

#[derive(Default, Clone, Debug)]
pub struct Opts {
    pub spacing: Option<f64>,
    pub margin: Option<f64>,
    pub fit_mode: Option<i64>,
    pub wall_seconds: Option<f64>,
    pub seed: Option<i64>,
    pub rotations: Option<i64>,
    pub name: Option<String>,
}

pub struct Imported {
    pub job: Value,
    pub warnings: Vec<String>,
}

type Mat = [f64; 16]; // column-major, like session_proto.Xform.matrix

const IDENTITY: Mat = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0];

fn mul(a: &Mat, b: &Mat) -> Mat {
    let mut m = [0.0; 16];
    for c in 0..4 {
        for r in 0..4 {
            m[c * 4 + r] = (0..4).map(|k| a[k * 4 + r] * b[c * 4 + k]).sum();
        }
    }
    m
}

fn apply(m: &Mat, x: f64, y: f64, z: f64) -> [f64; 2] {
    [m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13]]
}

fn area(r: &[[f64; 2]]) -> f64 {
    let n = r.len();
    let mut a = 0.0;
    for i in 0..n {
        let j = (i + n - 1) % n;
        a += r[j][0] * r[i][1] - r[i][0] * r[j][1];
    }
    0.5 * a
}

fn inside(p: [f64; 2], r: &[[f64; 2]]) -> bool {
    let mut c = false;
    let n = r.len();
    let mut j = n - 1;
    for i in 0..n {
        let (xi, yi) = (r[i][0], r[i][1]);
        let (xj, yj) = (r[j][0], r[j][1]);
        if (yi > p[1]) != (yj > p[1]) && p[0] < (xj - xi) * (p[1] - yi) / (yj - yi) + xi {
            c = !c;
        }
        j = i;
    }
    c
}

struct RingIn {
    ring: Vec<[f64; 2]>,
    name: String,
}

/// Rings -> outlines with holes by containment (largest first; a ring's parent is the smallest
/// larger ring containing it; even depth = outline).
fn nest(rings: Vec<RingIn>) -> Vec<(RingIn, Vec<Vec<[f64; 2]>>)> {
    let n = rings.len();
    let areas: Vec<f64> = rings.iter().map(|r| area(&r.ring).abs()).collect();
    let mut order: Vec<usize> = (0..n).collect();
    order.sort_by(|a, b| areas[*b].partial_cmp(&areas[*a]).unwrap_or(std::cmp::Ordering::Equal));
    let mut parent = vec![usize::MAX; n];
    let mut depth = vec![0usize; n];
    for (k, &i) in order.iter().enumerate() {
        let probe = [rings[i].ring[0], rings[i].ring[rings[i].ring.len() / 2]];
        for m in (0..k).rev() {
            let c = order[m];
            if areas[c] > areas[i] && inside(probe[0], &rings[c].ring) && inside(probe[1], &rings[c].ring) {
                parent[i] = c;
                depth[i] = depth[c] + 1;
                break;
            }
        }
    }
    let mut out: Vec<(RingIn, Vec<Vec<[f64; 2]>>)> = Vec::new();
    let mut slot = vec![usize::MAX; n];
    let mut rings: Vec<Option<RingIn>> = rings.into_iter().map(Some).collect();
    for i in 0..n {   // outlines in the author's drawing order
        if depth[i] % 2 == 0 {
            slot[i] = out.len();
            out.push((rings[i].take().expect("ring"), Vec::new()));
        }
    }
    for &i in &order {
        if depth[i] % 2 == 1 {
            if let Some(r) = rings[i].take() {
                let p = parent[i];
                if slot[p] != usize::MAX {
                    out[slot[p]].1.push(r.ring);
                }
            }
        }
    }
    out
}

fn polyline_ring(pl: &Polyline, m: &Mat, warnings: &mut Vec<String>) -> Option<Vec<[f64; 2]>> {
    let label = if pl.name.is_empty() { pl.guid.clone() } else { pl.name.clone() };
    let c = &pl.coords;
    if c.len() < 9 {
        warnings.push(format!("polyline '{label}': fewer than 3 points, ignored"));
        return None;
    }
    let mut r: Vec<[f64; 2]> = Vec::with_capacity(c.len() / 3);
    for k in (0..c.len() - 2).step_by(3) {
        let p = apply(m, c[k], c[k + 1], c[k + 2]);
        if r.last().map(|q| (q[0] - p[0]).abs() < 1e-9 && (q[1] - p[1]).abs() < 1e-9).unwrap_or(false) {
            continue;
        }
        r.push(p);
    }
    if r.len() > 1 && (r[0][0] - r[r.len() - 1][0]).abs() < 1e-6 && (r[0][1] - r[r.len() - 1][1]).abs() < 1e-6 {
        r.pop();
    }
    if r.len() < 3 {
        warnings.push(format!("polyline '{label}': degenerate, ignored"));
        return None;
    }
    let b = r.iter().fold([f64::MAX, f64::MAX, f64::MIN, f64::MIN], |b, p| [b[0].min(p[0]), b[1].min(p[1]), b[2].max(p[0]), b[3].max(p[1])]);
    let ext2 = (b[2] - b[0]).powi(2) + (b[3] - b[1]).powi(2);
    if area(&r).abs() <= 1e-9 * ext2 + 1e-12 {
        warnings.push(format!("polyline '{label}': no area (an open line?), ignored"));
        return None;
    }
    Some(r)
}

fn ring_json(r: &[[f64; 2]]) -> Value {
    Value::Array(r.iter().map(|p| json!([p[0], p[1]])).collect())
}

pub fn session_to_job(bytes: &[u8], opts: &Opts) -> Result<Imported, String> {
    let session = Session::decode(bytes).map_err(|e| format!("not a session file: {e}"))?;
    let objects = session.objects.as_ref().ok_or("the session has no objects")?;
    let polylines: HashMap<&str, &Polyline> = objects.polylines.iter().map(|p| (p.guid.as_str(), p)).collect();
    let xforms: HashMap<&str, Mat> = session
        .xforms
        .iter()
        .filter_map(|e| {
            let m = e.xform.as_ref()?.matrix.as_slice();
            (m.len() == 16).then(|| (e.guid.as_str(), <[f64; 16]>::try_from(m).ok()).into())
        })
        .filter_map(|(g, m): (&str, Option<Mat>)| m.map(|m| (g, m)))
        .collect();
    let mut warnings = Vec::new();

    let mut parts: Vec<RingIn> = Vec::new();
    let mut sheets: Vec<RingIn> = Vec::new();
    let mut seen: std::collections::HashSet<String> = Default::default();
    fn walk(
        node: &TreeNode, world: Mat, in_sheets: bool, polylines: &HashMap<&str, &Polyline>, xforms: &HashMap<&str, Mat>,
        parts: &mut Vec<RingIn>, sheets: &mut Vec<RingIn>, seen: &mut std::collections::HashSet<String>, warnings: &mut Vec<String>,
    ) {
        let local = xforms.get(node.guid.as_str()).or_else(|| xforms.get(node.name.as_str())).copied().unwrap_or(IDENTITY);
        let world = mul(&world, &local);
        if let Some(pl) = polylines.get(node.name.as_str()) {
            seen.insert(pl.guid.clone());
            if let Some(ring) = polyline_ring(pl, &world, warnings) {
                let name = if pl.name.is_empty() { pl.guid.clone() } else { pl.name.clone() };
                (if in_sheets { sheets } else { parts }).push(RingIn { ring, name });
            }
            return;
        }
        let here = in_sheets || node.name.eq_ignore_ascii_case("sheets") || node.name.eq_ignore_ascii_case("sheet");
        for child in &node.children {
            walk(child, world, here, polylines, xforms, parts, sheets, seen, warnings);
        }
    }
    if let Some(root) = session.tree.as_ref().and_then(|t| t.root.as_ref()) {
        walk(root, IDENTITY, false, &polylines, &xforms, &mut parts, &mut sheets, &mut seen, &mut warnings);
    }
    for pl in &objects.polylines {
        if !seen.contains(&pl.guid) {
            if let Some(ring) = polyline_ring(pl, &IDENTITY, &mut warnings) {
                warnings.push(format!("polyline '{}' is not in the tree: taken as a part", if pl.name.is_empty() { &pl.guid } else { &pl.name }));
                parts.push(RingIn { ring, name: if pl.name.is_empty() { pl.guid.clone() } else { pl.name.clone() } });
            }
        }
    }
    if sheets.is_empty() {
        return Err("no sheets: put the sheet outline(s) in a group named 'sheets'".into());
    }
    if parts.is_empty() {
        return Err("no parts: no closed polyline outside the 'sheets' group".into());
    }

    // settings carried in the file
    let mut file_opts = json!({});
    for c in &objects.components {
        if c.type_name.eq_ignore_ascii_case("opennest") || c.name.eq_ignore_ascii_case("opennest") {
            match serde_json::from_str::<Value>(&c.json_data) {
                Ok(v) if v.is_object() => file_opts = v,
                _ => warnings.push("component 'opennest' has no JSON object in json_data, ignored".into()),
            }
        }
    }
    let num = |k: &str| file_opts.get(k).and_then(|v| v.as_f64());
    let spacing = opts.spacing.or_else(|| num("spacing")).unwrap_or(0.0);
    let margin = opts.margin.or_else(|| num("margin")).unwrap_or(0.0);
    let mut params = file_opts.get("params").cloned().filter(|v| v.is_object()).unwrap_or_else(|| json!({}));
    if let Some(f) = opts.fit_mode { params["fit_mode"] = json!(f); }
    if let Some(s) = opts.seed { params["seed"] = json!(s); }
    if params.get("iter_mode").is_none() { params["iter_mode"] = json!(1); }
    if params.get("iter_budget").is_none() { params["iter_budget"] = json!(4000); }
    let rotations = opts.rotations.or_else(|| num("rotations").map(|r| r as i64)).unwrap_or(0);
    if params.get("num_rotations").is_none() { params["num_rotations"] = json!(if rotations > 0 { rotations } else { 3600 }); }
    let mut limits = file_opts.get("limits").cloned().filter(|v| v.is_object()).unwrap_or_else(|| json!({}));
    if let Some(w) = opts.wall_seconds { limits["wall_seconds"] = json!(w); }

    let mut used: HashMap<String, usize> = HashMap::new();
    let unique = |name: &str, used: &mut HashMap<String, usize>| -> String {
        let n = used.entry(name.to_string()).or_insert(0);
        *n += 1;
        if *n == 1 { name.to_string() } else { format!("{name} ({n})") }
    };
    let part_json: Vec<Value> = nest(parts)
        .into_iter()
        .map(|(r, holes)| json!({
            "id": unique(&r.name, &mut used), "qty": 1, "rotations": rotations,
            "outer": ring_json(&r.ring), "holes": holes.iter().map(|h| ring_json(h)).collect::<Vec<_>>(),
        }))
        .collect();
    let mut used_s: HashMap<String, usize> = HashMap::new();
    let sheet_json: Vec<Value> = nest(sheets)
        .into_iter()
        .map(|(r, holes)| json!({
            "id": unique(&r.name, &mut used_s), "count": 1,
            "outer": ring_json(&r.ring), "holes": holes.iter().map(|h| ring_json(h)).collect::<Vec<_>>(),
        }))
        .collect();
    let name = opts.name.clone().filter(|n| !n.is_empty()).unwrap_or_else(|| session.name.clone());
    Ok(Imported {
        job: json!({
            "schema": "opennest.job/1", "units": "mm", "name": name,
            "spacing": spacing, "margin": margin,
            "parts": part_json, "sheets": sheet_json, "params": params, "limits": limits,
        }),
        warnings,
    })
}

/// An `opennest.job/1` document as a session file (`parts` and `sheets` groups, holes as nested
/// polylines, qty / count as repeated polylines, settings in an `opennest` component). Parts are
/// laid out in a row and sheets in a row below them: in a job every ring is in its own coordinates,
/// while a drawing's containment rule needs outlines that do not overlap.
pub fn job_to_session(job: &Value) -> Result<Vec<u8>, String> {
    use session_rust::{Color, Point, Polyline as Pl, Session as S};
    let ring = |v: &Value| -> Option<Vec<[f64; 2]>> {
        let a = v.as_array()?;
        let mut r = Vec::with_capacity(a.len());
        for p in a { let q = p.as_array()?; r.push([q.first()?.as_f64()?, q.get(1)?.as_f64()?]); }
        (r.len() >= 3).then_some(r)
    };
    let pl = |r: &[[f64; 2]], name: &str| {
        let mut pts: Vec<Point> = r.iter().map(|p| Point::new(p[0], p[1], 0.0)).collect();
        pts.push(Point::new(r[0][0], r[0][1], 0.0));
        let mut p = Pl::new(pts);
        p.name = name.to_string();
        p.linecolor = Color::new(0.1, 0.1, 0.1, 1.0);
        p
    };
    let bbox = |r: &[[f64; 2]]| r.iter().fold([f64::MAX, f64::MAX, f64::MIN, f64::MIN], |b, p| [b[0].min(p[0]), b[1].min(p[1]), b[2].max(p[0]), b[3].max(p[1])]);
    let shift = |r: &[[f64; 2]], dx: f64, dy: f64| -> Vec<[f64; 2]> { r.iter().map(|p| [p[0] + dx, p[1] + dy]).collect() };
    // (id, outer, holes) per copy
    let mut part_items: Vec<(String, Vec<[f64; 2]>, Vec<Vec<[f64; 2]>>)> = Vec::new();
    for (i, p) in job["parts"].as_array().ok_or("job.parts missing")?.iter().enumerate() {
        let id = match &p["id"] { Value::String(t) => t.clone(), Value::Null => format!("part {i}"), other => other.to_string() };
        let outer = ring(&p["outer"]).ok_or_else(|| format!("parts[{i}].outer invalid"))?;
        let holes: Vec<Vec<[f64; 2]>> = p["holes"].as_array().map(|h| h.iter().filter_map(ring).collect()).unwrap_or_default();
        for _ in 0..p["qty"].as_u64().unwrap_or(1).max(1) { part_items.push((id.clone(), outer.clone(), holes.clone())); }
    }
    let mut sheet_items: Vec<(String, Vec<[f64; 2]>, Vec<Vec<[f64; 2]>>)> = Vec::new();
    for (i, sh) in job["sheets"].as_array().ok_or("job.sheets missing")?.iter().enumerate() {
        let id = match &sh["id"] { Value::String(t) => t.clone(), Value::Null => format!("sheet {i}"), other => other.to_string() };
        let outer = ring(&sh["outer"]).ok_or_else(|| format!("sheets[{i}].outer invalid"))?;
        let holes: Vec<Vec<[f64; 2]>> = sh["holes"].as_array().map(|h| h.iter().filter_map(ring).collect()).unwrap_or_default();
        for _ in 0..sh["count"].as_u64().unwrap_or(1).max(1) { sheet_items.push((id.clone(), outer.clone(), holes.clone())); }
    }
    let widest = part_items.iter().chain(sheet_items.iter()).map(|(_, o, _)| { let b = bbox(o); b[2] - b[0] }).fold(1.0, f64::max);
    let gap = 0.05 * widest;
    let name = job["name"].as_str().unwrap_or("opennest job");
    let mut s = S::new(name);
    let parts = s.add_group("parts");
    let (mut x, mut row_h) = (0.0, 0.0_f64);
    for (id, outer, holes) in &part_items {
        let b = bbox(outer);
        let (dx, dy) = (x - b[0], -b[1]);
        s.add_polyline(pl(&shift(outer, dx, dy), id), Some(&parts));
        for h in holes { s.add_polyline(pl(&shift(h, dx, dy), &format!("{id} hole")), Some(&parts)); }
        x += b[2] - b[0] + gap;
        row_h = row_h.max(b[3] - b[1]);
    }
    let sheets = s.add_group("sheets");
    let (mut x, y0) = (0.0, -(row_h + 4.0 * gap));
    for (id, outer, holes) in &sheet_items {
        let b = bbox(outer);
        let (dx, dy) = (x - b[0], y0 - b[3]);
        s.add_polyline(pl(&shift(outer, dx, dy), id), Some(&sheets));
        for h in holes { s.add_polyline(pl(&shift(h, dx, dy), &format!("{id} keep-out")), Some(&sheets)); }
        x += b[2] - b[0] + gap;
    }
    let mut settings = serde_json::Map::new();
    for k in ["spacing", "margin", "params", "limits"] {
        if let Some(v) = job.get(k) { settings.insert(k.to_string(), v.clone()); }
    }
    if !settings.is_empty() {
        let mut comp = session_rust::Component::new();
        comp.type_name = "opennest".to_string();
        comp.name = "opennest".to_string();
        comp.extra = settings.into_iter().collect();
        s.add_component(comp, None);
    }
    Ok(s.pb_dumps())
}

/// The clearance example as a session file: the format reference, and the e2e fixture.
pub fn example_session() -> Vec<u8> {
    use session_rust::{Color, Point, Polyline as Pl, Session as S};
    let mut s = S::new("opennest example");
    let pl = |r: &[[f64; 2]], name: &str| {
        let mut pts: Vec<Point> = r.iter().map(|p| Point::new(p[0], p[1], 0.0)).collect();
        pts.push(Point::new(r[0][0], r[0][1], 0.0));
        let mut p = Pl::new(pts);
        p.name = name.to_string();
        p.linecolor = Color::new(0.1, 0.1, 0.1, 1.0);
        p
    };
    let parts = s.add_group("parts");
    let rect = |x: f64, y: f64, w: f64, h: f64| vec![[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
    for i in 0..6 { s.add_polyline(pl(&rect(i as f64 * 230.0, 0.0, 200.0, 100.0), "rect 200x100"), Some(&parts)); }
    for i in 0..3 {
        let x = i as f64 * 330.0;
        s.add_polyline(pl(&[[x, 150.0], [x + 300.0, 150.0], [x + 300.0, 230.0], [x + 80.0, 230.0], [x + 80.0, 450.0], [x, 450.0]], "L 300"), Some(&parts));
    }
    for i in 0..2 {
        let x = i as f64 * 330.0;
        s.add_polyline(pl(&rect(x, 500.0, 300.0, 300.0), "frame 300"), Some(&parts));
        s.add_polyline(pl(&rect(x + 40.0, 540.0, 220.0, 220.0), "frame 300 hole"), Some(&parts));   // inside the frame -> its hole
    }
    for i in 0..4 {
        let x = i as f64 * 180.0;
        s.add_polyline(pl(&[[x, 850.0], [x + 150.0, 850.0], [x, 1000.0]], "tri 150"), Some(&parts));
    }
    for i in 0..8 { s.add_polyline(pl(&rect(i as f64 * 70.0, 1050.0, 60.0, 40.0), "small 60x40"), Some(&parts)); }
    let sheets = s.add_group("sheets");
    for k in 0..3 {
        let y = 1200.0 + k as f64 * 800.0;
        s.add_polyline(pl(&rect(0.0, y, 1200.0, 700.0), "sheet 1200x700"), Some(&sheets));
        s.add_polyline(pl(&rect(550.0, y + 300.0, 100.0, 100.0), "keep-out"), Some(&sheets));       // inside the sheet -> keep-out
    }
    s.pb_dumps()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn example_round_trips() {
        let bytes = example_session();
        let imp = session_to_job(&bytes, &Opts { spacing: Some(10.0), margin: Some(15.0), fit_mode: Some(0), ..Default::default() }).unwrap();
        let j = &imp.job;
        assert_eq!(j["parts"].as_array().unwrap().len(), 6 + 3 + 2 + 4 + 8);
        let frames: Vec<&Value> = j["parts"].as_array().unwrap().iter().filter(|p| p["id"].as_str().unwrap().starts_with("frame 300")).collect();
        assert_eq!(frames.len(), 2);
        assert!(frames.iter().all(|f| f["holes"].as_array().unwrap().len() == 1), "frame holes by containment");
        assert_eq!(j["sheets"].as_array().unwrap().len(), 3);
        assert!(j["sheets"][0]["holes"].as_array().unwrap().len() == 1, "keep-out by containment");
        assert_eq!(j["spacing"], 10.0);
        assert_eq!(j["params"]["fit_mode"], 0);
        assert_eq!(j["parts"][0]["id"], "rect 200x100");
        assert_eq!(j["parts"][1]["id"], "rect 200x100 (2)");
        assert!(imp.warnings.is_empty(), "{:?}", imp.warnings);
    }

    #[test]
    fn garbage_is_rejected() {
        assert!(session_to_job(b"not a session", &Opts::default()).is_err());
    }
}
