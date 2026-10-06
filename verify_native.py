#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""原生格式层回归：把"和 rpfm_cli 4.7.4 逐字节对拍"这套验收做成一条命令。

  python verify_native.py <你的.pack> [原版 data/db.pack]

三项检查（全绿才算过）：
  ① 表往返：原版 db.pack 的每张表「解码 → 再编码」与原始字节一致
  ② 重写包：把你的包原生重写一遍，rpfm_cli 能读、且列表与内容都对得上
  ③ 画布拖动导出：指定一个组拖到 (700,150)，rpfm_cli 读导出包确认坐标已改

依赖：本机的 rpfm_cli 4.7.4（对拍基准）+ 构建好的 Probe。
"""
import base64
import io
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TMP = os.path.join(HERE, ".tmp", "verify_native")
CLI_CANDIDATES = [
    os.path.join(HERE, "..", "warband-tree-studio", "bin", "rpfm_cli.exe"),
    os.path.join(HERE, "rpfm", "rpfm_cli.exe"),
]
PROBE_DLL = os.path.join(HERE, "tools", "WarbandStudio.Probe", "bin", "Debug", "net10.0",
                         "WarbandStudio.Probe.dll")


def find_cli():
    for p in CLI_CANDIDATES:
        if os.path.isfile(p):
            return os.path.abspath(p)
    return None


def find_dotnet():
    local = os.path.join(os.path.dirname(HERE), "dotnet", "dotnet.exe")
    return local if os.path.isfile(local) else "dotnet"


def probe(*args):
    """跑一条 Probe 命令，返回 (退出码, 输出)。"""
    r = subprocess.run([find_dotnet(), PROBE_DLL, *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", cwd=HERE)
    return r.returncode, (r.stdout or "") + (r.stderr or "")


def cli(cli_exe, *args):
    env = dict(os.environ, MSYS2_ARG_CONV_EXCL="*")
    r = subprocess.run([cli_exe, "-g", "warhammer_3", *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env)
    out = re.sub(r"\x1b\[[0-9;]*m", "", (r.stdout or "") + (r.stderr or ""))
    return r.returncode, out


def main():
    if len(sys.argv) < 2:
        print("用法: python verify_native.py <你的.pack> [原版 data/db.pack]")
        return 1
    mod_pack = os.path.abspath(sys.argv[1])

    vanilla = sys.argv[2] if len(sys.argv) > 2 else None
    if not vanilla:
        game = ""
        cfg = os.path.join(HERE, "config.json")
        if os.path.isfile(cfg):
            game = (json.load(io.open(cfg, encoding="utf-8")) or {}).get("game") or ""
        vanilla = os.path.join(game, "data", "db.pack") if game else ""
    vanilla = os.path.abspath(vanilla) if vanilla else ""
    if not os.path.isfile(vanilla):
        print("[!] 找不到原版 db.pack（可在 config.json 里设 game，或当第二个参数传进来）")
        return 1

    cli_exe = find_cli()
    if not cli_exe:
        print("[!] 找不到 rpfm_cli.exe（对拍基准；放 tools/warband-tree-studio/bin/ 或 ./rpfm/）")
        return 1
    if not os.path.isfile(PROBE_DLL):
        print("[!] 没找到 Probe，先构建：dotnet build WarbandStudio.slnx")
        return 1

    # 开跑前清空临时目录：残留的旧 TSV 会让"读回核对"读到上一次跑别的包留下的行
    # （实测：同名的 studio_test_route 在旧文件里指向 BRT 的组，检查对着旧行判"不通过"）
    shutil.rmtree(TMP, ignore_errors=True)
    os.makedirs(TMP, exist_ok=True)
    fails = []

    # ① 表往返
    print("① 表往返（解码 → 再编码 = 原始字节）…")
    rc, out = probe("native-roundtrip", vanilla)
    line = next((l for l in out.splitlines() if "往返字节一致" in l), out.strip().splitlines()[-1] if out.strip() else "")
    print("   " + line)
    m = re.search(r"一致 (\d+)/(\d+)", line)
    if rc != 0 or not m or m.group(1) != m.group(2):  # 无定义的表 Probe 已按跳过处理，这里必须严格相等
        fails.append("① 表往返：" + line)

    # ①b 组合键读：军事组授权不能被按键合并吃掉（v0.89 的根因，见交接文档 §7.10）
    print("①b 组合键读（军事组授权 / 专属授权）…")
    for tbl, cols in (("units_to_groupings_military_permissions_tables", "unit,military_group"),
                      ("units_to_exclusive_faction_permissions_tables", "key,faction"),
                      # 成本的"额外资源"（如 chivalry_other -50）也是组合键：factor + resource_cost。
                      # 按第一个键合并会把同一个资源因子下的多条消耗吃掉（v0.120 起读这张表）
                      ("resource_cost_pooled_resource_junctions_tables", "pooled_resource_factor,resource_cost")):
        rc, out = probe("table-rows", mod_pack, tbl, cols, vanilla)
        line = next((l for l in out.splitlines() if "原版打底 + 本包" in l), "")
        print("   " + (line or "(无输出)"))
        mm = re.search(r"按键合并 (\d+) 行 / 组合键\([^)]*\) (\d+) 行", line)
        if rc != 0 or not mm or int(mm.group(2)) < int(mm.group(1)):
            fails.append(f"①b {tbl}：" + (line or "没读到行数"))

    # ② 重写包 + CLI 读回
    print("② 重写包 → rpfm_cli 读回…")
    rewritten = os.path.join(TMP, "rewritten.pack")
    rc, out = probe("native-write-pack", mod_pack, rewritten)
    print("   " + (out.strip().splitlines()[-1] if out.strip() else "(无输出)"))
    if rc != 0:
        fails.append("② 重写包失败")
    else:
        _, a = cli(cli_exe, "pack", "list", "-p", rewritten)
        _, b = cli(cli_exe, "pack", "list", "-p", mod_pack)
        la = sorted(x.strip() for x in a.splitlines() if x.strip() and "[INFO]" not in x and "[WARN]" not in x)
        lb = sorted(x.strip() for x in b.splitlines() if x.strip() and "[INFO]" not in x and "[WARN]" not in x)
        if la == lb:
            print(f"   ✓ CLI 列表一致（{len(la)} 条）")
        else:
            fails.append(f"② 列表不一致：新 {len(la)} / 原 {len(lb)}")

    # ③ 拖动导出
    print("③ 画布拖动导出（组坐标 → 就地改原文件 / zzzz_studio_layout 覆盖表）…")
    group = None
    rc, out = probe("native-list", mod_pack, "--plain")
    for line_ in out.splitlines():
        if line_.startswith("db/unit_upgrade_group_ui_infos_tables/"):
            rc2, tsv = probe("native-tsv", mod_pack, line_.strip(), os.path.join(TMP, "infos.tsv"))
            if os.path.isfile(os.path.join(TMP, "infos.tsv")):
                rows = io.open(os.path.join(TMP, "infos.tsv"), encoding="utf-8").read().splitlines()
                if len(rows) > 2:
                    group = rows[2].split("\t")[0]
            break
    if not group:
        fails.append("③ 找不到可用的组")
    else:
        exported = os.path.join(TMP, "moved.pack")
        rc, out = probe("export-edit", mod_pack, vanilla, exported, group, "700", "150")
        if rc != 0:
            fails.append("③ 导出失败：" + out.strip().splitlines()[-1])
        else:
            dest = os.path.join(TMP, "layout")
            cli(cli_exe, "pack", "extract", "-p", exported,
                "-t", os.path.join(HERE, "src", "WarbandStudio.Rpfm", "schemas", "schema_wh3.ron"),
                "-F", f"db/unit_upgrade_group_ui_infos_tables;{dest}")
            hit = None
            for dp, _d, fs in os.walk(dest):
                for f in fs:
                    p = os.path.join(dp, f)
                    if group in io.open(p, encoding="utf-8", errors="replace").read():
                        for ln in io.open(p, encoding="utf-8").read().splitlines():
                            if ln.startswith(group):
                                hit = ln
            if hit and hit.split("\t")[1:3] == ["700", "150"]:
                print(f"   ✓ {group} → (700,150) 已写进导出包")
            else:
                fails.append(f"③ 坐标没写对：{hit!r}")

    # ── ④ 画布编辑落表（新建组/加兵、新建升级+连线、新建成本、改页签、删连线/升级/兵）──
    if not fails and cli_exe and vanilla:
        print("④ 画布编辑 → 落表（导出后用 rpfm_cli 读回核对）…")
        amend = os.path.join(TMP, "amend.pack")
        rc, out = probe("amend-test", mod_pack, vanilla, amend)
        if rc != 0:
            fails.append("④ 全量编辑导出失败：" + (out.strip().splitlines() or ["(无输出)"])[-1])
        else:
            plan = json.load(io.open(amend + ".plan.json", encoding="utf-8"))
            schema = os.path.join(HERE, "src", "WarbandStudio.Rpfm", "schemas", "schema_wh3.ron")
            ex = os.path.join(TMP, "amend_x")
            folders = {
                "routes": "db/unit_upgrade_to_unit_groups_tables",
                "junc": "db/unit_to_unit_group_junctions_tables",
                "groups": "db/unit_upgrade_groups_tables",
                "costs": "db/resource_costs_tables",
                "links": "db/unit_upgrade_group_ui_links_tables",
                "infos": "db/unit_upgrade_group_ui_infos_tables",
                "grants": "db/units_to_groupings_military_permissions_tables",
            }
            args = []
            for key, inner in folders.items():
                args += ["-F", f"{inner};{os.path.join(ex, key)}"]
            cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema, *args)

            def table_rows(folder):
                """→ [(文件名, {列名: 值})]；跳过 #表名;版本;路径 那一行。"""
                result = []
                for dp, _d, fs in os.walk(folder):
                    for f in fs:
                        if not f.endswith(".tsv"):
                            continue
                        header = None
                        for ln in io.open(os.path.join(dp, f), encoding="utf-8", errors="replace"):
                            cells = ln.rstrip("\n").split("\t")
                            if not cells or not any(c.strip() for c in cells) or cells[0].startswith("#"):
                                continue
                            if header is None:
                                header = cells
                                continue
                            if cells[0].strip():
                                result.append((f, dict(zip(header, cells))))
                return result

            def has(rows, **want):
                for _f, row in rows:
                    if all(str(row.get(k, "")) == str(v) for k, v in want.items()):
                        return row
                return None

            def lacks(rows, **want):
                return has(rows, **want) is None

            routes = table_rows(os.path.join(ex, "routes"))
            junc = table_rows(os.path.join(ex, "junc"))
            groups = table_rows(os.path.join(ex, "groups"))
            costs = table_rows(os.path.join(ex, "costs"))
            links = table_rows(os.path.join(ex, "links"))
            infos = table_rows(os.path.join(ex, "infos"))

            def check(label, ok, detail=""):
                if ok:
                    print("   ✓ " + label)
                else:
                    fails.append(f"④ {label}" + (f"（{detail}）" if detail else ""))
                    print("   ✗ " + label + (f"（{detail}）" if detail else ""))

            new_route = has(routes, upgrade_key=plan["newRouteKey"])
            check("新建升级写进了 unit_upgrade_to_unit_groups_tables",
                  new_route and new_route.get("base_unit_group") == plan["newRouteBase"]
                  and new_route.get("target_unit_group") == plan["newRouteTarget"]
                  and str(new_route.get("required_rank")) == str(plan["newRouteRank"])
                  and new_route.get("resource_cost") == plan["newCost"],
                  detail=f"读到 {new_route}")
            check("删掉的升级不见了", lacks(routes, upgrade_key=plan["deletedRoute"]))
            # 成本工坊（v0.127）：成本（带符号）+ 资源池 + 成本↔池
            if plan.get("wbCostId"):
                check("成本工坊：成本 id 落表且金额按输入的正负原样写（正号 = 获得）",
                      has(costs, id=plan["wbCostId"], treasury_cost=str(plan["wbGold"])) is not None,
                      detail=f"期望 {plan['wbCostId']} = {plan['wbGold']}")
                pdir = os.path.join(TMP, "wbpools")
                cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                    "-F", f"db/pooled_resource_factor_junctions_tables;{pdir}")
                prows = table_rows(pdir)
                hitp = has(prows, unique_id=plan["wbPoolId"])
                check("成本工坊：资源池按 <资源>_warband_upgrade 建好（factor=other / min=-2147483647 / max=2147483647 / sort=0）",
                      hitp is not None and hitp.get("factor") == "other" and hitp.get("resource") == plan["wbResource"]
                      and str(hitp.get("minimum")) == "-2147483647" and str(hitp.get("maximum")) == "2147483647"
                      and str(hitp.get("sort_order")) == "0" and not str(hitp.get("specific_faction_set", "")).strip(),
                      detail=f"读到 {hitp}")
                jdir = os.path.join(TMP, "wbjunction")
                cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                    "-F", f"db/resource_cost_pooled_resource_junctions_tables;{jdir}")
                jrows = table_rows(jdir)
                hitj = has(jrows, pooled_resource_factor=plan["wbPoolId"], resource_cost=plan["wbCostId"])
                check("成本工坊：成本↔池关联写对（amount=-50，context/ui 照抄包里已有行）",
                      hitj is not None and str(hitj.get("amount")) == "-50"
                      and str(hitj.get("context", "")).strip() != "" and str(hitj.get("ui_resource_transaction_pooled_resource", "")).strip() != "",
                      detail=f"读到 {hitj}")
                # 成本工坊 v2：改已有成本就地覆盖 / 删成本（连资源关联）
                if plan.get("adjCostId"):
                    rows2 = [r for _f, r in costs if r.get("id") == plan["adjCostId"]]
                    check("成本工坊：改已有成本是就地覆盖（同 id 只 1 行、金额是新值）",
                          len(rows2) == 1 and str(rows2[0].get("treasury_cost")) == "-777",
                          detail=f"读到 {rows2[:2]}")
                if plan.get("delCostId"):
                    check("成本工坊：删成本 → 包里没有这个 id 了",
                          lacks(costs, id=plan["delCostId"]), detail=f"期望没有 {plan['delCostId']}")
                    left = [r for _f, r in jrows if r.get("resource_cost") == plan["delCostId"]]
                    check("成本工坊：删成本连带清掉它的资源关联（junction 行没了）",
                          len(left) == 0, detail=f"读到 {len(left)} 行")

            check("新建成本写进 resource_costs_tables 且金额是负的",
                  has(costs, id=plan["newCost"], treasury_cost=str(plan["newCostAmount"])),
                  detail=f"期望 {plan['newCost']} = {plan['newCostAmount']}")
            grants = table_rows(os.path.join(ex, "grants"))
            check("加兵时按军事组授权写进了 units_to_groupings_military_permissions_tables",
                  has(grants, unit=plan.get("grantUnit", ""), military_group=plan.get("grantGroup", "")) is not None,
                  detail=f"期望 {plan.get('grantUnit')} → {plan.get('grantGroup')}")
            # 加进画布的兵自动解锁战役经验（zzzz_studio_elite_unlock 覆盖表）
            unl = os.path.join(TMP, "unlock")
            cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                "-f", f"db/main_units_tables/zzzz_studio_elite_unlock;{unl}")
            urows = table_rows(unl)
            hit = has(urows, unit=plan.get("unlockUnit", plan.get("grantUnit", "")))
            check("加进画布的兵写进 zzzz_studio_elite_unlock 且 restrict_xp_gain_in_campaign=false",
                  hit is not None and str(hit.get("restrict_xp_gain_in_campaign")).lower() == "false",
                  detail=f"读到 {hit}")

            check("新建组 + 加兵写进 junction/groups",
                  has(junc, unit=plan["newUnit"], unit_group=plan["newGroup"]) is not None
                  and has(groups, unit_group=plan["newGroup"]) is not None)
            check("删掉的兵（junction）不见了",
                  lacks(junc, unit=plan["deletedJunctionUnit"], unit_group=plan["deletedJunctionGroup"]))
            check("新建的界面连线写进了 ui_links（C1：箭头读这张表）",
                  has(links, child_key=plan["newGroup"], parent_key=plan["newRouteBase"]) is not None)
            check("删掉的连线不见了",
                  lacks(links, child_key=plan["deletedLinkChild"], parent_key=plan["deletedLinkParent"]))

            # ── 合并组：本次会话新建的组并进另一个组，兵不能丢（v0.84 的老 bug）──
            if plan.get("mergeKeep"):
                check("合并组：兵保住了（进了 keep 组）",
                      has(junc, unit=plan["mergeUnit"], unit_group=plan["mergeKeep"]) is not None)
                check("合并组：指向被并组的悬空 junction 行没了",
                      lacks(junc, unit=plan["mergeUnit"], unit_group=plan["mergeDrop"]))
                check("合并组：被并的组已删除",
                      lacks(groups, unit_group=plan["mergeDrop"]))
                mp = has(infos, unit_upgrade_group=plan["mergeKeep"])
                check("合并组：保留的组仍有坐标/页签（重载后不消失）",
                      mp is not None and [mp.get("x"), mp.get("y")] == [str(plan["mergePos"][0]), str(plan["mergePos"][1])],
                      detail=f"读到 {mp}")

            # ── 悬空引用自愈（游戏/RPFM 会把"引用了不存在的组"判成 invalid record 直接崩）──
            gs = os.path.join(ex, "groups_all")
            cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                "-F", f"db/unit_upgrade_groups_tables;{gs}")
            live = {r.get("unit_group", "") for _f, r in table_rows(gs)}
            live = {x for x in live if x}
            dangling = []
            for label, rows2 in (("路线", routes), ("连线", links)):
                for _f, r in rows2:
                    for col in ("base_unit_group", "target_unit_group", "child_key", "parent_key"):
                        v = r.get(col, "")
                        if v and v not in live and v.startswith("studio_"):
                            dangling.append(f"{label}:{col}={v}")
            check("导出的包里没有『引用不存在的组』的行（自愈生效）",
                  not dangling, detail=f"悬空 {len(dangling)}：{dangling[:3]}")

            # ── 「右键 → 查看已有升级」面板的落表语义（跨页只改路线表）──
            if plan.get("xpageRouteKey"):
                xr = has(routes, upgrade_key=plan["xpageRouteKey"])
                check("跨页升级：路线写进了 unit_upgrade_to_unit_groups_tables",
                      xr is not None and xr.get("base_unit_group") == plan["xpageFrom"]
                      and xr.get("target_unit_group") == plan["xpageTo"],
                      detail=f"读到 {xr}")
                check("跨页升级：没有写界面连线（只改路线表）",
                      lacks(links, child_key=plan["xpageTo"], parent_key=plan["xpageFrom"]))
            if plan.get("swapRouteKey"):
                sr = has(routes, upgrade_key=plan["swapRouteKey"])
                check("同会话同键后写为准：交换方向后路线是新 base → 新 target",
                      sr is not None and sr.get("base_unit_group") == plan["swapBase"]
                      and sr.get("target_unit_group") == plan["swapTarget"],
                      detail=f"读到 {sr}")
                sl = has(links, child_key=plan["swapTarget"], parent_key=plan["swapBase"])
                check("交换方向：连线出入口按新方向重算",
                      sl is not None
                      and str(sl.get("child_link_position")) == str(plan["swapChildPos"])
                      and str(sl.get("parent_link_position")) == str(plan["swapParentPos"]),
                      detail=f"读到 {sl}，期望 {plan['swapChildPos']}/{plan['swapParentPos']}")
                check("交换方向：旧方向的连线已删",
                      lacks(links, child_key=plan["swapBase"], parent_key=plan["swapTarget"]))
            if plan.get("keepLinkRouteKey"):
                check("只删路线（跨页语义）：路线不见了",
                      lacks(routes, upgrade_key=plan["keepLinkRouteKey"]))
                check("只删路线（跨页语义）：界面连线保留",
                      has(links, child_key=plan["keepLinkChild"], parent_key=plan["keepLinkParent"]) is not None)
            # 互斥写入（v0.115）：同一对"先加后删"要真的删掉、"先删后加"要真的加回来
            # ——用户实测的三个症状（连线删不掉 / 合并先后拆组不生效 / 拆出复制品）都是这条语义没做对
            if plan.get("purgeRouteKey"):
                check("互斥：先建后删的升级不在包里（先连线再删除删得掉）",
                      lacks(routes, upgrade_key=plan["purgeRouteKey"]))
            if plan.get("purgeLinkChild"):
                check("互斥：先建后删的连线不在包里",
                      lacks(links, child_key=plan["purgeLinkChild"], parent_key=plan["purgeLinkParent"]))
            if plan.get("keepPairUnit"):
                check("互斥：先删后加的兵↔组还在（拖出去又拖回来）",
                      has(junc, unit=plan["keepPairUnit"], unit_group=plan["keepPairGroup"]) is not None,
                      detail=f"期望 {plan['keepPairUnit']} → {plan['keepPairGroup']}")

            # 一对组只有一条界面连线（v0.124）：反方向再写一条，导出后这一对只该剩 1 行 ——
            # 老代码按方向精确匹配，反方向那条删不掉/覆盖不掉，画布上就是"两条线"
            if plan.get("flipLinkChild"):
                fc, fp = plan["flipLinkChild"], plan["flipLinkParent"]
                n = 0
                for _f, row in links:
                    c2, p2b = row.get("child_key", ""), row.get("parent_key", "")
                    if (c2 == fc and p2b == fp) or (c2 == fp and p2b == fc):
                        n += 1
                check("一对组只有一条线：反方向再写一条 → 导出后这一对只剩 1 行（不会双线）",
                      n == 1, detail=f"读到 {n} 行")

            # 只调出入口（同方向）→ 旧条目必须被**就地覆盖**，不能变成两份
            if plan.get("adjLinkChild"):
                ac, ap = plan["adjLinkChild"], plan["adjLinkParent"]
                hits = [r for _f, r in links
                        if (r.get("child_key") == ac and r.get("parent_key") == ap)
                        or (r.get("child_key") == ap and r.get("parent_key") == ac)]
                check("调整画线：旧的连线条目被就地覆盖（这一对只有 1 行，不是两份）",
                      len(hits) == 1, detail=f"读到 {len(hits)} 行：{hits[:2]}")
                check("调整画线：出入口写成了新方向（2/4）",
                      len(hits) == 1 and str(hits[0].get("child_link_position")) == "2"
                      and str(hits[0].get("parent_link_position")) == "4",
                      detail=f"读到 {hits[0] if hits else None}")
                # 三个 offset 归零：否则旧的弯度偏移会把中间那段折到卡片另一侧
                # （画布上就是"对向多出一小段线"——用户实测）
                def _z(v):
                    try:
                        return abs(float(v)) < 1e-6
                    except (TypeError, ValueError):
                        return False
                if len(hits) == 1:
                    # mid_link_offset = 中间那段沿起始方向走多少像素（拖转点手柄写它）；po/co 不是像素，**不该被动**
                    check("调整画线：mid_link_offset 写成新值（中间那段的位置由它决定）",
                          str(hits[0].get("mid_link_offset")) in ("37", "37.0000"),
                          detail=f"读到 mid={hits[0].get('mid_link_offset')}（期望 37，改前 {plan.get('adjLinkMoBefore')}）")
                    check("调整画线：parent/child offset 保持作者原值（它们的数值不是像素，不归工具管）",
                          str(hits[0].get("parent_link_position_offset")) in (plan.get("adjLinkPoBefore"), plan.get("adjLinkPoBefore") + ".0000", "0", "0.0000")
                          and str(hits[0].get("child_link_position_offset")) in (plan.get("adjLinkCoBefore"), plan.get("adjLinkCoBefore") + ".0000", "0", "0.0000"),
                          detail=f"读到 po={hits[0].get('parent_link_position_offset')}（原 {plan.get('adjLinkPoBefore')}）"
                                 f" co={hits[0].get('child_link_position_offset')}（原 {plan.get('adjLinkCoBefore')}）")

            # 改作者已有路线的金额/等级 → 就地覆盖（1 行），不留第二份
            if plan.get("adjRouteKey"):
                rr = [r for _f, r in routes if r.get("upgrade_key") == plan["adjRouteKey"]]
                check("改已有升级：旧条目不新增第二份（同 key 只有 1 行）",
                      len(rr) == 1, detail=f"读到 {len(rr)} 行：{rr[:2]}")
                check("改已有升级：金额/等级写成了新值",
                      len(rr) == 1 and rr[0].get("resource_cost") == plan["adjRouteCost"]
                      and str(rr[0].get("required_rank")) == "5" and str(rr[0].get("subtracted_rank")) == "2",
                      detail=f"读到 {rr[0] if rr else None}")

            moved = has(infos, unit_upgrade_group=plan["movedGroup"])
            check("拖动改的坐标落表（就地改原文件 / 覆盖表）",
                  moved is not None and [moved.get("x"), moved.get("y")] == [str(plan["movedTo"][0]), str(plan["movedTo"][1])],
                  detail=f"读到 {moved}")
            check("改页签落表（就地改原文件 / 覆盖表）",
                  has(infos, unit_upgrade_group=plan["categoryGroup"], category=plan["category"]) is not None,
                  detail=f"期望 category={plan['category']}")

            # 页签重命名：categories 换成新 key、infos 里旧 key 消失、twui 里有新 holder_tab
            if plan.get("renameOld"):
                oldk, newk = plan["renameOld"], plan["renameNew"]
                cats2 = os.path.join(ex, "cats2")
                cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                    "-F", f"db/unit_upgrade_group_ui_categories_tables;{cats2}")
                cat_rows2 = table_rows(cats2)
                cat_col2 = list(cat_rows2[0][1].keys())[0] if cat_rows2 else "category"
                check(f"重命名：categories 里有 {newk} 且没有 {oldk}",
                      has(cat_rows2, **{cat_col2: newk}) is not None and lacks(cat_rows2, **{cat_col2: oldk}))
                check(f"重命名：组的 category 已改成 {newk}",
                      has(infos, category=newk) is not None and lacks(infos, category=oldk))
                tdir = os.path.join(TMP, "renametwui")
                cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{plan['newTabTwui']};{tdir}")
                txt = ""
                for dp, _d, fs in os.walk(tdir):
                    for f in fs:
                        if f.lower().endswith(".twui.xml"):
                            txt = io.open(os.path.join(dp, f), encoding="utf-8", errors="replace").read()
                check(f"重命名：twui 里有 holder_tab_{newk}",
                      f"holder_tab_{newk}" in txt, detail=f"长度 {len(txt)}")

            # 换图：导出包里目标条目的字节应当与来源一致。
            # **注意（v1.4 起）**：这一轮同时还有"页签改名 BRT → BRTREN"，改名会把页签实际用的那张图
            # 跟着改成新名字（用户要的语义：图跟新 key 走，撞名才加 _N）—— 所以"换的图"落在**新名字**上，
            # 旧名字（background_images_brt.png）保持包里原样（真·改名：加新名 + 旧名不动）。
            if plan.get("artTarget"):
                artdir = os.path.join(TMP, "art")
                renamed_target = f"ui/skins/default/warband_upgrades/background_images_{plan['renameNew'].lower()}.png"
                cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{plan['artTarget']};{artdir}")
                cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{plan['artSource']};{artdir}")
                cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{renamed_target};{artdir}")
                got = {}
                for dp, _d, fs in os.walk(artdir):
                    for f in fs:
                        if f.lower().endswith(".png"):
                            got[f.lower()] = io.open(os.path.join(dp, f), "rb").read()
                src = os.path.basename(plan["artSource"]).lower()
                tgt = os.path.basename(plan["artTarget"]).lower()
                newname = os.path.basename(renamed_target).lower()
                check("换图：换的图跟着页签改名落到新名字上（图跟新 key 走）",
                      newname in got and src in got and got[newname] == got[src],
                      detail=f"新名 {len(got.get(newname, b''))} 字节 / 来源 {len(got.get(src, b''))} 字节")
                check("换图：旧名字那张保持包里原样（改名是加新名，不覆盖/不删旧名）",
                      tgt in got and got[tgt] != got[src],
                      detail=f"旧名 {len(got.get(tgt, b''))} 字节 / 来源 {len(got.get(src, b''))} 字节")

            # 页签表（新建页签要往里加一行；先读出来给下面的分支用）
            cats = os.path.join(ex, "cats")
            cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                "-F", f"db/unit_upgrade_group_ui_categories_tables;{cats}")
            cat_rows = table_rows(cats)
            cat_col = list(cat_rows[0][1].keys())[0] if cat_rows else "category"

            # 新建页签：twui（本体块 + hierarchy 索引 + 两张图的引用）+ 两张图 + categories 行
            if plan.get("newTabKey"):
                import xml.dom.minidom
                tab = plan["newTabKey"]
                tabdir = os.path.join(TMP, "tab")
                cli(cli_exe, "pack", "extract", "-p", amend, "-t", schema,
                    "-f", f"{plan['newTabTwui']};{tabdir}")
                twui = None
                for dp, _d, fs in os.walk(tabdir):
                    for f in fs:
                        if f.lower().endswith(".twui.xml"):
                            twui = os.path.join(dp, f)
                if twui is None:
                    check("新建页签：twui 提取到了", False, "导出包里没找到 twui")
                else:
                    text = io.open(twui, encoding="utf-8", errors="replace").read()
                    guid = r"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-?[0-9a-fA-F]{12,20}"
                    # 新页签的 tag/id 按 WUU 的写法**小写**（holder_tab_modx），categories/God 才是大写 key → 比较时统一小写
                    ids = [x.lower() for x in re.findall('id="holder_tab_([A-Za-z0-9_]+)"', text)]
                    check(f"新建页签：twui 里有 holder_tab_{tab}", tab.lower() in ids, detail=f"共 {len(ids)} 个页签")
                    check(f"新建页签：categories 里有 {tab}", has(cat_rows, **{cat_col: tab}) is not None)
                    # 本体块里的 GUID 必须是新的（不能和母版撞）
                    body = None
                    m = re.search(f'id="holder_tab_{tab}"', text, re.I)
                    if m and m.start() > 0:
                        end = text.lower().find(f"</holder_tab_{tab.lower()}>", m.start())
                        body = text[max(0, m.start() - 60):end + 20] if end > 0 else None
                    donor_ids = [x.lower() for x in re.findall(guid, text)]
                    if body:
                        mine = set(x.lower() for x in re.findall(guid, body))
                        donor_name = plan.get("newTabDonor", "").lower()
                        di = text.find(f'id="holder_tab_{donor_name}"')
                        dend = text.find(f"</holder_tab_{donor_name}>", di) if di > 0 else -1
                        donor_body = text[max(0, di - 60):dend + 20] if dend > 0 else ""
                        donor_ids = set(x.lower() for x in re.findall(guid, donor_body))
                        # 只允许共享组件（如 selected_frame_general）重合，且这些在全文里出现很多次
                        import collections
                        cnt = collections.Counter(x.lower() for x in re.findall(guid, text))
                        bad = [g for g in (mine & donor_ids) if cnt[g] <= 3]
                        check("新建页签：本体块的 GUID 全是新的（没和母版撞）", not bad, detail=f"撞了的：{bad[:3]}")
                    try:
                        xml.dom.minidom.parseString(text.encode("utf-8"))
                        check("新建页签：生成的 twui 是良构 XML", True)
                    except Exception as ex:
                        check("新建页签：生成的 twui 是良构 XML", False, str(ex))
                    # v0.87：新建页签必须补齐 WUU 的那一整套（按钮组件 / 背景面板 / 背景条目 / hierarchy 里按钮改名），
                    # 只克隆 holder_tab 的话游戏里就是"按钮是母版的、背景不出现、两个页签抢同一个按钮"。
                    low = tab.lower()
                    don = (plan.get("newTabDonor") or "").lower()
                    check(f"新建页签：有 button_toggle_tab_{low} 组件（按钮不是沿用母版的）",
                          f"<button_toggle_tab_{low}" in text.lower())
                    check(f"新建页签：有背景面板组件 <{low}>（页签背景靠它显示）",
                          re.search(r'<' + re.escape(low) + r'[\s>]', text) is not None)
                    check(f"新建页签：有 background_images_{low}.png 的图条目",
                          f"background_images_{low}.png" in text.lower())
                    # hierarchy 里新页签的节点：里面的按钮必须是**新的 key**，不能还是母版的
                    mnode = re.search(r'<holder_tab_' + low + r'[\s\S]{0,1200}?</holder_tab_' + low + r'>', text, re.I)
                    if mnode:
                        check(f"新建页签：hierarchy 节点里的按钮是 button_toggle_tab_{low}（不是母版 {don}）",
                              f"button_toggle_tab_{low}" in mnode.group(0).lower()
                              and (not don or f"button_toggle_tab_{don}" not in mnode.group(0).lower()))
                    else:
                        check("新建页签：hierarchy 里有这个页签的节点", False, "没找到节点")
                    # 背景图条目的 GUID 必须是新的（不能和母版撞）
                    def _entry_guid(nam):
                        mm = re.search(r'<component_image\b[^>]*imagepath="[^"]*background_images_' + re.escape(nam) +
                                       r'\.png"[^>]*/>', text, re.I)
                        if not mm:
                            return None
                        gg = re.search(r'this="([0-9a-fA-F\-]+)"', mm.group(0))
                        return gg.group(1).lower() if gg else None
                    g_new, g_old = _entry_guid(low), _entry_guid(don) if don else None
                    check("新建页签：背景图条目是新 GUID（没和母版撞）",
                          g_new is not None and g_old is not None and g_new != g_old,
                          detail=f"新 {g_new} / 母版 {g_old}")

                    # P1：新页签的 offset 必须和其他页签都不一样（槽位：x=8 一列、y 从 12 起步长 20、占空槽）
                    offs = {}
                    for mm in re.finditer(r'id="holder_tab_([A-Za-z0-9_]+)"', text):
                        nm = mm.group(1)
                        dend = text.find(f"</holder_tab_{nm}>", mm.start())
                        seg = text[mm.start():dend if dend > 0 else mm.start() + 4000]
                        om = re.search(r'offset="([0-9.]+),([0-9.]+)"', seg)
                        if om:
                            offs[nm] = (om.group(1), om.group(2))
                    if tab in offs:
                        mine2 = offs[tab]
                        others = sorted({v for k2, v in offs.items() if k2 != tab})
                        check("新建页签：offset 与已有页签都不相同（P1 槽位规则）",
                              mine2 not in others,
                              detail=f"{tab}={mine2}，已有 {others[-3:]}")
                        # 槽位占用情况只做提示：WUU 源文件自己的页签就大量同槽（第一列 15 槽常是满的），
                        # 所以新页签落在第二列 x=79 是正常现象 —— 游戏里表现要看 P1 实机确认
                        c1 = sorted({v[1] for k2, v in offs.items() if k2 != tab and float(v[0]) < 40})
                        c2 = sorted({v[1] for k2, v in offs.items() if k2 != tab and float(v[0]) >= 40})
                        print(f"   [i] 槽位占用：第一列 {len(c1)}/15 个槽，第二列 {len(c2)}/15；"
                              f"本次新页签 {tab} = {mine2[0]},{mine2[1]}")
                    # P4：新建页签自动放的空组要真的落表（groups + infos 覆盖表）
                    ag = has(groups, unit_group=plan.get("newTabAutoGroup", ""))
                    check("新建页签：自动空组落表（P4：有组游戏里页签才显示）",
                          ag is not None and has(infos, unit_upgrade_group=plan.get("newTabAutoGroup", ""),
                                                 category=plan.get("newTabAutoCat", "")) is not None,
                          detail=f"读到 {ag}")
                # 本地素材文件当来源：新页签的按钮图字节 = 那份本地文件；换图新增的目标条目同理
                if plan.get("localArtBytes"):
                    want_bytes = base64.b64decode(plan["localArtBytes"])
                    for label, inner in (("新建页签的按钮图（本地素材）", plan.get("newTabLocalBtn", "")),
                                         ("换图新增的条目（本地素材）", plan.get("localArtTarget", ""))):
                        if not inner:
                            continue
                        here = os.path.join(TMP, "locart")
                        cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{inner};{here}")
                        got = None
                        for dp, _d, fs in os.walk(here):
                            for f in fs:
                                if f.lower() == os.path.basename(inner).lower():
                                    got = io.open(os.path.join(dp, f), "rb").read()
                        check(f"{label}：字节和本地那份一致", got == want_bytes,
                              detail=f"包内 {len(got) if got else 0}B / 本地 {len(want_bytes)}B")

                for img in plan.get("newTabImages", []):
                    here = os.path.join(TMP, "tabimg")
                    cli(cli_exe, "pack", "extract", "-p", amend, "-f", f"{img};{here}")
                    found = any(f.lower().endswith(os.path.basename(img).lower())
                                for _dp, _d, fs in os.walk(here) for f in fs)
                    check(f"新建页签：图片在包里（{os.path.basename(img)}）", found)

    print()
    if fails:
        print("【总判定】✗ 不通过：")
        for f in fails:
            print("   - " + f)
        return 1
    print("【总判定】✓ 通过：原生格式层与 rpfm_cli 4.7.4 逐字节一致")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    sys.exit(main())
