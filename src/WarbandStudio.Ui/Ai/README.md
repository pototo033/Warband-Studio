# 战帮工坊 AI 接口

本版本提供 MCP stdio 服务，可连接支持 MCP 的 AI 客户端。工具清单和参数格式由 tools/list 自动返回。

## 连接正在使用的工坊

1. 在工具栏点击「AI 接口」。
2. 点击「启用当前窗口接口」。
3. 复制「当前工坊窗口」配置，添加到 MCP 客户端。
4. 连接后先调用 studio_status，确认工程、当前 Pack 和未保存编辑。

这条连接使用当前窗口的同一份编辑会话。指定 pack 查询或编辑时会同步选中该包，AI 修改会刷新画布及已打开的表。修改支持撤销重做，studio_save 使用现有备份和 RPFM 保存校验。

「关闭当前窗口接口」会断开客户端。工坊重新启动后，需要复制该窗口的新配置。同一 Windows 用户可连接此窗口的命名管道。

## 独立会话

工程会记录通过 studio_open_pack(reference=true) 或界面「加载兵种 mod」成功加载的参考包路径；重开同一工程时自动按路径加载，参考包仍只读。关闭/切换工程保留路径记录；明确调用 studio_close_pack 关闭某个参考包，或在「mod 兵种」中右键卸载，会取消它的自动加载。失效路径提示跳过并保留记录。

在「AI 接口」窗口复制「独立会话」配置，也可以按实际安装位置填写下方配置。

```json
{
  "mcpServers": {
    "warband-studio": {
      "command": "C:\\WarbandStudio\\WarbandStudio.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

客户端会启动一个独立编辑会话。游戏目录默认读取本机设置，也可以添加 "--game-dir" 和游戏根目录参数，或调用 studio_configure。

独立会话通过标准输入输出交互，退出前应调用 studio_save。标准输入关闭后，会话结束，内存中的未保存编辑随会话释放。

## 工具

| 工具 | 用途 |
| --- | --- |
| studio_status | 会话、工程、打开的 Pack、修订号、未保存编辑、撤销深度 |
| studio_configure | 设置包含 data 的游戏根目录 |
| studio_project | 新建、打开、关闭工程，导入 Pack/WUU 模板，管理识别清单 |
| studio_open_pack / studio_select_pack / studio_close_pack | 打开、切换、关闭 Pack，加载外部兵种参考包 |
| studio_files / studio_table / studio_file | 查询有效文件、表结构、行数据与原始文件，暂存本地文件 |
| studio_canvas | 页签、组坐标、成员、升级、连线和派系显示规则 |
| studio_factions / studio_units | 种族、派系、军事组和兵种库，支持外部包及未分配单位 |
| studio_costs | 金币和多资源成本，支持新增、替换或删除某项资源、重命名及引用查询 |
| studio_edit | 批量修改组、单位、路线、连线、页签、素材和军事组授权 |
| studio_table_edit | 带旧行快照校验的表单元格编辑，组 key 重命名同步引用 |
| studio_undo / studio_redo | 对目标 Pack 撤销或重做一步 |
| studio_validate / studio_save | 按当前编辑编译并运行 RPFM 校验，保存或另存 |
| studio_resolve_rules | 根据冲突结果选择重复兵升级规则 |
| studio_history | 查看、恢复工程中的历史包 |
| studio_assets | 查看素材库、导入素材、将素材添加到 Pack |
| studio_bundle / studio_logs | 导出编辑与日志，查询接口会话日志 |

studio_edit 支持的 action 与逐项参数可从 tools/list 获取。常用操作包括 new_group、add_unit、remove_unit、remove_group、rename_group、move_group、set_axis、set_category、merge_groups、split_group、move_units、new_tab、rename_tab、delete_tab、copy_tab、paste_tab、open_tabs、close_tab、import_tab、set_editor_races、set_visibility、set_art、add_route、update_route、swap_route、remove_route、set_link、remove_link、grant_permissions、remove_permissions、rename_file 和 remove_file。

## 一次编辑

先打开 Pack，再读取 studio_canvas 或 studio_table。以下是 tools/call 的 arguments 示例。

```json
{
  "pack": "C:\\MyProject\\packs\\example.pack",
  "expectedRevision": 4,
  "operations": [
    {"action": "move_group", "group": "author_EMP_swordsmen", "x": 150, "y": 300},
    {"action": "move_group", "group": "author_EMP_spearmen", "x": 250, "y": 300}
  ]
}
```

pack 填写已打开 Pack 的绝对路径。expectedRevision 可使用上一次结果 state.revision，数据已经改变时会返回 stale_revision。整批操作作为一次撤销记录，任一操作失败都会回滚。

表编辑的 row 从 0 开始，expected 填写 studio_table 返回的完整 values 数组。同一行修改多个字段时共用批次开始前的快照，重复指定同一单元格会被拒绝。表已变化时会拒绝过期行快照。

金币 gold 与资源 amount 使用带符号数值，负数代表消耗。修改多资源成本时，replacePool 指定要替换的关联，append=true 追加资源。通过 add_unit 加兵会保留已有军事组授权，新增授权需明确调用 grant_permissions。

## 保存和错误处理

正常返回包含 ok、result 和 state。失败返回 isError=true，并在 structuredContent.error 中给出 code、message 和 details。

- studio_save 省略 path 时写回当前 Pack，并沿用旧包备份。
- 指定其他 path 时导出副本，已有目标文件需传 overwrite=true。
- RPFM Error 阻止写入，原包及待保存编辑保留。诊断条目包含表路径、错误类型、行列和可获取的单位 key。
- Warning 允许保存，diagnostics 中返回警告记录。
- 重复兵规则冲突返回 rule_conflict 与选项 ID。使用 studio_resolve_rules 提交 choices，再重试保存。
- 关闭或切换含修改的工程时，unsaved 默认为 fail。明确传 save 或 discard 后执行相应处理。
- 导出复现数据时调用 studio_bundle，包含底本、当前修改、引用包、撤销重做和日志。

请求按会话串行执行，支持初始化协商、ping、工具发现、结构化工具结果和取消通知。取消会阻止尚未开始的操作，已经进入文件提交的保存会完成该次原子写入。

协议参考见 [MCP stdio](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)、[工具调用](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)。
