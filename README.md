# WarbandStudio（战帮树工坊 · 重构版）

全战争锤 3「战帮升级」MOD 的可视化编辑器，**RPFM 式桌面应用**：
左侧 Pack 文件树、中间战帮升级可视化画布、右侧派系与兵种库。
底层读写全部交给 **rpfm 5.0.6**（`rpfm_server`），本工具只负责战帮这一块。

> 和旧的 `tools/warband-tree-studio`（Python + pywebview）是**两个独立工具**。
> 旧工具冻结不动、继续可用；新工具做到功能对等前，旧工具仍是主力。
> 领域逻辑（坐标系、升级链、导出补行规则）从旧工具搬，已验证的部分直接复用。

## 技术栈

| 层 | 用什么 | 说明 |
|---|---|---|
| 壳 | **C# / .NET 10 + WPF** | 视觉照《Asset Editor 国区版（派大星）UI 规范》复刻：语义色 token、26 DIP 控件、24 DIP 行高、圆角 3/4/6/7，深色石墨基线 |
| 画布 | **WebView2 内嵌** | 复用旧工具已验证的 1:1 坐标渲染核心（HTML/JS） |
| 读端引擎 | **rpfm_cli.exe（RPFM 4.7.4，随包内置）** | `pack list` / `pack extract -t <schema>` / `pack add -t <schema>` / `pack create` / `pack diagnose` / `dependencies generate`；每次操作一个独立进程，不驻留、不占端口 |
| schema | **随包 `schemas/schema_wh3.ron`** | 命令行直接指路径，**没有"首跑装 schema"这一步**，也不依赖用户装没装 RPFM |
| SDK | `.NET 10.0.401`（用户级） | 装在 `tools/dotnet/`，不写系统 PATH |

> 备选路线（代码保留、默认不用）：RPFM **5.0.6** 的常驻 `rpfm_server`（WebSocket 协议，命令面更全，含引用搜索）。相关代码在 `WarbandStudio.Rpfm`（`RpfmClient` / `RpfmServerHost` / `SchemaInstaller`），仅当以后要用 5.x 能力时再切。

## 目录

```
tools/warband-studio/
├── WarbandStudio.slnx          解决方案（.NET 10 的新 XML 格式）
├── VERSION                     当前版本号（build.ps1 会写）
├── build.ps1                   打包脚本（版本约定见下）
├── src/WarbandStudio.Core      领域层：战帮模型、坐标系、TSV↔模型
├── src/WarbandStudio.Rpfm      rpfm_server 客户端（协议封套、命令封装、进程管理）
├── src/WarbandStudio.Pack      pack 会话：只读容器 + 可编辑包 + 文件树模型
├── src/WarbandStudio.Editors   编辑器模块：战帮画布 / DB 表网格 / twui / 页签向导
├── src/WarbandStudio.Ui        WPF 壳（三栏布局、主题 token、i18n）
├── tools/WarbandStudio.Probe   阶段 0 的验证/冒烟工具（命令行）
├── tests/WarbandStudio.Tests   单测（坐标系、升级链不变式）
└── release/                    构建产物（WarbandStudio_v<号>/，只留最近 5 个）
```

## 构建 / 运行

```powershell
# 开发时直接跑
.\build.ps1 -NoPublish                # 只编译验证
dotnet run --project src/WarbandStudio.Ui      # 起壳（需 SDK 在 PATH，或直接用下面的全路径）

# 出发布版（self-contained，目标机不用装 .NET）
.\build.ps1                           # 用 VERSION 里的号；目录已存在就自动 +0.1
.\build.ps1 -Version 0.3              # 指定版本
.\build.ps1 -DryRun                   # 只看打算做什么

# 冒烟/验证工具（对着 rpfm_server 跑真实 pack）
dotnet run --project tools/WarbandStudio.Probe -- tree <某个.pack>
dotnet run --project tools/WarbandStudio.Probe -- roundtrip <pack> <表路径> <工作目录>
```

**版本约定**：从 `v0.1` 起，每次构建产出一个 `release/WarbandStudio_v<号>/`，
**只保留最近 5 个**（当前 + 4 个旧版本），更早的构建脚本自动删。

## 阶段 0：已经验证过的东西（2026-09-28）

用 `WarbandStudio.Probe` 对着真实 pack（旧工具导出的 `__四神开.pack`）实测：

| 项 | 结论 |
|---|---|
| server 发现 / 复用 | 本机 5.0.6 在跑就复用，不硬起（避免端口 10048）|
| 选游戏 → schema | `SetGameSelected("warhammer_3")` 后 schema 自动加载（读 `%APPDATA%\FrodoWazEre\rpfm\config\schemas\schema_wh3.ron`）|
| **schema 自举** | 配置目录里没有 schema 时，从随包文件补一份并成功加载（模拟全新机器实测通过）|
| 打开 pack / 文件树 | PFH5 识别正常，22 个文件，战帮相关过滤出 18 个 |
| **表导出 TSV** | **与 rpfm_cli 4.7.4 逐字节一致（md5 相同）** —— 现有表产线可零改动迁移 |
| **写路径往返** | 导出 → `ImportTSV` → `SavePackAs` → 重开 → 再导出，**TSV 逐字节一致**（编解码无损）|
| 表结构（表编辑器用） | `DefinitionsByTableName` 拿到列定义（含 `is_reference=[表,列]` 引用关系）|
| 诊断 | `DiagnosticsCheck` 可用（未生成依赖缓存时会提示）|
| 引用搜索 | `SearchReferences` 可用（表名要用带 `_tables` 的全名）|

## 首跑：什么都不用装

引擎（`rpfm/rpfm_cli.exe`，RPFM 4.7.4，MIT，22MB）和 schema（`schemas/schema_wh3.ron`，MIT，11MB）
都随发布包走，命令行直接指路径 —— 所以：

- **不需要**用户装 RPFM，**不需要**联网下 schema；
- 启动后只在「全局选项」里选一次**游戏目录**（用来做诊断 / 生成依赖缓存；纯编辑和导出不需要它）；
- 依赖缓存（`%APPDATA%\WarbandStudio\cache\dependencies_wh3.pak2`）只在跑「诊断」时才要生成一次。

**怎么开始改（v1.5.0 起 = 工程化）**：启动是**空状态**（不再自动打开参考包）——

1. 「**从 Pack 打开工程…**」选你的 `.pack` →（弹表单页）填**工程目录 / 生成 Pack 目录 / 项目 key**
   —— 源包会被**复制**进工程再编辑（源文件不动）；也可以「新建工程…」建空骨架后「导入 WUU 模板」/「导入 Pack」；
2. 左下「全局选项」里填**项目 key**（新建单位组的命名前缀：`项目key_页签_兵种`）；
3. 每次「保存」前，原包自动备份进 `<工程目录>\old\`（保留最近 20 份）；菜单「工程 → 历史版本…」一键还原。

工具栏「打开上次工程」可一键续接；**多个战帮各用各的工程目录，互不影响**。

## 原生格式层（`WarbandStudio.Packfile`）—— 学 CRPFM 的路线

参考 [CRPFM（ChaDeRPFM）](https://github.com/) 的做法：**不依赖 RPFM 程序本体，用 C# 把格式层重写**（规格照 rpfm_lib，MIT）。
依赖都是宽松许可的托管库：`K4os.Compression.LZ4` / `K4os.Compression.LZ4.Streams`（LZ4）、`ZstdSharp.Port`（Zstd）、`SharpCompress`（LZMA1）。

已落地并**对拍验证**的部分：

| 里程碑 | 内容 | 验收（与 rpfm_cli 4.7.4 对拍） |
|---|---|---|
| M1 PFH 索引 | PFH5/PFH6 头 + 依赖索引 + 文件索引（含 flags：时间戳/加密/扩展头），按 StoredSize 顺次算数据偏移 | 4 个包路径列表**完全一致**：你的 mod 256 条、`__四神开` 25 条、原版 `db.pack` 1601 条、`data.pack` 14037 条；最大的 `audio_base.pack` 163350 条读 **167ms** |
| M2 解压 | u32 原始大小 + LZMA1（TW 的 5 字节属性头）/ LZ4 / Zstd（后两者按魔术号嗅探） | 从原版 `data.pack` 取压缩条目 `documentation/script/images/battle_hierarchy.png`：原生解开 61145B 与 CLI 抽取**逐字节一致** |
| M3 schema | `schema_wh3.ron`（v5）解析器：definitions/fields/field_type/is_key/is_bitwise/enum_values/is_part_of_colour；其余键整块跳过 | 1742 张表，载入 **170ms** |
| M4 DB 解码 | 头部（GUID/版本标记、mysterious_byte、行数）→ 逐字段按类型读 → 三类后处理（位展开/枚举换名/**颜色分列合并**）→ version 0 表逐个定义试解 | **原版 `db.pack` 全部 1600 张表与 CLI 导出的 TSV 逐字节一致**（1600/1600，3.4s、2.1ms/表）；含颜色合并的 `character_skill_categories_tables`、45 列的 `main_units_tables`（1,072,538B）单测也逐字节一致 |

| M5 接进应用 | 打开包/列文件/解表全部走原生：`PackArchive` + `DbTable`，**毫秒级、纯内存、不起进程、不落临时文件**；rpfm_cli 只在写回/诊断/依赖缓存时按需拉起。双击文件树里的表 → 中间栏开表视图（DataGrid） | 打开 256 个文件的包 **8~11ms**；`main_units_tables`（15 行 × 45 列）解出并显示；`schema` 首次载入 219ms（1742 张表） |

| M6 原生写回 | `DbEncoder`（表→二进制）+ `PackWriter`（索引写/数据区排布/依赖索引；未改动条目**原样搬运存储字节**，压缩态保留） | ① **离线往返：原版 1600 张表「解码→再编码」与原始字节完全一致**（3.6s）；② 重写你的 32.3MB 包（259 条、含重编码一张表）**98ms**，rpfm_cli 读得懂、167 张表 TSV 零差异；③ 重写原版压缩 `db.pack`（1601 条压缩条目 + 混入 1 张未压缩）**53ms**，rpfm_cli 导出 1600 张表零差异 |

| M6b 导出规则①：精英解锁 | `WarbandExporter`：读兵组↔兵关联表拿到树里的兵 → 从**原版 `db.pack`** 取完整行 → 往 `db/main_units_tables/studio_elite_unlock` 写一个**覆盖表**（只含解锁行、`restrict_xp_gain_in_campaign=false`），**模组原有文件一个都不动**（TW 表按 key 覆盖） | 你的包实测：树里 30 个兵 → 解锁 6、本来就解锁 24、原版表里没有 0；导出 287ms；rpfm_cli 读导出包取该表，6 行**全部 false** ✓。应用里「另存为…」已接这条路（原生导出 1.3s / 32.3MB 包） |

| M7 战帮画布 | WebView2 承载 `Web/canvas.html`，坐标模型与游戏 1:1（卡 40×87、步长 46、格 50 锚在原点 (322,48)、面板 1317×780、左 UI 区 322）；数据由原生读表 + **按键合并多文件表**生成（`TableFiles.ReadMerged`） | 你的包实测：**843 组 / 966 张牌 / 966 张卡图命中（0 缺失）/ 26 页签**，数据构建 233ms；页面通过 WebView2 消息**自报**渲染结果（`{"type":"rendered","groups":843,"cards":966,…}`）|
| M7b 画布写回 | 拖动结束 → 页面回传 `{type:'move', group, x, y}` → 记进编辑表 → 导出时生成 `db/unit_upgrade_group_ui_infos_tables/studio_layout` **覆盖表**（整行 + 新 x/y） | 把 `Yukino_Brt_Cav_Royal_Hippogryph_Knights` 拖到 (700,150) → 导出 → rpfm_cli 读出该行 **x=700 y=150** ✓ |

写回时踩到的两条：

- **版本 0 的表不写版本标记**（`VERSION_MARKER`）——写了每张表多 8 字节（1069 张对不上就是这个）；
- **字符串写回要用未转义的原文**：解码时把换行/制表符转成字面两字符是为了 TSV 安全，但"字面反斜杠+n"和"换行"转义后一样，**转义不可逆**（45 张表对不上就是这个）——所以 `DbValue` 同时保留 `Str`（显示）与 `RawStr`（写回）。

复刻 TSV 时必须对上的几条隐藏规则（都踩过）：

- **列顺序：主键列提前**（`fields_processed_sorted(keys_first)`）——`main_units_tables` 的 `unit` 必须排第一；
- **颜色分列合并**：`col_r/col_g/col_b` → 一列 `col_hex`，追加在行尾、按颜色组序号排；无下划线时叫 `unnamed colour group_<n>`；
- **不要做 CSV 引号转义**：RPFM 原样写出（值里的 `\n`/`\t` 在解码时已转成字面两字符）；
- 浮点固定 **4 位小数**（`{:.4}`）；布尔 `true/false`；Sequence 类型用 **base64**；
- `autoresolver_unit_group_categories_enums_tables` 在 schema 里没有定义 → 原生与 CLI **同样跳过**（行为一致）。

细节与坑：

- `*.rpfm_reserved`（`settings` / `notes` / `dependencies_manager_v2`）是 RPFM 存在包里的内部文件，**它的文件列表不显示**——我们的 `VisibleEntries` 同样过滤；`settings.rpfm_reserved` 里存着 `compression_format`（原生也能读）。
- 加密包（老游戏/Arena，`HAS_ENCRYPTED_INDEX/DATA`）**暂不支持**，明确抛错；WH3 的原版包实测不加密（`type=Release, flags=0`），只是条目压缩。
- **LZ4/Zstd 的解压代码已写但本机没有这种包可验**（WH3 6.2+ 格式），需要找一个真包再对拍一次。

验证工具：`probe native-list <pack> [--plain]`、`probe native-extract <pack> <包内路径> <输出文件>`。

自动化验收钩子（学 CRPFM 的 `--xxx-test` 做法）：

```bash
# 打开指定包 + 直接开一张表视图（无人值守截图/检查用）
WarbandStudio.exe --open-table "db/main_units_tables/data__"
# 命令行验证工具（对拍用）
probe native-list <pack> [--plain] / native-extract / native-tsv / native-tsv-all
```

`TreeItem` / `CenterTab` 都覆写了 `ToString()` —— UI Automation 与屏幕阅读器能读到节点名（否则只会读到类型名）。

## 业务知识：三张表的职责 与 兵牌（卡图）解析链

> 这一段是**踩过坑之后定下来的规则**，改卡图/图标相关代码前先读它。

### 三张表各管什么

| 表 | 负责什么 |
|---|---|
| `land_units_tables` | **战场上的单位配置**：往下连战斗实体、近战武器、护甲、盾牌、属性组、间距（近战武器还能继续连接触效果） |
| `main_units_tables` | **战役与名册层面**的单位记录（招募、维护费、容量、精英锁等级…） |
| `unit_variants_tables` | **链接兵牌与单位**。注意：这张表的 `unit` 列用的是 **`land_units_tables` 的 key**，不是 `main_units` 的 key |

### 兵牌图怎么找（实测 966/966 全部命中游戏本体）

```
main_units.unit  →  main_units.land_unit  →  unit_variants_tables[unit = land_unit].unit_card
                                            →  ui/units/icons/<unit_card>.png
```

- **先按 `land_unit` 查变体表**，查不到再退回 `main_units` 的 key（少数包这么写）；
- 候选链（依次试，每条再试 `_2/_3/_4` 数字后缀）：`unit_card` → `land_units.icon` → `land_unit` 名 → `main_units` key；
- 来源链：**① 打开的包自带 → ② 游戏 `data/*.pack`（扫索引建表，345 个包约 1s）→ ③ 随附素材**（开发机兜底，发布版不需要）。

**踩坑记录**：一开始拿 `main_units` 的 key 当文件名去猜（`wh_main_emp_inf_swordsmen`），结果 966 张里有 600 张找不到——实际文件叫 `wh_main_emp_swordsmen.png`（`unit_card` 的值）。改成"读 `unit_variants_tables.unit_card` + 按 `land_unit` 查"之后一次全中：

```
卡图命中 966（包内 0，游戏 966，随附 0，缺 0）
```

**排查入口**：每次启动画布都会把"游戏里没找到的卡（含试过的所有候选名）"打进日志（`%APPDATA%\WarbandStudio\app.log`），以后遇到查不到的卡直接看日志，不必改代码。

### 参考：CRPFM（ChaDeRPFM）的做法

它是**闭源 C# 工具**（只给了发布包），但从 DLL 字符串 + `knowledge/relationships.json` + 它的技术文档能看出：**图片名从 DB 字段读（不猜 key）**、跨"原版 + 依赖 + 当前 mod"解析、图片按需读取 + 有上限的内存缓存、资源索引单独建（它记录每个 pack 的条目数与 sha256）。我们的实现思路与之一致。

## 版本号约定

`release/WarbandStudio_v<号>/`；**发布号 = 当前版本号**（`tools/publish_github.ps1 -Version <号>` 直接发，
不再"推送自动改名"）。`build.ps1` 用 `[version]` 解析（不是 double，"0.10" 会被 double 当成 0.1），
只保留最近 5 个版本（v1.0 留档）；每次发布同步更新根目录 `CHANGELOG.md`。

## 阶段路线

- **已完成（v1.4.x ~ v1.5.x）**：页签图名体系（twui 实际引用解析、"正名优先"让位、换图顺带图名归正）、
  成本工坊 v2、新组命名 `项目key_页签_兵种`、**工程化**（工程目录 + `old/` 历史版本 + 项目 key + 启动空状态 + 从 Pack 复制进工程）
- **已完成（v1.2）**：
  - 三栏壳 + 多 pack 共存（每个 pack 一棵树根，点开的各自展开；原版那棵叫「原版战帮升级」）
  - 文件树只显示**战帮相关**文件（15 张表白名单 + `ui/**warband_upgrades` + `campaign ui/`），兵牌相关表归右下的兵种库
  - 画布：组按 `x/y` 落位、同组 +46 并排、页签（`category`）过滤、拖动改坐标
  - **连线**按 C1 结论读 `unit_upgrade_group_ui_links_tables`（出入口位 1上/2右/3下/4左 + 三个 offset），
    升级关系读 `unit_upgrade_to_unit_groups_tables`（不画线）；体检只报告"有路线没连线/有连线没路线"
  - 右栏：种族 → **传奇派系**两级树（loc 中文名 + 悬浮看 key，「通用」= 非传奇合并）+ 兵种库（实时读 DB：军事编组 ∪ 专属授权 ∪ 建筑招募，再与 `main_units` 求交）
  - 本地化：读游戏 `local_cn.pack`（简中，找不到再退繁中/英文）的 `.loc`，键 → 中文名
  - 启动即选中「战帮画布」（以前 TabControl 的双向绑定会把选中项写成 null，点画布页签没反应）
  - 卡图：DB 图标字段候选链（`unit_variants.unit_card` / `land_units.icon`）→ 游戏包索引 → 随附素材，
    统一抽到 `%APPDATA%\WarbandStudio\iconcache`，页面用 `icons.local` 读
- **画布编辑（v0.1x）**：连接两个组 = 自动写「升级路线 + 界面连线」（成本从已有 Key 里选或自动新建、金额写负）；
  点连线可改金额/等级、交换方向、删除；卡片面板可删卡/删组/改页签；兵种库双击或拖入 = 加兵（写 junction 与组），
  Ctrl+拖兵牌进兵种库 = 删兵。全部编辑先记内存（状态栏显示摘要），导出时一次落表 —— 见 `docs/UI模块与空间约定.md` 第六节
- **待做**：
  - 兵种库双击加进画布后**写回**（`unit_upgrade_groups_tables` + junction + 组坐标一次落盘；现在只记在内存）
  - 派系专属授权（`units_to_exclusive_faction_permissions_tables`，配合加兵时的 `unitXfPlus`）
  - 「剔除无效记录」开关（老工坊有）、loc 显示名（现在显示 key）
  - DB 表网格可编辑 + 新建页签向导（自动写 twui + 两张 png + categories 行）
  - 兵种库卡图补齐（当前帝国 114/160、矮人 56/62 有图；缺口是立绘/infopics 那条链还没接全）

## 地基文档

- `docs/战帮升级_表单关系路线图.md` —— **所有涉及表单的关系路线图**（身份层 → 兵种层 → 升级数据链 → UI 层 → 派系接入），
  每个节点写清作用/键/被谁引用，外加"页签归属推导链"和读表铁律。凡是要自动填表的功能，都必须能沿它找到依据。

## 参考资料（工作区里就有）

- `E:\AAA战锤工作区\rpfm5.06 source code\rpfm-5.0.6\` —— RPFM 源码，**协议文档在 `docs/server/`**（`ws-protocol.md` / `ws-commands.md` / `ws-shared-types.md` 是权威依据）
- `E:\AAA战锤工作区\AssetEditor\TheAssetEditor-master\` —— 派大星上游源码（C#/WPF，布局/tab/主题可借鉴；注意它**没有** DB 表编辑器）
- `E:\AAA战锤工作区\rpfm\rpfm_server.exe` —— 5.0.6 二进制（可用 `RPFM_SERVER_PATH` 指定别处）
