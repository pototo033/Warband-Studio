# 排查："信息不明"的兵是哪些、为什么
import io, os, collections

T = os.path.join(os.environ["TEMP"])

def load(path):
    rows = [l.rstrip("\n").split("\t") for l in io.open(path, encoding="utf-8", errors="replace")]
    hdr = None; data = []
    for r in rows:
        if not r or not any(c.strip() for c in r) or r[0].startswith("#"):
            continue
        if hdr is None:
            hdr = r; continue
        if r[0].strip():
            data.append(r)
    return hdr, data

def load_dir(d):
    out = []
    for fn in os.listdir(d):
        h, data = load(os.path.join(d, fn))
        out += [(h, r) for r in data]
    return out

# 1) 军事组授权（包内 + 原版）
mg = []
d = os.path.join(T, "wb3", "db", "units_to_groupings_military_permissions_tables")
if os.path.isdir(d):
    for h, r in load_dir(d):
        mg.append((r[0], r[1]))
h, data = load(os.path.join(T, "wb3_v_mg.tsv"))
mg += [(r[0], r[1]) for r in data]
print("军事组授权行：", len(mg), "（去重后", len(set(mg)), "）")

# 2) 派系 → 军事组
h, fac = load(os.path.join(T, "fac.tsv"))
ik, isub, img = h.index("key"), h.index("subculture"), h.index("military_group")
facGroup = {r[ik]: r[img] for r in fac if r[img].strip()}
facRace = {r[ik]: r[isub].replace("_pro_sc_", "_sc_") for r in fac}
groupFacs = collections.defaultdict(set)
for f, g in facGroup.items():
    groupFacs[g].add(f)

# 3) 建筑招募（faction 非空 / 为空）
h, bua = load(os.path.join(T, "bua.tsv"))
iu, iff, ie = h.index("unit"), h.index("faction"), h.index("enabled")
buaFac = collections.defaultdict(set)      # 有 faction 的
buaGeneric = set()                          # faction 为空的（通用建筑）
for r in bua:
    if r[ie].lower() != "true": continue
    if r[iff].strip(): buaFac[r[iff]].add(r[iu])
    else: buaGeneric.add(r[iu])
print("建筑招募：有 faction 的", len(buaFac), "个派系；faction 为空的行涉及", len(buaGeneric), "个兵")

# 4) 战帮树上的兵
h, junc = load(os.path.join(T, "wb", "db", "unit_to_unit_group_junctions_tables", os.listdir(os.path.join(T, "wb", "db", "unit_to_unit_group_junctions_tables"))[0]))
iu2, ig2 = h.index("unit"), h.index("unit_group")
tree = set(r[iu2] for r in junc if r[iu2].strip())
print("画布上的兵：", len(tree))

# 5) 逐个兵算归属（军事组 ∩ 派系 + 建筑 faction + 通用建筑）
mgUnits = collections.defaultdict(set)     # 军事组 → 兵
for u, g in mg:
    mgUnits[g].add(u)
unitFacs = collections.defaultdict(set)
for u, g in mg:
    unitFacs[u] |= groupFacs.get(g, set())
for f, us in buaFac.items():
    for u in us: unitFacs[u].add(f)

unknown = [u for u in tree if not unitFacs.get(u)]
onlyGeneric = [u for u in tree if not unitFacs.get(u) and u in buaGeneric]
print()
print("**没有派系归属的兵：", len(unknown), "/", len(tree), "**")
print("  其中能被『通用建筑（faction 为空的建筑招募行）』解释的：", len(onlyGeneric))
print("  两条路都查不到的（MOD 可能用别的机制给）：", len(unknown) - len(onlyGeneric))
print("  样例（前 12 个）：")
for u in unknown[:12]:
    print("   ", u, "  通用建筑里有吗：", "是" if u in buaGeneric else "否",
          "  军事组授权里有吗：", "是" if any(u in v for v in mgUnits.values()) else "否")
