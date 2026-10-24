#!/usr/bin/env python3
"""Generator of the "block code → colour" table from the assets of an installed Vintage Story.

Why: the map needs the CORRECT colour of a block. The semantic palette (BlockColorPalette)
guesses the colour from the name, so dozens of different materials (cobblestone-claystone,
cobblestone-chert, stonebricks-granite, ...) all collapsed into one grey. Here we
take the real game textures and compute the average colour.

What it does:
  1. Reads blocktypes/*.json (VS uses "relaxed" JSON: comments, unquoted keys).
  2. Expands variantgroups (states or loadFromProperties → worldproperties/block/*.json).
  3. Resolves the texture path (all → up → side → first), substitutes {variants} and `*` (glob).
  4. Computes AvgColor the way the game does (4 pixels at 35%/65%), and for blocks with grass
     coverage — also the base and the layer weight (BlockWithGrassOverlay/BlockForestFloor), and writes
     lines `code=RRGGBB|rain|climate|season|samples[|base|weight]`.

The result is VSCore/BlockColors.txt (an embedded resource). Modded blocks that are not in the
vanilla assets keep using the semantic palette.

Usage:  python3 tools/gen_block_colors.py [assets_path] [output_file]
"""

import glob
import json
import os
import random
import re
import sys
from itertools import product

DEFAULT_ROOT = os.path.expanduser("~/Downloads/vintagestory/assets")
DOMAINS = ("survival", "game", "creative")

# Debug/technical blocks with a gigantic product of variants (blockideater — 83k codes)
# and blocks that never appear on the surface map.
SKIP_CODE_PREFIXES = ("blockideater",)
MAX_VARIANTS = 512


# ---------------------------------------------------------------- relaxed JSON
def strip_comments(text: str) -> str:
    out, i, n = [], 0, len(text)
    in_str = False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if c == '"':
            in_str = True
            out.append(c)
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def load_relaxed(path: str):
    try:
        text = open(path, encoding="utf-8-sig").read()
    except OSError:
        return None
    text = strip_comments(text)
    text = re.sub(r",(\s*[}\]])", r"\1", text)                       # trailing commas
    text = re.sub(r"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)(\s*:)", r'\1"\2"\3', text)   # unquoted keys
    try:
        return json.loads(text)
    except Exception:
        return None


# ---------------------------------------------------------------- block variants
_props_cache = {}


def property_values(root: str, code: str):
    if code in _props_cache:
        return _props_cache[code]
    parts = code.split("/")
    name = parts[-1]
    category = parts[0] if len(parts) > 1 else "block"
    vals = []
    for domain in DOMAINS:
        found_domain = False
        for rel in (f"worldproperties/{category}/{name}.json", f"worldproperties/block/{name}.json"):
            path = os.path.join(root, domain, rel)
            if not os.path.exists(path):
                continue
            obj = load_relaxed(path)
            if not obj:
                continue
            for v in obj.get("variants", []):
                c = (v.get("Code") or v.get("code")) if isinstance(v, dict) else v
                if isinstance(c, str):
                    vals.append(c)
            found_domain = True
            break
        if found_domain:
            break
    _props_cache[code] = vals
    return vals


def block_variants(root: str, obj: dict):
    """List of (code, substitutions) for all variants of the block."""
    groups = []
    for g in obj.get("variantgroups") or []:
        if not isinstance(g, dict):
            return None
        props = g.get("loadFromProperties")
        gcode = g.get("code")
        if not gcode and isinstance(props, str):
            gcode = props.split(":")[-1].split("/")[-1]      # soil -> fertility/grasscoverage
        if not gcode:
            return None
        # VS: loadFromProperties defines the values, `states` ADDS its own (e.g. stonebricks:
        # rock + suevite/travertine; drystone: rockwithdeposit + mixed)
        values = []
        if isinstance(props, str):
            values += property_values(root, props.split(":")[-1])
        if isinstance(g.get("states"), list):
            for s in g["states"]:
                v = s if isinstance(s, str) else (s.get("code") if isinstance(s, dict) else None)
                if isinstance(v, str) and v not in values:
                    values.append(v)
        if not values:
            return None
        groups.append((gcode, values))

    pattern_code = obj.get("code")
    if not isinstance(pattern_code, str):
        return None
    if not groups:
        return [(pattern_code, {})]

    skip = [s for s in (obj.get("skipVariants") or []) if isinstance(s, str)]
    out = []
    for combo in product(*[v for _, v in groups]):
        subs = {groups[i][0]: combo[i] for i in range(len(combo))}
        full = pattern_code + "-" + "-".join(combo)
        if any(_match_skip(skip, full) for s in skip):
            continue
        out.append((full, subs))
    return out


def _match_skip(patterns, code: str) -> bool:
    for p in patterns:
        rx = "^" + re.escape(p).replace(r"\*", ".*") + "$"
        if re.match(rx, code):
            return True
    return False


# ---------------------------------------------------------------- textures
def pattern_matches(pat: str, code: str) -> bool:
    """Block type pattern: `rock-*` (glob) or `@rock-(a|b)` (regex), `*` — everything."""
    if not isinstance(pat, str):
        return False
    if pat.startswith("@"):
        try:
            return re.fullmatch(pat[1:], code) is not None
        except re.error:
            return False
    return re.fullmatch(re.escape(pat).replace(r"\*", ".*"), code) is not None


def texture_candidates(obj: dict, code: str):
    """Texture candidates in descending priority: top face (`up`), grass coverage
    (`specialSecondTexture`), then `all`/`side`. If the first variant has no file
    (e.g. `specialSecondTexture` for `-none`), the next one is taken.

    Separately — `largeleavesByType`: foliage defines textures not via `textures` but via
    its own keys (the tessellator reads them by name from the shape), so without this branch
    foliage has no colour at all and the map drew it in semantic green."""
    cands = []
    tex = obj.get("textures")
    # For foliage these keys live INSIDE textures (`textures: { largeleavesByType: ... }`),
    # and Block itself assembles the "largeleaves"/"smallleaves" textures from them.
    for holder in (obj, tex):
        if not isinstance(holder, dict):
            continue
        for key in ("largeleavesByType", "smallleavesByType"):
            by_type = holder.get(key)
            if not isinstance(by_type, dict):
                continue
            best, best_score = None, -1
            for pat, val in by_type.items():
                if not isinstance(val, dict) or not pattern_matches(pat, code):
                    continue
                score = 0 if pat.startswith("@") else (2 if "*" not in pat else 1)
                score = score * 100 + len(pat)
                if score > best_score:
                    best, best_score = val, score
            if best:
                cands += _texture_bases(best)
    if isinstance(tex, dict):
        cands += _texture_bases(tex)
    by_type = obj.get("texturesByType")
    if isinstance(by_type, dict):
        best, best_score = None, -1
        for pat, val in by_type.items():
            if not isinstance(val, dict) or not pattern_matches(pat, code):
                continue
            score = 0 if pat.startswith("@") else (2 if "*" not in pat else 1)
            score = score * 100 + len(pat)          # specificity: exact > glob > regex > *
            if score > best_score and _texture_bases(val):
                best, best_score = val, score
        if best:
            cands = _texture_bases(best) + cands
    seen, out = set(), []
    for c in cands:
        if c not in seen:
            seen.add(c)
            out.append(c)
    return out


def _tex_value(v):
    if isinstance(v, str):
        return v
    if isinstance(v, dict):
        for key in ("base", "all", "up"):
            if isinstance(v.get(key), str):
                return v[key]
    return None


def _texture_bases(tex: dict):
    """Textures for the TOP view: `up` (top face) is more important than `all`; for blocks with grass
    coverage the top is `specialSecondTexture` (that is what is visible on the map)."""
    if not isinstance(tex, dict):
        return []
    out = []
    for key in ("up", "specialSecondTexture", "all", "side", "north", "base"):
        if key in tex:
            b = _tex_value(tex[key])
            if b:
                out.append(b)
    for v in tex.values():
        b = _tex_value(v)
        if b and b not in out:
            out.append(b)
    return out


def resolve_textures(root: str, base: str, subs: dict):
    path = base
    for k, v in subs.items():
        path = path.replace("{" + k + "}", v)
    path = path.split(":")[-1]
    if "{" in path:
        return []
    files = []
    for domain in DOMAINS:
        prefix = os.path.join(root, domain, "textures")
        if path.endswith("*"):
            files += glob.glob(os.path.join(prefix, path[:-1] + "*.png"))
        else:
            f = os.path.join(prefix, path + ".png")
            if os.path.exists(f):
                files.append(f)
        if files:
            break
    return files


SAMPLE_COUNT = 30


def texture_colours(files):
    """Average colour of the top face + random texel samples.

    The average colour is NOT the mean over all pixels: the game takes AvgColor as the mean of four
    pixels at 35%/65% (TextureAtlas.ComposeTextureAtlasses_StageC →
    ColorUtil.ColorAverage), and for textures with a large pattern (grass, forest floor)
    this differs noticeably from the full mean. The samples are there so the map is not flat:
    the game takes a random texel and overlays it on the average (ColorOverlay), which makes
    grass/stone "noisy" on the map. There are exactly 30 of them, like RndColors in TextureAtlas (random
    pixels with repetition, transparent ones skipped) — 6 ordered quantiles smoothed
    the distribution and gave less contrasty noise than the game.
    """
    from PIL import Image
    px_all = []
    picks = []
    for f in files:
        try:
            img = Image.open(f).convert("RGBA")
        except Exception:
            continue
        w, h = img.size
        # exactly the same points as TextureAtlas: full colour where alpha is present, otherwise
        # (a transparent pixel) the game's AvgColor also takes it as is — including 0.
        for fx, fy in ((0.35, 0.35), (0.65, 0.35), (0.35, 0.65), (0.65, 0.65)):
            picks.append(img.getpixel((int(fx * w), int(fy * h)))[:3])
        for r, g, b, a in img.getdata():
            if a < 8:
                continue
            px_all.append((r, g, b))
    if not picks or not px_all:
        return None
    avg = (sum(p[0] for p in picks) // len(picks), sum(p[1] for p in picks) // len(picks),
           sum(p[2] for p in picks) // len(picks))
    n = len(px_all)
    # RndColors in the game are 30 independent random pixels (with repetition), not quantiles:
    # quantiles clip the tails and understate the spread, which makes the map look smoother than the game.
    # A fixed seed makes the table reproducible; the game still has its own specific 30 colours
    # in every session, only their distribution density matters.
    rnd = random.Random(0x5EED)
    samples = [px_all[rnd.randrange(n)] for _ in range(SAMPLE_COUNT)]
    return avg, samples


def topsoil_parts(obj, code):
    """(base, overlay, weight) for blocks with grass coverage, otherwise None.

    Derived from the decompiled VSSurvivalMod.dll:
      BlockWithGrassOverlay.GetColor (BlockSoil/BlockSoilDeposit):
        none       -> GetColorWithoutTint (mean up, WITHOUT tint)
        normal     -> tint(mean specialSecondTexture)
        sparse     -> ColorOverlay(mean up, tint(mean specialSecondTexture), 0.75)
        verysparse -> ColorOverlay(mean up, tint(mean specialSecondTexture), 0.5)
      BlockForestFloor.GetColor: ColorOverlay(mean up, tint(mean
        block/plant/grasscoverage/normal), grass/7) — only the grass part is tinted,
        the base stays untouched.

    That is why the colour cannot be stored as a single number: the base and the grass layer are tinted differently."""
    cls = obj.get("class")
    tex = obj.get("textures")
    if not isinstance(tex, dict):
        return None
    if cls in ("BlockSoil", "BlockSoilDeposit"):
        coverage = code.rsplit("-", 1)[-1]
        weight = {"none": 0.0, "verysparse": 0.5, "sparse": 0.75, "normal": 1.0}.get(coverage)
        if weight is None:
            return None
        overlay = _tex_value(tex.get("specialSecondTexture"))
        base = _tex_value(tex.get("up")) or _tex_value(tex.get("all"))
        # GetRandomColor(UP) for BlockSoil takes ParticlesTextureCode: for grass variants
        # that is particletop (block/plant/grasscoverage/normal-top), for none — up.
        noise = _tex_value(tex.get("particletop")) if weight > 0 else None
        return (base, overlay, weight, noise or base)
    if cls == "BlockForestFloor":
        try:
            level = int(code.rsplit("-", 1)[-1])
        except ValueError:
            return None
        base = _tex_value(tex.get("up")) or _tex_value(tex.get("all"))
        # GetRandomColor takes mapColorTextureSubId — specialSecondTexture of the forestfloor-7 block,
        # that is forestoverlay7, regardless of the level.
        noise = _overlay_for_level(obj, 7) or "block/soil/forest/forestoverlay7"
        return (base, "block/plant/grasscoverage/normal", max(0, min(7, level)) / 7.0, noise)
    return None


def _overlay_for_level(obj, level: int):
    """`specialSecondTexture` of the forestfloor block of the requested level (for GetRandomColor)."""
    tex = obj.get("textures")
    if not isinstance(tex, dict):
        return None
    by_type = tex.get("specialSecondTextureByType")
    if isinstance(by_type, dict):
        val = by_type.get(f"*-{level}") or by_type.get("*")
        return _tex_value(val) if isinstance(val, dict) else None
    return _tex_value(tex.get("specialSecondTexture"))


NO_VALUE = ""   # the ByType pattern explicitly said "nothing" (e.g. `"*-none": null`)


def by_type_value(obj, key, code):
    """Value of a property like `climateColorMapByType`/`rainPermeableByType` for a variant.

    VS expands `*ByType` by pattern. The priority is the same as for textures: exact name,
    then a pattern without `*`, then glob/regex — otherwise `*` would override `*-none`.
    `null` in ByType means DELIBERATELY "no value", not "not set": without this
    `"*-none": null` on soil fell through to `"*": climatePlantTint`, and soil without grass
    got a grass tint (and on a slice — a colour 1.5 times darker than needed)."""
    table = obj.get(key)
    if isinstance(table, dict):
        best, best_score, found = None, -1, False
        for pat, val in table.items():
            if val is not None and not isinstance(val, (str, bool)):
                continue
            if not pattern_matches(pat, code):
                continue
            score = 1000 if pat == code else (500 if "*" not in pat and not pat.startswith("@") else 100)
            if score > best_score:
                best, best_score, found = val, score, True
        if found:
            return NO_VALUE if best is None else best
        return None
    val = obj.get(key)
    return val if isinstance(val, (str, bool)) else None


def resolve_colormap_texture(root, base):
    path = base.split(":")[-1]
    for domain in DOMAINS:
        f = os.path.join(root, domain, "textures", path + ".png")
        if os.path.exists(f):
            return f
    return None


def write_color_maps(root, out_path, wanted):
    """Climate and season tinting LUTs in a compact form.

    Climate maps in the assets are 264×264 (256×256 inside) — for sampling with a step
    of one they can be downsampled to 64×64: the map is smooth, the error is < 1/255. Seasonal ones are
    128×16, left as is: along the 16 rows there is noise, downsampling would eat it.
    """
    from PIL import Image
    entries = []
    for domain in DOMAINS:
        cfg = os.path.join(root, domain, "config", "colormaps.json")
        if os.path.exists(cfg):
            obj = load_relaxed(cfg)
            if isinstance(obj, list):
                entries += [e for e in obj if isinstance(e, dict)]
    out = []
    stats = {"maps": 0, "missing": 0, "skipped": 0}
    for e in entries:
        code = e.get("code")
        tex = e.get("texture")
        base = tex.get("base") if isinstance(tex, dict) else tex
        if not isinstance(code, str) or not isinstance(base, str) or code not in wanted:
            continue
        f = resolve_colormap_texture(root, base)
        if not f:
            stats["missing"] += 1
            continue
        img = Image.open(f).convert("RGB")
        pad = int(e.get("padding") or 0)
        w, h = img.size
        inner = img.crop((pad, pad, w - pad, h - pad))
        if code.startswith("climate") and inner.size != (64, 64):
            inner = inner.resize((64, 64), Image.BILINEAR)
        if inner.size != (128, 16) and not code.startswith("climate"):
            stats["skipped"] += 1
            continue
        out.append(f"{code}={inner.size[0]}x{inner.size[1]}\n")
        data = list(inner.getdata())
        iw, ih = inner.size
        for y in range(ih):
            out.append(",".join(f"{data[y * iw + x][0]:02x}{data[y * iw + x][1]:02x}{data[y * iw + x][2]:02x}"
                                for x in range(iw)) + "\n")
        stats["maps"] += 1
    with open(out_path, "w", encoding="utf-8") as f:
        f.write("# name=WxH, then H rows of W RRGGBB colours — climate/season\n")
        f.write("# tinting LUT from the Vintage Story assets (generated by tools/gen_block_colors.py)\n")
        f.writelines(out)
    print(f"[gen] colour maps={stats['maps']} missing file={stats['missing']} skipped={stats['skipped']}")
    print("[gen] ->", out_path, os.path.getsize(out_path), "bytes")


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_ROOT
    out_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "VSCore", "BlockColors.txt")
    maps_out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "VSCore", "ColorMaps.txt")
    if not os.path.isdir(root):
        print("assets not found:", root)
        return 1

    colours = {}
    meta = {}
    tex_cache = {}
    wanted_maps = set()
    stats = {"json": 0, "no_code": 0, "no_tex": 0, "no_variant": 0, "blocks": 0, "no_file": 0,
             "too_many": 0, "tinted": 0, "permeable": 0}

    for domain in DOMAINS:
        bt = os.path.join(root, domain, "blocktypes")
        for dirpath, _, files in os.walk(bt):
            for fn in files:
                if not fn.endswith(".json"):
                    continue
                obj = load_relaxed(os.path.join(dirpath, fn))
                stats["json"] += 1
                if not isinstance(obj, dict):
                    stats["no_code"] += 1
                    continue
                if isinstance(obj.get("code"), str) and obj["code"].startswith(SKIP_CODE_PREFIXES):
                    continue
                variants = block_variants(root, obj)
                if not variants:
                    stats["no_variant"] += 1
                    continue
                if len(variants) > MAX_VARIANTS:
                    stats["too_many"] += 1
                    continue
                for code, subs in variants:
                    stats["blocks"] += 1
                    parts = topsoil_parts(obj, code)
                    if parts and parts[1]:
                        base_col = overlay_col = noise_col = None
                        base_files = resolve_textures(root, parts[0], subs) if parts[0] else []
                        overlay_files = resolve_textures(root, parts[1], subs)
                        noise_files = resolve_textures(root, parts[3], subs) if parts[3] else []
                        for files, slot in ((base_files, "base"), (overlay_files, "overlay"), (noise_files, "noise")):
                            if not files:
                                continue
                            k = tuple(files)
                            if k not in tex_cache:
                                tex_cache[k] = texture_colours(files)
                            if slot == "base":
                                base_col = tex_cache[k]
                            elif slot == "overlay":
                                overlay_col = tex_cache[k]
                            else:
                                noise_col = tex_cache[k]
                        if base_col and (overlay_col or parts[2] == 0.0):
                            weight = parts[2]
                            if weight > 0 and overlay_col:
                                colours[code] = overlay_col[0]
                            else:
                                colours[code] = base_col[0]
                                weight = 0.0
                            # Samples — the GetRandomColor texture: the game blends GetColor with
                            # a random texel of this texture with weight 0.6 (colorRandomizationWeight).
                            samples = (noise_col or overlay_col or base_col)[1]
                            # The base is needed even at weight 0: forestfloor-0 GetColor does not tint at all
                            # (returns GetColorWithoutTint), and the renderer must know that.
                            meta_colour = base_col[0]
                            clim = by_type_value(obj, "climateColorMapByType", code)
                            if clim is None:
                                clim = by_type_value(obj, "climateColorMap", code)
                            seas = by_type_value(obj, "seasonColorMapByType", code)
                            if seas is None:
                                seas = by_type_value(obj, "seasonColorMap", code)
                            perm = by_type_value(obj, "rainPermeableByType", code)
                            if perm is None:
                                perm = by_type_value(obj, "rainPermeable", code)
                            if clim == NO_VALUE:
                                clim = None
                            if seas == NO_VALUE:
                                seas = None
                            if clim:
                                wanted_maps.add(clim)
                            if seas:
                                wanted_maps.add(seas)
                            if clim or seas:
                                stats["tinted"] += 1
                            if perm is True:
                                stats["permeable"] += 1
                            meta[code] = (clim or "-", seas or "-", 1 if perm is True else 0,
                                          samples, meta_colour, weight)
                            continue
                    cands = texture_candidates(obj, code)
                    if not cands:
                        stats["no_tex"] += 1
                        continue
                    stats_tex = None
                    found = False
                    for base in cands:
                        files = resolve_textures(root, base, subs)
                        if not files:
                            continue
                        found = True
                        key = tuple(files)
                        if key not in tex_cache:
                            tex_cache[key] = texture_colours(files)
                        if tex_cache[key]:          # for "none" coverage the texture is transparent -> take the next variant
                            stats_tex = tex_cache[key]
                            break
                    if not found:
                        stats["no_file"] += 1
                        continue
                    if not stats_tex:
                        continue
                    avg, samples = stats_tex
                    colours[code] = avg

                    clim = by_type_value(obj, "climateColorMapByType", code)
                    if clim is None:
                        clim = by_type_value(obj, "climateColorMap", code)
                    seas = by_type_value(obj, "seasonColorMapByType", code)
                    if seas is None:
                        seas = by_type_value(obj, "seasonColorMap", code)
                    perm = by_type_value(obj, "rainPermeableByType", code)
                    if perm is None:
                        perm = by_type_value(obj, "rainPermeable", code)
                    if clim == NO_VALUE:
                        clim = None
                    if seas == NO_VALUE:
                        seas = None
                    if clim:
                        wanted_maps.add(clim)
                    if seas:
                        wanted_maps.add(seas)
                    if clim or seas:
                        stats["tinted"] += 1
                    if perm is True:
                        stats["permeable"] += 1
                    meta[code] = (clim or "-", seas or "-", 1 if perm is True else 0, samples, None, 0.0)

    with open(out_path, "w", encoding="utf-8") as f:
        f.write("# code=RRGGBB[|rainPermeable|climateColorMap|seasonColorMap|sample1,...,sample30]\n")
        f.write("# average colour of the top face + map metadata from the Vintage Story assets\n")
        f.write("# generated by tools/gen_block_colors.py from the assets " +
                open(os.path.join(root, "version-1.22.7.txt")).read().strip() + "\n")
        for code in sorted(colours):
            r, g, b = colours[code]
            line = f"{code}={r:02x}{g:02x}{b:02x}"
            if code in meta:
                clim, seas, perm, samples, base, weight = meta[code]
                line += f"|{perm}|{clim}|{seas}|" + ",".join(f"{s[0]:02x}{s[1]:02x}{s[2]:02x}" for s in samples)
                if base is not None:
                    line += f"|{base[0]:02x}{base[1]:02x}{base[2]:02x}|{weight:.2f}"
            f.write(line + "\n")
    print(f"[gen] json={stats['json']} blocks={stats['blocks']} colours={len(colours)} "
          f"no texture={stats['no_tex']} no variants={stats['no_variant']} no file={stats['no_file']} "
          f"too many variants={stats['too_many']} tinted={stats['tinted']} "
          f"rain-permeable={stats['permeable']}")
    print("[gen] ->", out_path, os.path.getsize(out_path), "bytes")
    write_color_maps(root, maps_out, wanted_maps)
    for probe in ("rock-andesite", "cobblestone-claystone", "cobblestone-chert", "stonebricks-granite",
                  "planks-veryaged-we", "rock-granite", "soil-medium-normal", "water-still-7",
                  "sand", "gravel-andesite"):
        if probe in colours:
            r, g, b = colours[probe]
            print(f"    {probe:28} = ({r},{g},{b})  {meta.get(probe, ('-', '-', 0, []))[:3]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
