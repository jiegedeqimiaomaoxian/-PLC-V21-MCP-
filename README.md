# TIA Portal V21 × MCP 桥接

让 agent 直接对博图 V21 做 PLC 编程：建项目、加 CPU、写程序块、编译、下载、跑仿真，并配一个本机网页监视台看图看变量。

```
DSH(agent) ──MCP stdio──> TiaMcp.exe (net48, 单进程)
                            ├─ Openness   : 项目 / 设备 / 程序块(SIMATIC ML+SCL) / 编译 / 下载 / 打开块
                            ├─ PLCSIM API : 虚拟 PLC 实例 起停上电、变量读写
                            └─ HttpListener: http://127.0.0.1:5199/ 图形监视台 + PrintWindow 抓博图窗口
```

## 一、前置条件（只差一次重新登录）

1. **Windows 组**：账户必须在 `Siemens TIA Openness` 组里（已加过）。
   Windows **只在新登录时刷新组成员**，加完必须注销重登一次（或重启），否则永远报
   `不是 windows 组 'Siemens TIA Openness' 的成员`。
2. **许可证**：ALM 里要有 STEP 7 授权，否则编译/下载会被拦。
3. **PLCSIM**：`F:\DeepSeek Work Part\PLCSIM_V21`（S7-PLCSIM V21）或 PLCSIM Advanced 运行时装好即可，
   服务在跑就能被 `plcsim_*` 工具发现。

## 二、怎么用

已经在 DSH 里注册成 MCP 服务器（`~/.dsh/profiles/web/cordis.patch.yml` 的 `mcp-tia` 项），
重启 DSH 后模型就能看到 `mcp__tia__*` 工具，直接说「用博图建个电机启保停程序并仿真」即可。

常用顺序：

```
tia_status                # 先看 Portal / 项目 / Openness 组 / 仿真是否就绪
tia_search_catalog        # 搜 '6ES7 516' 之类，拿到 typeIdentifier
tia_create_project        # 新建项目
tia_add_plc               # 加 CPU（S7-1500）
tia_import_scl            # 提交 SCL 源码，生成 FB/FC/OB
tia_compile               # 编译，拿错误明细
tia_download              # 下载到仿真设备
plcsim_power / plcsim_run # 上电、RUN
plcsim_read               # 读变量实时值
tia_show_block            # 让博图打开某块（界面上就是梯形图）
tia_screenshot            # 截图，模型可以直接"看"梯形图
dashboard_url             # 网页监视台
```

## 三、图形监视台

`http://127.0.0.1:5199/`（端口被占用会自动 +100）

- 左栏：设备 / 程序块树，点块 = 在博图里打开它
- 中栏：博图窗口实时截图（3 秒刷新，PrintWindow，不需要窗口在前台）
- 右栏：PLCSIM 实例状态

## 四、排错

| 现象 | 原因 / 处理 |
| --- | --- |
| `不是 windows 组 'Siemens TIA Openness' 的成员` | 注销重登（组已加，令牌没刷新） |
| `Unable to connect to TIA Portal. Please ensure that "Copy Local" is set to false` | `bin` 目录里混进了 `Siemens.Engineering*.dll`，删掉它们（csproj 里这些引用必须是 `Private=false`） |
| `没有可用的下载目标` | 先在博图里给设备配好目标（PLCSIM 实例），或用界面手动下载 |
| `Error Code: -52, IsEmpty`（plcsim_run） | 虚拟 PLC 里还没下载程序，先编译+下载 |
| `plcsim_*` 报运行时不可用 | S7-PLCSIM / PLCSIM Advanced 运行时没启动 |

## 五、目录

```
src\TiaMcp        桥接本体（net48）
  App.cs          STA 调度器 + 全局状态 + 快照
  Mcp\            极简 MCP 服务器（换行分隔 JSON-RPC）
  Tia\TiaSession  Openness 封装
  Tia\PlcSimBridge PLCSIM 运行时 API 封装
  Web\Dashboard   监视台 + 截图
  Tools\          25+ 个工具的定义与实现
src\TiaSpike      探针（反射/连接/API dump）
docs\openness-api.txt  反射导出的完整 API 清单（写工具时查签名用）
tools\apiq.ps1    在 API 清单里按类型名查成员
work\             导出/导入/截图等临时产物
samples\          示例 SCL
```

## 六、已知限制

- 下载（`tia_download`）走的是"第一个可用目标配置"，没做目标选择 UI；复杂组态下可能需要在博图里手动下载一次。
- 梯形图渲染没有自己做 SVG，走的是"打开真实块 + 截图"，好处是不会画错，坏处是必须带界面跑。
- 只支持单实例、单项目（进程内一个 TIA Portal 会话）。
