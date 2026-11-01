//! session_viewer preview of a finished job: a Session .pb written with the viewer's own kernel
//! (session_rust) plus the small scene manifest (.yaml) the viewer's `?live=` mode watches.
//!
//! Layout: every used sheet copy is drawn in one row along +x (its bounding-box minimum moved to
//! (offset, 0)), a gap of 10% of the widest sheet between them; unplaced parts are laid out in a row
//! below. All geometry is baked into world coordinates (millimetres, z = 0), so the scene needs no
//! transforms. Groups: one per sheet ("sheet 1", ...) holding its outline, keep-outs and parts.

use serde::Serialize;
use serde_json::Value;
use session_rust::{Color, Point, Polyline, Session};

type Ring = Vec<[f64; 2]>;

fn ring(v: &Value) -> Option<Ring> {
    let mut out = Vec::new();
    for p in v.as_array()? {
        let a = p.as_array()?;
        out.push([a.first()?.as_f64()?, a.get(1)?.as_f64()?]);
    }
    if out.len() > 1 && out.first() == out.last() {
        out.pop();
    }
    (out.len() >= 3).then_some(out)
}

fn rings(v: Option<&Value>) -> Vec<Ring> {
    v.and_then(|h| h.as_array()).map(|a| a.iter().filter_map(ring).collect()).unwrap_or_default()
}

fn area(r: &Ring) -> f64 {
    let n = r.len();
    let mut a = 0.0;
    for i in 0..n {
        let j = (i + n - 1) % n;
        a += r[j][0] * r[i][1] - r[i][0] * r[j][1];
    }
    0.5 * a.abs()
}

fn bbox(r: &Ring) -> [f64; 4] {
    let mut b = [f64::MAX, f64::MAX, f64::MIN, f64::MIN];
    for p in r {
        b[0] = b[0].min(p[0]);
        b[1] = b[1].min(p[1]);
        b[2] = b[2].max(p[0]);
        b[3] = b[3].max(p[1]);
    }
    b
}

fn place(r: &Ring, angle: f64, tx: f64, ty: f64) -> Ring {
    let (s, c) = angle.sin_cos();
    r.iter().map(|p| [c * p[0] - s * p[1] + tx, s * p[0] + c * p[1] + ty]).collect()
}

fn shift(r: &Ring, dx: f64, dy: f64) -> Ring {
    r.iter().map(|p| [p[0] + dx, p[1] + dy]).collect()
}

/// A distinct, readable colour per part id (golden-angle hue walk).
fn part_color(index: usize) -> Color {
    let h = (index as f64 * 137.508) % 360.0;
    let (s, l) = (0.65, 0.42);
    let c = (1.0 - (2.0 * l - 1.0_f64).abs()) * s;
    let x = c * (1.0 - ((h / 60.0) % 2.0 - 1.0).abs());
    let m = l - c / 2.0;
    let (r, g, b) = match (h / 60.0) as i32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    Color::new((r + m) as f32, (g + m) as f32, (b + m) as f32, 1.0)
}

fn polyline(r: &Ring, name: &str, color: Color, width: f64) -> Polyline {
    let mut pts: Vec<Point> = r.iter().map(|p| Point::new(p[0], p[1], 0.0)).collect();
    pts.push(Point::new(r[0][0], r[0][1], 0.0)); // closed
    let mut pl = Polyline::new(pts);
    pl.name = name.to_string();
    pl.linecolor = color;
    pl.width = width;
    pl
}

#[derive(Serialize, Clone, Debug)]
pub struct SheetSummary {
    pub index: i64,
    pub parts: usize,
    pub utilization: f64, // placed part area / usable sheet area, 0..1
}

pub struct Preview {
    pub pb: Vec<u8>,
    pub texts: Vec<(String, [f64; 3], f64)>, // label, position, height
    pub sheets: Vec<SheetSummary>,
}

fn id_text(v: &Value) -> String {
    match v {
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}

/// Build the preview from the ORIGINAL job geometry and the result transforms.
pub fn build(job: &Value, result: &Value, title: &str) -> Result<Preview, String> {
    let parts = job["parts"].as_array().ok_or("job has no parts")?;
    let sheet_specs = job["sheets"].as_array().ok_or("job has no sheets")?;
    // sheet copies in the solver's order (sheet-major)
    let mut sheet_copies: Vec<(Ring, Vec<Ring>)> = Vec::new();
    for s in sheet_specs {
        let outer = ring(&s["outer"]).ok_or("sheet without a valid outer ring")?;
        let holes = rings(s.get("holes"));
        for _ in 0..s.get("count").and_then(|c| c.as_u64()).unwrap_or(1) {
            sheet_copies.push((outer.clone(), holes.clone()));
        }
    }
    let part_index = |id: &Value| parts.iter().position(|p| p.get("id").map(|x| x == id).unwrap_or(false));
    let placements = result["placements"].as_array().ok_or("result has no placements")?;

    let mut used: Vec<i64> = placements.iter().filter_map(|p| p["sheet"].as_i64()).filter(|s| *s >= 0).collect();
    used.sort();
    used.dedup();
    let widest = used
        .iter()
        .filter_map(|s| sheet_copies.get(*s as usize))
        .map(|(o, _)| {
            let b = bbox(o);
            b[2] - b[0]
        })
        .fold(0.0, f64::max)
        .max(1.0);
    let gap = 0.1 * widest;

    let mut session = Session::new(title);
    let ink = Color::new(0.1, 0.1, 0.1, 1.0);
    let keepout = Color::new(0.8, 0.15, 0.15, 1.0);
    let mut texts = Vec::new();
    let mut summaries = Vec::new();
    let mut x = 0.0;
    let mut row_height: f64 = 0.0;
    for (slot, sid) in used.iter().enumerate() {
        let (outer, holes) = &sheet_copies[*sid as usize];
        let b = bbox(outer);
        let (dx, dy) = (x - b[0], -b[1]);
        let group = session.add_group(&format!("sheet {}", slot + 1));
        session.add_polyline(polyline(&shift(outer, dx, dy), "sheet", ink.clone(), 2.0), Some(&group));
        for h in holes {
            session.add_polyline(polyline(&shift(h, dx, dy), "keep-out", keepout.clone(), 1.5), Some(&group));
        }
        let usable = area(outer) - holes.iter().map(area).sum::<f64>();
        let mut placed_area = 0.0;
        let mut count = 0;
        for pl in placements.iter().filter(|p| p["sheet"].as_i64() == Some(*sid)) {
            let Some(pi) = part_index(&pl["part"]) else { continue };
            let part = &parts[pi];
            let Some(po) = ring(&part["outer"]) else { continue };
            let (a, tx, ty) = (pl["angle"].as_f64().unwrap_or(0.0), pl["tx"].as_f64().unwrap_or(0.0), pl["ty"].as_f64().unwrap_or(0.0));
            let name = format!("{} #{}", id_text(&pl["part"]), pl["copy"].as_i64().unwrap_or(0));
            let color = part_color(pi);
            session.add_polyline(polyline(&shift(&place(&po, a, tx, ty), dx, dy), &name, color.clone(), 1.0), Some(&group));
            let ph = rings(part.get("holes"));
            for h in &ph {
                session.add_polyline(polyline(&shift(&place(h, a, tx, ty), dx, dy), &format!("{name} hole"), color.clone(), 1.0), Some(&group));
            }
            placed_area += area(&po) - ph.iter().map(area).sum::<f64>();
            count += 1;
        }
        let util = if usable > 0.0 { placed_area / usable } else { 0.0 };
        let (w, h) = (b[2] - b[0], b[3] - b[1]);
        texts.push((format!("Sheet {}  {} parts  {:.1}%", slot + 1, count, 100.0 * util), [x, h + 0.04 * h.max(w * 0.25), 0.0], 0.04 * h.max(w * 0.25)));
        summaries.push(SheetSummary { index: *sid, parts: count, utilization: util });
        row_height = row_height.max(h);
        x += w + gap;
    }

    // unplaced parts: one row below the sheets, each at its own size
    let unplaced: Vec<&Value> = placements.iter().filter(|p| p["sheet"].as_i64().unwrap_or(-1) < 0).collect();
    if !unplaced.is_empty() {
        let group = session.add_group("unplaced");
        let mut ux = 0.0;
        let mut top = -gap;
        for pl in &unplaced {
            let Some(pi) = part_index(&pl["part"]) else { continue };
            let Some(po) = ring(&parts[pi]["outer"]) else { continue };
            let b = bbox(&po);
            let (dx, dy) = (ux - b[0], -gap - (b[3] - b[1]) - b[1]);
            let name = format!("UNPLACED {} #{}", id_text(&pl["part"]), pl["copy"].as_i64().unwrap_or(0));
            session.add_polyline(polyline(&shift(&po, dx, dy), &name, keepout.clone(), 1.5), Some(&group));
            for h in rings(parts[pi].get("holes")) {
                session.add_polyline(polyline(&shift(&h, dx, dy), &name, keepout.clone(), 1.5), Some(&group));
            }
            ux += (b[2] - b[0]) + 0.02 * widest;
            top = top.min(-gap - (b[3] - b[1]));
        }
        let hgt = 0.04 * row_height.max(widest * 0.25);
        texts.push((format!("{} unplaced", unplaced.len()), [0.0, top - 1.5 * hgt, 0.0], hgt));
    }
    Ok(Preview { pb: session.pb_dumps(), texts, sheets: summaries })
}

/// Scene manifest for session_viewer. `file` is resolved against the manifest's own folder.
/// Only numbers and fixed text go into it (never user strings), quoted as JSON strings, which YAML reads.
pub fn manifest(name: &str, file: &str, texts: &[(String, [f64; 3], f64)]) -> String {
    let q = |s: &str| serde_json::to_string(s).unwrap_or_else(|_| "\"\"".into());
    let mut y = format!("name: {}\nlive: true\ngrid: false\nitems:\n  - file: {}\n    name: \"nesting\"\n", q(name), q(file));
    if !texts.is_empty() {
        y.push_str("texts:\n");
        for (t, at, h) in texts {
            y.push_str(&format!(
                "  - text: {}\n    at: [{:.3}, {:.3}, {:.3}]\n    height: {:.3}\n    plate: false\n    color: [20, 20, 20, 255]\n",
                q(t), at[0], at[1], at[2], h
            ));
        }
    }
    y
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn preview_pb_reads_back_with_session_rust() {
        let job = json!({
            "schema": "opennest.job/1",
            "parts": [
                {"id": "a", "qty": 2, "outer": [[0,0],[10,0],[10,5],[0,5]]},
                {"id": "ring", "outer": [[0,0],[20,0],[20,20],[0,20]], "holes": [[[5,5],[15,5],[15,15],[5,15]]]},
                {"id": "huge", "outer": [[0,0],[500,0],[500,500],[0,500]]}
            ],
            "sheets": [{"id": "s", "count": 2, "outer": [[100,100],[200,100],[200,150],[100,150]],
                        "holes": [[[140,120],[150,120],[150,130],[140,130]]]}]
        });
        let result = json!({"placements": [
            {"part": "a", "copy": 0, "sheet": 0, "tx": 100.0, "ty": 100.0, "angle": 0.0},
            {"part": "a", "copy": 1, "sheet": 1, "tx": 110.0, "ty": 100.0, "angle": 0.0},
            {"part": "ring", "copy": 0, "sheet": 1, "tx": 120.0, "ty": 105.0, "angle": 0.0},
            {"part": "huge", "copy": 0, "sheet": -1, "tx": 0.0, "ty": 0.0, "angle": 0.0}
        ]});
        let p = build(&job, &result, "t").unwrap();
        // 2 sheets x (outline + keep-out) + 2 rects + ring outer + ring hole + 1 unplaced = 9 polylines
        Session::pb_loads(&p.pb).expect("session_rust must read the preview back");
        use prost::Message;
        let raw = session_rust::proto::Session::decode(p.pb.as_slice()).unwrap();
        let polylines = raw.objects.as_ref().map(|o| o.polylines.len()).unwrap_or(0);
        assert_eq!(polylines, 9);
        assert_eq!(p.sheets.len(), 2);
        assert_eq!(p.sheets[1].parts, 2);
        let usable = 100.0 * 50.0 - 100.0;
        assert!((p.sheets[0].utilization - 50.0 / usable).abs() < 1e-9);
        assert!((p.sheets[1].utilization - (50.0 + 300.0) / usable).abs() < 1e-9);
        assert!(p.texts.iter().any(|t| t.0.contains("unplaced")));
        let y = manifest("n", "result.pb", &p.texts);
        assert!(y.contains("file: \"result.pb\"") && y.contains("live: true"));
    }
}
