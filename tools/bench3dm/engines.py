"""Headless driver for the two OpenNest nesting engines (nest_physics.dll / nfp_nest.dll).

Loads the engine DLLs with ctypes (cdecl C ABIs, static CRT), flattens an
``opennest.bench3dm.instance/1`` JSON into the exact flat arrays the ABIs expect, runs one solve
with a wall-clock cancel timer, and writes an ``opennest.bench3dm.placements/1`` JSON.

    python -I tools/bench3dm/engines.py --selftest
    python -I tools/bench3dm/engines.py --instance <instance.json> --config gh_np --seed 100 \
        --cap 900 --out <dir> [--dll <path>] [--set key=value ...] [--progress]

Exit codes: 0 ok, 1 failure, 124 cancelled by the cap timer (placements.json is still written with the
best-so-far layout and ``"cancelled": true``).

The DLLs print diagnostics on the process stdout/stderr; run_matrix.py runs this script in a
subprocess and captures that output into engine_stdout.txt.

C ABIs mirrored here (keep in sync):
  src/nest_physics_cpp/nest_physics_capi.h      (NpParams, np_nest, np_nest_prio, np_last_quality,
                                                 np_last_prio, np_cancel ...)
  src/opennest_cpp/src/capi/nfp_nest_capi.h     (NfpParams, nfp_nest, nfp_cancel ...)
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import sys
import threading
import time
from ctypes import POINTER, c_double, c_int, c_longlong
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
CONFIGS_PATH = HERE / "configs.json"

PLACEMENTS_SCHEMA = "opennest.bench3dm.placements/1"
INSTANCE_SCHEMA = "opennest.bench3dm.instance/1"
FRAME = "p_world = sheet_world_origin[sid] + (inset,inset) + R(angle_rad)*p_local + (tx,ty)"

ENGINE_NP = "np_nest"
ENGINE_NFP = "nfp_nest"
DLL_NAMES = {ENGINE_NP: "nest_physics.dll", ENGINE_NFP: "nfp_nest.dll"}
DLL_ENV = {ENGINE_NP: "BENCH3DM_NP_DLL", ENGINE_NFP: "BENCH3DM_NFP_DLL"}
DLL_FALLBACK = {
    ENGINE_NP: REPO / "src" / "nest_physics_cpp" / "build" / "Release" / "nest_physics.dll",
    ENGINE_NFP: REPO / "src" / "opennest_cpp" / "build" / "Release" / "nfp_nest.dll",
}
DLL_BASELINE_DIR = HERE / "out" / "dll_baseline"

EXIT_OK, EXIT_FAIL, EXIT_CANCELLED = 0, 1, 124


# ----------------------------------------------------------------------------------------------------
# ABI structs
# ----------------------------------------------------------------------------------------------------
class NpParams(ctypes.Structure):
    """Mirror of `struct NpParams` (nest_physics_capi.h). Natural x64 alignment, sizeof 80."""
    _fields_ = [
        ("num_rotations", c_int),
        ("spacing", c_double),             # IGNORED by the engine (host pre-offsets)
        ("simplify_tolerance", c_double),
        ("seed", c_int),
        ("time_budget_secs", c_double),
        ("iter_budget", c_longlong),
        ("iter_mode", c_int),
        ("max_sheets", c_int),
        ("n_starts", c_int),
        ("part_holes_mode", c_int),
        ("pole_max", c_int),
        ("final_compact", c_int),
        ("fit_mode", c_int),               # 0 fewest sheets, 1 one sheet, 2 global bin pack, 3 group & fill
        ("n_threads", c_int),              # fit_mode 3 portfolio threads (0 -> 4); former tail padding, sizeof stays 80
    ]


class NfpParams(ctypes.Structure):
    """Mirror of `struct NfpParams` (nfp_nest_capi.h). Natural x64 alignment, sizeof 152."""
    _fields_ = [
        ("placementType", c_int),
        ("rotations", c_int),
        ("mutationRate", c_int),
        ("populationSize", c_int),
        ("seed", c_int),
        ("curveTolerance", c_double),
        ("clipperScale", c_double),
        ("spacing", c_double),
        ("sheetSpacing", c_double),
        ("rotationLimit", c_double),
        ("useHoles", c_int),
        ("exploreConcave", c_int),
        ("clipByHull", c_int),
        ("clipByRects", c_int),
        ("simplify", c_int),
        ("mode", c_int),
        ("generations", c_int),
        ("numSeeds", c_int),
        ("useParallel", c_int),
        ("timeBudgetSecs", c_double),
        ("maxSheets", c_int),
        ("edgeSamples", c_int),
        ("compactionPasses", c_int),
        ("tryAllRotations", c_int),
        ("exactNfp", c_int),
        ("stagnationGens", c_int),
        ("exactVoids", c_int),
        ("smartRotations", c_int),
        ("rotationTiltDeg", c_double),
    ]


# Expected x64 offsets, derived by hand from the C headers (natural alignment).
NP_OFFSETS = {
    "num_rotations": 0, "spacing": 8, "simplify_tolerance": 16, "seed": 24, "time_budget_secs": 32,
    "iter_budget": 40, "iter_mode": 48, "max_sheets": 52, "n_starts": 56, "part_holes_mode": 60,
    "pole_max": 64, "final_compact": 68, "fit_mode": 72, "n_threads": 76,
}
NFP_OFFSETS = {
    "placementType": 0, "rotations": 4, "mutationRate": 8, "populationSize": 12, "seed": 16,
    "curveTolerance": 24, "clipperScale": 32, "spacing": 40, "sheetSpacing": 48, "rotationLimit": 56,
    "useHoles": 64, "exploreConcave": 68, "clipByHull": 72, "clipByRects": 76, "simplify": 80,
    "mode": 84, "generations": 88, "numSeeds": 92, "useParallel": 96, "timeBudgetSecs": 104,
    "maxSheets": 112, "edgeSamples": 116, "compactionPasses": 120, "tryAllRotations": 124,
    "exactNfp": 128, "stagnationGens": 132, "exactVoids": 136,
    "smartRotations": 140, "rotationTiltDeg": 144,
}
assert ctypes.sizeof(NpParams) == 80, f"sizeof(NpParams) = {ctypes.sizeof(NpParams)} != 80"
assert ctypes.sizeof(NfpParams) == 152, f"sizeof(NfpParams) = {ctypes.sizeof(NfpParams)} != 152"


def check_struct_layout() -> list:
    """Return a list of layout errors (empty = OK)."""
    errs = []
    for cls, exp, size in ((NpParams, NP_OFFSETS, 80), (NfpParams, NFP_OFFSETS, 152)):
        if ctypes.sizeof(cls) != size:
            errs.append(f"sizeof({cls.__name__}) = {ctypes.sizeof(cls)} != {size}")
        names = [f[0] for f in cls._fields_]
        if names != list(exp):
            errs.append(f"{cls.__name__}: field order {names} != {list(exp)}")
        for name, off in exp.items():
            got = getattr(cls, name).offset
            if got != off:
                errs.append(f"{cls.__name__}.{name}: offset {got} != {off}")
    return errs


def params_struct(engine: str, values: dict):
    cls = NpParams if engine == ENGINE_NP else NfpParams
    p = cls()
    known = {f[0] for f in cls._fields_}
    for k, v in values.items():
        if k not in known:
            raise KeyError(f"unknown {cls.__name__} field '{k}'")
        setattr(p, k, v)
    return p


def struct_to_dict(p) -> dict:
    return {f[0]: getattr(p, f[0]) for f in p._fields_}


# ----------------------------------------------------------------------------------------------------
# DLL discovery + loading
# ----------------------------------------------------------------------------------------------------
def sha256_file(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _kind(kind: str) -> str:
    if kind in (ENGINE_NP, "np", "nest_physics"):
        return ENGINE_NP
    if kind in (ENGINE_NFP, "nfp"):
        return ENGINE_NFP
    raise ValueError(f"unknown engine kind '{kind}'")


def find_dll(kind: str, override=None) -> dict:
    """Locate an engine DLL. Order: explicit override -> env BENCH3DM_NP_DLL / BENCH3DM_NFP_DLL ->
    tools/bench3dm/out/dll_baseline/<name> -> src/.../build/Release/<name>.

    An explicit override / env path that does not exist is an error (no silent fallback).
    Returns {"path", "sha256", "mtime", "source"}. Raises FileNotFoundError."""
    kind = _kind(kind)
    cands = []
    if override:
        cands.append(("override", Path(override)))
    env = os.environ.get(DLL_ENV[kind])
    if env:
        cands.append((f"env:{DLL_ENV[kind]}", Path(env)))
    cands.append(("baseline", DLL_BASELINE_DIR / DLL_NAMES[kind]))
    cands.append(("build", DLL_FALLBACK[kind]))
    for src, p in cands:
        if p.is_file():
            p = p.resolve()
            st = p.stat()
            return {"path": str(p), "sha256": sha256_file(p),
                    "mtime": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(st.st_mtime)),
                    "source": src}
        if src == "override" or src.startswith("env:"):
            raise FileNotFoundError(f"{kind} DLL ({src}) not found: {p}")
    raise FileNotFoundError(f"{kind} DLL not found; tried: " + ", ".join(str(p) for _, p in cands))


_I = POINTER(c_int)
_D = POINTER(c_double)

NP_SYMBOLS = ["np_nest", "np_last_quality", "np_cancel", "np_cancel_reset", "np_progress"]
NP_OPTIONAL_SYMBOLS = ["np_nest_prio", "np_last_prio"]     # E1 (part priorities); older DLLs lack them
NFP_SYMBOLS = ["nfp_nest", "nfp_cancel", "nfp_cancel_reset", "nfp_progress", "nfp_fitness"]


def _load(path: str) -> ctypes.CDLL:
    p = str(Path(path).resolve())
    if hasattr(os, "add_dll_directory"):
        try:
            os.add_dll_directory(str(Path(p).parent))
        except OSError:
            pass
    return ctypes.CDLL(p)


def load_np(dll_path: str) -> ctypes.CDLL:
    lib = _load(dll_path)
    lib.np_nest.argtypes = [
        c_int, _I, _D, _I,                 # part_count, part_vertex_counts, part_xy, part_rotations
        c_int, _I, _D, _I, _I, _D,          # sheet_count, outer vcounts, outer xy, hole counts, hole vcounts, hole xy
        _I, _I, _D,                         # part_hole_counts, part_hole_vertex_counts, part_hole_xy
        POINTER(NpParams),
        _D, _D, _D, _I, _I,                 # out_tx, out_ty, out_angle(rad), out_sheet_id, out_n_sheets
    ]
    lib.np_nest.restype = c_int
    if hasattr(lib, "np_nest_prio"):        # E1: np_nest + part_priority right after part_rotations
        lib.np_nest_prio.argtypes = [
            c_int, _I, _D, _I, _I,             # part_count, vcounts, xy, part_rotations, part_priority
            c_int, _I, _D, _I, _I, _D,
            _I, _I, _D,
            POINTER(NpParams),
            _D, _D, _D, _I, _I,
        ]
        lib.np_nest_prio.restype = c_int
    if hasattr(lib, "np_last_prio"):
        lib.np_last_prio.argtypes = [_I, _I, _D, _D, _I]   # mandatory, missing, placed/offered area, retries
        lib.np_last_prio.restype = c_int
    lib.np_last_quality.argtypes = [_I, _I, _I, _I, _I]
    lib.np_last_quality.restype = c_int
    lib.np_cancel.argtypes = []
    lib.np_cancel.restype = None
    lib.np_cancel_reset.argtypes = []
    lib.np_cancel_reset.restype = None
    lib.np_progress.argtypes = []
    lib.np_progress.restype = c_longlong
    return lib


def load_nfp(dll_path: str) -> ctypes.CDLL:
    lib = _load(dll_path)
    lib.nfp_nest.argtypes = [
        c_int, _I, _D, _I, _I,              # part_count, vcounts, xy, quantities, rotations
        _I, _I, _D,                         # part_hole_counts, part_hole_vertex_counts, part_hole_xy
        c_int, _I, _D, _I, _I, _D,          # sheet_count, vcounts, xy, hole counts, hole vcounts, hole xy
        POINTER(NfpParams),
        _D, _D, _D, _I, _I, _I, _D,         # tx, ty, angle(deg), sheet_id, part_index, n_sheets, fitness
    ]
    lib.nfp_nest.restype = c_int
    lib.nfp_cancel.argtypes = []
    lib.nfp_cancel.restype = None
    lib.nfp_cancel_reset.argtypes = []
    lib.nfp_cancel_reset.restype = None
    lib.nfp_progress.argtypes = []
    lib.nfp_progress.restype = c_longlong
    lib.nfp_fitness.argtypes = []
    lib.nfp_fitness.restype = c_double
    return lib


def load_engine(engine: str, dll_path: str) -> ctypes.CDLL:
    return load_np(dll_path) if engine == ENGINE_NP else load_nfp(dll_path)


# ----------------------------------------------------------------------------------------------------
# Configs
# ----------------------------------------------------------------------------------------------------
def load_configs(path=CONFIGS_PATH) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def resolve_config(name: str, configs=None) -> dict:
    """Resolve `inherit` chains. Returns {"name", "engine", "sheets", "params", "chain"}."""
    configs = configs if configs is not None else load_configs()
    chain, cur, seen = [], name, set()
    while cur is not None:
        if cur in seen:
            raise ValueError(f"config inheritance cycle at '{cur}'")
        if cur not in configs or cur.startswith("_"):
            known = ", ".join(k for k in configs if not k.startswith("_"))
            raise KeyError(f"unknown config '{cur}' (known: {known})")
        seen.add(cur)
        chain.append(cur)
        cur = configs[cur].get("inherit")
    out = {"name": name, "engine": None, "sheets": None, "params": {}, "chain": chain}
    for c in reversed(chain):
        for k, v in configs[c].items():
            if k == "params":
                out["params"].update(v)
            elif k not in ("inherit", "description"):
                out[k] = v
    if out["engine"] not in (ENGINE_NP, ENGINE_NFP):
        raise ValueError(f"config '{name}': engine must be {ENGINE_NP} or {ENGINE_NFP}")
    return out


def apply_overrides(cfg: dict, sets) -> dict:
    """--set key=value: params struct field (typed from the ctypes field) or 'sheets'."""
    cls = NpParams if cfg["engine"] == ENGINE_NP else NfpParams
    types = {f[0]: f[1] for f in cls._fields_}
    for s in sets or []:
        if "=" not in s:
            raise ValueError(f"--set expects key=value, got '{s}'")
        k, v = (t.strip() for t in s.split("=", 1))
        if k == "sheets":
            cfg["sheets"] = int(v)
        elif k in types:
            cfg["params"][k] = float(v) if types[k] is c_double else int(float(v))
        else:
            raise KeyError(f"--set: unknown {cls.__name__} field '{k}'")
    return cfg


# ----------------------------------------------------------------------------------------------------
# Flattening
# ----------------------------------------------------------------------------------------------------
def _ring(pts) -> np.ndarray:
    a = np.asarray(pts, dtype=np.float64).reshape(-1, 2)
    if len(a) > 1 and np.array_equal(a[0], a[-1]):   # tolerate a closing duplicate
        a = a[:-1]
    if len(a) < 3:
        raise ValueError(f"degenerate ring with {len(a)} vertices")
    return a


def _i32(x) -> np.ndarray:
    a = np.ascontiguousarray(np.asarray(x, dtype=np.int32).ravel())
    return a if a.size else np.zeros(1, dtype=np.int32)      # never hand the ABI a NULL/empty array


def _f64(x) -> np.ndarray:
    a = np.ascontiguousarray(np.asarray(x, dtype=np.float64).ravel())
    return a if a.size else np.zeros(1, dtype=np.float64)


def _pi(a: np.ndarray):
    return a.ctypes.data_as(_I)


def _pd(a: np.ndarray):
    return a.ctypes.data_as(_D)


def _solve_geom(part: dict):
    s = part.get("solve") or {}
    outer = _ring(s["outer"] if "outer" in s else part["outer"])
    holes = [_ring(h) for h in (s["holes"] if "holes" in s else part.get("holes", [])) or []]
    return outer, holes


def _sheets(instance: dict, cfg: dict):
    ss = instance["solve_sheet"]
    w, h = float(ss["w"]), float(ss["h"])
    n = int(cfg.get("sheets") or instance["sheet"]["count"])
    return n, w, h, [0.0, 0.0, w, 0.0, w, h, 0.0, h]


class NpInputs:
    """Flat np_nest arrays: one solver part per COPY (qty expanded, part-major); part holes part-major;
    N identical sheets (0,0)-(w,h), no sheet holes. Arrays are kept alive on the object.

    `prio` (E1): optional int32 per copy (0 = normal, 1 = mandatory) -> solve_np calls np_nest_prio. It is
    built only when a part carries a "priority" key or `mandatory` (a set of part ids) is given; else None
    and solve_np calls np_nest exactly as before."""

    def __init__(self, instance: dict, cfg: dict, mandatory=None):
        pvc, pxy, prot, phc, phvc, phxy, self.copies = [], [], [], [], [], [], []
        prio, use_prio = [], mandatory is not None
        mand = {int(i) for i in mandatory} if mandatory is not None else set()
        for part in instance["parts"]:
            outer, holes = _solve_geom(part)
            rot = max(int(part.get("rotations", 0) or 0), 0)
            if "priority" in part:
                use_prio = True
            pr = 1 if (int(part.get("priority", 0) or 0) > 0 or int(part["id"]) in mand) else 0
            for c in range(int(part.get("qty", 1))):
                self.copies.append((int(part["id"]), c))
                pvc.append(len(outer))
                pxy.append(outer.ravel())
                prot.append(rot)
                prio.append(pr)
                phc.append(len(holes))
                for hr in holes:
                    phvc.append(len(hr))
                    phxy.append(hr.ravel())
        self.part_count = len(self.copies)
        self.pvc, self.prot = _i32(pvc), _i32(prot)
        self.prio = _i32(prio) if use_prio else None
        self.pxy = _f64(np.concatenate(pxy) if pxy else [])
        self.phc, self.phvc = _i32(phc), _i32(phvc)
        self.phxy = _f64(np.concatenate(phxy) if phxy else [])
        self.sheet_count, self.sheet_w, self.sheet_h, rect = _sheets(instance, cfg)
        self.sovc = _i32([4] * self.sheet_count)
        self.soxy = _f64(rect * self.sheet_count)
        self.shc = _i32([0] * self.sheet_count)
        self.hvc = _i32([])
        self.hxy = _f64([])


class NfpInputs:
    """Flat nfp_nest arrays: one entry per PART with its quantity; part holes part-major;
    N identical sheets (0,0)-(w,h), no sheet holes."""

    def __init__(self, instance: dict, cfg: dict):
        pvc, pxy, pq, prot, phc, phvc, phxy, self.part_ids = [], [], [], [], [], [], [], []
        for part in instance["parts"]:
            outer, holes = _solve_geom(part)
            self.part_ids.append(int(part["id"]))
            pvc.append(len(outer))
            pxy.append(outer.ravel())
            pq.append(max(int(part.get("qty", 1)), 1))
            prot.append(max(int(part.get("rotations", 0) or 0), 0))
            phc.append(len(holes))
            for hr in holes:
                phvc.append(len(hr))
                phxy.append(hr.ravel())
        self.part_count = len(self.part_ids)
        self.instance_count = int(sum(pq))
        self.pvc, self.pq, self.prot = _i32(pvc), _i32(pq), _i32(prot)
        self.pxy = _f64(np.concatenate(pxy) if pxy else [])
        self.phc, self.phvc = _i32(phc), _i32(phvc)
        self.phxy = _f64(np.concatenate(phxy) if phxy else [])
        self.sheet_count, self.sheet_w, self.sheet_h, rect = _sheets(instance, cfg)
        self.svc = _i32([4] * self.sheet_count)
        self.sxy = _f64(rect * self.sheet_count)
        self.shc = _i32([0] * self.sheet_count)
        self.shvc = _i32([])
        self.shxy = _f64([])


def flatten_np(instance: dict, cfg: dict, mandatory=None) -> NpInputs:
    """mandatory: optional iterable of part ids flagged mandatory (E1); a part's own "priority" key works too."""
    return NpInputs(instance, cfg, mandatory)


def flatten_nfp(instance: dict, cfg: dict) -> NfpInputs:
    return NfpInputs(instance, cfg)


# ----------------------------------------------------------------------------------------------------
# Solving (main thread; a Timer thread fires the cooperative cancel; ctypes releases the GIL)
# ----------------------------------------------------------------------------------------------------
class _CapTimer:
    def __init__(self, cap_s: float, cancel_fn):
        self.fired = False
        self._fn = cancel_fn
        self._t = threading.Timer(cap_s, self._fire) if cap_s and cap_s > 0 else None
        if self._t:
            self._t.daemon = True

    def _fire(self):
        self.fired = True
        self._fn()

    def __enter__(self):
        if self._t:
            self._t.start()
        return self

    def __exit__(self, *exc):
        if self._t:
            self._t.cancel()


class _Progress:
    def __init__(self, enabled: bool, fn, label: str, every_s: float = 15.0):
        self.enabled, self.fn, self.label, self.every = enabled, fn, label, every_s
        self._stop = threading.Event()

    def __enter__(self):
        if self.enabled:
            t0 = time.perf_counter()

            def loop():
                while not self._stop.wait(self.every):
                    print(f"[{self.label}] t={time.perf_counter() - t0:7.1f}s progress={self.fn()}",
                          file=sys.stderr, flush=True)
            threading.Thread(target=loop, daemon=True).start()
        return self

    def __exit__(self, *exc):
        self._stop.set()


def solve_np(lib, inputs: NpInputs, params: NpParams, cap_s: float, progress: bool = False) -> dict:
    n = inputs.part_count
    tx, ty, ang = np.zeros(n), np.zeros(n), np.zeros(n)
    sid = np.full(n, -1, dtype=np.int32)
    nsh = np.zeros(1, dtype=np.int32)
    lib.np_cancel_reset()
    with _CapTimer(cap_s, lib.np_cancel) as timer, _Progress(progress, lib.np_progress, "np"):
        t0 = time.perf_counter()
        prio = getattr(inputs, "prio", None)
        if prio is not None:
            if not hasattr(lib, "np_nest_prio"):
                raise RuntimeError("inputs.prio is set but this nest_physics.dll has no np_nest_prio (pre-E1 build)")
            rc = lib.np_nest_prio(n, _pi(inputs.pvc), _pd(inputs.pxy), _pi(inputs.prot), _pi(prio),
                                  inputs.sheet_count, _pi(inputs.sovc), _pd(inputs.soxy), _pi(inputs.shc),
                                  _pi(inputs.hvc), _pd(inputs.hxy),
                                  _pi(inputs.phc), _pi(inputs.phvc), _pd(inputs.phxy),
                                  ctypes.byref(params),
                                  _pd(tx), _pd(ty), _pd(ang), _pi(sid), _pi(nsh))
        else:
            rc = lib.np_nest(n, _pi(inputs.pvc), _pd(inputs.pxy), _pi(inputs.prot),
                             inputs.sheet_count, _pi(inputs.sovc), _pd(inputs.soxy), _pi(inputs.shc),
                             _pi(inputs.hvc), _pd(inputs.hxy),
                             _pi(inputs.phc), _pi(inputs.phvc), _pd(inputs.phxy),
                             ctypes.byref(params),
                             _pd(tx), _pd(ty), _pd(ang), _pi(sid), _pi(nsh))
        wall = time.perf_counter() - t0
    q = [c_int(-1) for _ in range(5)]
    clean = lib.np_last_quality(*[ctypes.byref(x) for x in q])
    rounds = int(lib.np_progress())
    lib.np_cancel_reset()
    quality = dict(zip(["overlap_pairs", "oob", "unplaced", "cancelled", "demoted"], [x.value for x in q]))
    quality["clean"] = int(clean)
    if getattr(inputs, "prio", None) is not None and hasattr(lib, "np_last_prio"):
        pm, pmiss, pa, po, pr = c_int(0), c_int(0), c_double(0.0), c_double(0.0), c_int(0)
        lib.np_last_prio(ctypes.byref(pm), ctypes.byref(pmiss), ctypes.byref(pa), ctypes.byref(po), ctypes.byref(pr))
        quality["mandatory"], quality["mandatory_missing"] = pm.value, pmiss.value
        quality["prio_retries"] = pr.value
        quality["placed_area"], quality["offered_area"] = pa.value, po.value
    return {"rc": int(rc), "ok": rc == 0, "wall_s": wall, "tx": tx, "ty": ty, "angle_rad": ang,
            "sheet": sid, "n_sheets": int(nsh[0]), "quality": quality, "rounds": rounds,
            "cancelled": bool(timer.fired or quality["cancelled"] == 1)}


def solve_np_warm(lib, inputs: NpInputs, params: NpParams, warm_placements: dict,
                  cap_s: float, progress: bool = False) -> dict:
    """Opt-in complete one-sheet repair, starting from movable incumbent transforms.

    Seeds are keyed by part ID (quantity one) or (part ID, copy index). Missing
    copies are explicitly marked absent; the native entry point must initialize
    all of them before optimizing. A failed repair returns the original seed.
    Existing np_nest entry points are unchanged.
    """
    if not hasattr(lib, "np_nest_warm_one"):
        raise RuntimeError("this DLL does not export np_nest_warm_one")
    fn = lib.np_nest_warm_one
    fn.argtypes = list(lib.np_nest.argtypes[:4]) + [POINTER(c_int), POINTER(c_double),
                    POINTER(c_double), POINTER(c_double)] + list(lib.np_nest.argtypes[4:])
    fn.restype = c_int
    n = inputs.part_count
    present = np.zeros(n, dtype=np.int32)
    seed_x, seed_y, seed_a = np.zeros(n), np.zeros(n), np.zeros(n)
    copies = list(inputs.copies)
    known = set(copies) | {pid for pid, _ in copies}
    if set(warm_placements) - known:
        raise ValueError("warm seed contains unknown part/copy IDs")
    for i, (pid, copy_index) in enumerate(copies):
        key = (pid, copy_index)
        if key not in warm_placements:
            if pid not in warm_placements:
                continue
            if sum(p == pid for p, _ in copies) != 1:
                raise ValueError("repeated parts require (part ID, copy index) warm keys")
            key = pid
        value = warm_placements[key]
        if len(value) != 3 or not all(math.isfinite(v) for v in value):
            raise ValueError("warm transform must contain finite tx, ty and angle")
        present[i] = 1
        seed_x[i], seed_y[i], seed_a[i] = value
    tx, ty, ang = np.zeros(n), np.zeros(n), np.zeros(n)
    sid, nsh = np.full(n, -1, dtype=np.int32), np.zeros(1, dtype=np.int32)
    lib.np_cancel_reset()
    with _CapTimer(cap_s, lib.np_cancel) as timer, _Progress(progress, lib.np_progress, "np-warm"):
        t0 = time.perf_counter()
        rc = fn(n, _pi(inputs.pvc), _pd(inputs.pxy), _pi(inputs.prot),
                _pi(present), _pd(seed_x), _pd(seed_y), _pd(seed_a),
                inputs.sheet_count, _pi(inputs.sovc), _pd(inputs.soxy), _pi(inputs.shc),
                _pi(inputs.hvc), _pd(inputs.hxy), _pi(inputs.phc), _pi(inputs.phvc), _pd(inputs.phxy),
                ctypes.byref(params), _pd(tx), _pd(ty), _pd(ang), _pi(sid), _pi(nsh))
        wall = time.perf_counter() - t0
    q = [c_int(-1) for _ in range(5)]
    clean = lib.np_last_quality(*[ctypes.byref(x) for x in q])
    rounds = int(lib.np_progress())
    lib.np_cancel_reset()
    quality = dict(zip(["overlap_pairs", "oob", "unplaced", "cancelled", "demoted"], [x.value for x in q]))
    quality["clean"] = int(clean)
    return {"rc": int(rc), "ok": rc == 0, "wall_s": wall, "tx": tx, "ty": ty, "angle_rad": ang,
            "sheet": sid, "n_sheets": int(nsh[0]), "quality": quality, "rounds": rounds,
            "cancelled": bool(timer.fired or quality["cancelled"] == 1)}


def solve_nfp(lib, inputs: NfpInputs, params: NfpParams, cap_s: float, progress: bool = False) -> dict:
    n = inputs.instance_count
    tx, ty, ang = np.zeros(n), np.zeros(n), np.zeros(n)
    sid = np.full(n, -1, dtype=np.int32)
    pidx = np.full(n, -1, dtype=np.int32)
    nsh = np.zeros(1, dtype=np.int32)
    fit = np.zeros(1, dtype=np.float64)
    lib.nfp_cancel_reset()
    with _CapTimer(cap_s, lib.nfp_cancel) as timer, _Progress(progress, lib.nfp_progress, "nfp"):
        t0 = time.perf_counter()
        placed = lib.nfp_nest(inputs.part_count, _pi(inputs.pvc), _pd(inputs.pxy), _pi(inputs.pq),
                              _pi(inputs.prot), _pi(inputs.phc), _pi(inputs.phvc), _pd(inputs.phxy),
                              inputs.sheet_count, _pi(inputs.svc), _pd(inputs.sxy), _pi(inputs.shc),
                              _pi(inputs.shvc), _pd(inputs.shxy),
                              ctypes.byref(params),
                              _pd(tx), _pd(ty), _pd(ang), _pi(sid), _pi(pidx), _pi(nsh), _pd(fit))
        wall = time.perf_counter() - t0
    gens = int(lib.nfp_progress())
    lib.nfp_cancel_reset()
    return {"rc": int(placed), "ok": placed >= 0, "wall_s": wall, "tx": tx, "ty": ty,
            "angle_rad": np.radians(ang), "sheet": sid, "part_index": pidx,
            "n_sheets": int(nsh[0]), "fitness": float(fit[0]), "placed": int(placed),
            "generations_done": gens, "cancelled": bool(timer.fired)}


# ----------------------------------------------------------------------------------------------------
# Output
# ----------------------------------------------------------------------------------------------------
def _f(x) -> float:
    x = float(x)
    return 0.0 if x == 0.0 else x          # drop -0.0 so identical layouts serialise identically


def to_placements(engine: str, instance_path: str, cfg: dict, seed: int, dll: dict, inputs, res: dict,
                  params) -> dict:
    instances = []
    if engine == ENGINE_NP:
        keys = list(inputs.copies)
        report = {"rc": res["rc"], "np_last_quality": res["quality"], "rounds": res["rounds"]}
    else:
        keys, counter = [], {}
        for k in range(inputs.instance_count):
            pi = int(res["part_index"][k])
            if not 0 <= pi < inputs.part_count:
                raise RuntimeError(f"nfp_nest returned out-of-range part index {pi} at instance {k}")
            pid = inputs.part_ids[pi]
            keys.append((pid, counter.get(pid, 0)))
            counter[pid] = counter.get(pid, 0) + 1
        report = {"rc": res["rc"], "placed": res["placed"], "fitness": res["fitness"],
                  "n_sheets": res["n_sheets"], "generations_done": res["generations_done"]}
    for k, (pid, c) in enumerate(keys):
        s = int(res["sheet"][k])
        instances.append({"part": pid, "copy": c, "sheet": s if s >= 0 else -1,
                          "tx": _f(res["tx"][k]), "ty": _f(res["ty"][k]),
                          "angle_rad": _f(res["angle_rad"][k])})
    used = sorted({i["sheet"] for i in instances if i["sheet"] >= 0})
    n_sheets = res["n_sheets"] if res["n_sheets"] > 0 else len(used)
    return {
        "schema": PLACEMENTS_SCHEMA,
        "instance": str(Path(instance_path).resolve()),
        "engine": engine,
        "config": cfg["name"],
        "seed": int(seed),
        "dll": {"path": dll["path"], "sha256": dll["sha256"], "mtime": dll.get("mtime")},
        "wall_s": round(float(res["wall_s"]), 3),
        "cancelled": bool(res["cancelled"]),
        "engine_report": report,
        "frame": FRAME,
        "n_sheets": int(n_sheets),
        "sheets_used": used,
        "sheets_given": int(inputs.sheet_count),
        "params": struct_to_dict(params),
        "instances": instances,
    }


def validate_placements(doc: dict, instance=None) -> list:
    """Minimal schema check; returns a list of problems (empty = valid)."""
    errs = []
    req = {"schema": str, "instance": str, "engine": str, "config": str, "seed": int, "dll": dict,
           "wall_s": (int, float), "cancelled": bool, "engine_report": dict, "frame": str,
           "n_sheets": int, "instances": list}
    for k, t in req.items():
        if k not in doc:
            errs.append(f"missing key '{k}'")
        elif not isinstance(doc[k], t):
            errs.append(f"key '{k}' has type {type(doc[k]).__name__}")
    if errs:
        return errs
    if doc["schema"] != PLACEMENTS_SCHEMA:
        errs.append(f"schema {doc['schema']!r}")
    if doc["engine"] not in (ENGINE_NP, ENGINE_NFP):
        errs.append(f"engine {doc['engine']!r}")
    if doc["frame"] != FRAME:
        errs.append("frame string differs")
    for k in ("path", "sha256"):
        if not isinstance(doc["dll"].get(k), str):
            errs.append(f"dll.{k} missing")
    if doc["engine"] == ENGINE_NP and "np_last_quality" not in doc["engine_report"]:
        errs.append("engine_report.np_last_quality missing")
    seen = set()
    for i, it in enumerate(doc["instances"]):
        for k, t in (("part", int), ("copy", int), ("sheet", int), ("tx", float), ("ty", float),
                     ("angle_rad", float)):
            v = it.get(k)
            if not isinstance(v, t) or isinstance(v, bool):
                errs.append(f"instances[{i}].{k} bad: {v!r}")
            elif t is float and not math.isfinite(v):
                errs.append(f"instances[{i}].{k} not finite")
        key = (it.get("part"), it.get("copy"))
        if key in seen:
            errs.append(f"duplicate instance {key}")
        seen.add(key)
    if instance is not None:
        want = {(int(p["id"]), c) for p in instance["parts"] for c in range(int(p.get("qty", 1)))}
        if seen != want:
            errs.append(f"instance set mismatch: missing {sorted(want - seen)[:5]} extra {sorted(seen - want)[:5]}")
    return errs


def run(instance_path: str, config_name: str, seed: int, cap_s: float, out_dir: str,
        dll_override=None, sets=None, progress: bool = False):
    """Run one solve and write <out_dir>/placements.json. Returns (placements_path, doc)."""
    with open(instance_path, "r", encoding="utf-8") as f:
        instance = json.load(f)
    if instance.get("schema") != INSTANCE_SCHEMA:
        print(f"warning: instance schema {instance.get('schema')!r} != {INSTANCE_SCHEMA!r}", file=sys.stderr)
    cfg = apply_overrides(resolve_config(config_name), sets or [])
    engine = cfg["engine"]
    pvals = dict(cfg["params"])
    pvals["seed"] = int(seed)
    params = params_struct(engine, pvals)
    if engine == ENGINE_NP and params.iter_mode != 0 and params.iter_budget < 500:
        print(f"warning: iter_budget {params.iter_budget} < 500 selects the engine's fast PREVIEW profile",
              file=sys.stderr)
    dll = find_dll(engine, dll_override)
    lib = load_engine(engine, dll["path"])
    inputs = flatten_np(instance, cfg) if engine == ENGINE_NP else flatten_nfp(instance, cfg)
    print(f"[engines] {engine} config={cfg['name']} seed={seed} cap={cap_s}s sheets={inputs.sheet_count} "
          f"({inputs.sheet_w}x{inputs.sheet_h}) parts={inputs.part_count} dll={dll['path']} "
          f"sha256={dll['sha256'][:12]}", flush=True)
    solve = solve_np if engine == ENGINE_NP else solve_nfp
    res = solve(lib, inputs, params, cap_s, progress)
    doc = to_placements(engine, instance_path, cfg, seed, dll, inputs, res, params)
    if not res["ok"]:
        doc["error"] = f"{engine} returned {res['rc']}"
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    path = out / "placements.json"
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    placed = sum(1 for i in doc["instances"] if i["sheet"] >= 0)
    print(f"[engines] rc={res['rc']} wall={res['wall_s']:.2f}s n_sheets={doc['n_sheets']} "
          f"placed={placed}/{len(doc['instances'])} cancelled={doc['cancelled']} "
          f"report={json.dumps(doc['engine_report'])}", flush=True)
    return str(path), doc


# ----------------------------------------------------------------------------------------------------
# Self-test + CLI
# ----------------------------------------------------------------------------------------------------
def selftest() -> int:
    ok = True
    print(f"python {sys.version.split()[0]}  pointer={ctypes.sizeof(ctypes.c_void_p) * 8}-bit")
    for cls, exp in ((NpParams, NP_OFFSETS), (NfpParams, NFP_OFFSETS)):
        print(f"sizeof({cls.__name__}) = {ctypes.sizeof(cls)}")
        print("  " + " ".join(f"{k}@{getattr(cls, k).offset}" for k in exp))
    errs = check_struct_layout()
    for e in errs:
        print("  LAYOUT ERROR:", e)
    print("struct layout:", "OK" if not errs else "FAIL")
    ok &= not errs
    for kind, syms, loader in ((ENGINE_NP, NP_SYMBOLS, load_np), (ENGINE_NFP, NFP_SYMBOLS, load_nfp)):
        try:
            d = find_dll(kind)
            lib = loader(d["path"])
            missing = []
            for s in syms:
                try:
                    getattr(lib, s)
                except AttributeError:
                    missing.append(s)
            print(f"{kind}: loaded {d['path']} [{d['source']}] sha256={d['sha256']} mtime={d['mtime']}")
            print("  symbols: " + ", ".join(s + ("=MISSING" if s in missing else "=ok") for s in syms))
            ok &= not missing
        except Exception as e:  # noqa: BLE001
            print(f"{kind}: LOAD FAILED: {e}")
            ok = False
    try:
        cfgs = load_configs()
        names = [k for k in cfgs if not k.startswith("_")]
        for n in names:
            c = resolve_config(n, cfgs)
            params_struct(c["engine"], c["params"])
        print(f"configs: {', '.join(names)} (all resolve)")
    except Exception as e:  # noqa: BLE001
        print(f"configs: FAILED: {e}")
        ok = False
    print("SELFTEST", "PASS" if ok else "FAIL")
    return EXIT_OK if ok else EXIT_FAIL


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Headless OpenNest engine driver (np_nest / nfp_nest).")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--instance")
    ap.add_argument("--config", default="gh_np")
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--cap", type=float, default=900.0, help="wall-clock cap (s) -> cooperative cancel")
    ap.add_argument("--out")
    ap.add_argument("--dll", help="explicit engine DLL path (overrides env/baseline/build)")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="override a params struct field (or 'sheets'); repeatable")
    ap.add_argument("--progress", action="store_true", help="print engine progress to stderr every 15 s")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.instance or not a.out:
        ap.error("--instance and --out are required (or use --selftest)")
    try:
        _, doc = run(a.instance, a.config, a.seed, a.cap, a.out, a.dll, a.set, a.progress)
    except Exception as e:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        print(f"[engines] FAILED: {e}", file=sys.stderr)
        return EXIT_FAIL
    if "error" in doc:
        return EXIT_FAIL
    return EXIT_CANCELLED if doc["cancelled"] else EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
