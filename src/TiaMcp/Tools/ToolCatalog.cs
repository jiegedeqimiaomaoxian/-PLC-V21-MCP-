using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Mcp;

namespace TiaMcp.Tools
{
    public static class ToolCatalog
    {
        private const string WorkRoot = @"F:\DeepSeek Work Part\TIA-MCP\work";

        public static void Register(McpServer server)
        {
            server.AddTool(Tool("tia_status", "查看 TIA Portal / 项目 / 仿真 / Openness 权限的整体状态。任何操作前先调用它。", "{}",
                _ => ToolResult.Ok(App.Snapshot().ToString())));

            server.AddTool(Tool("tia_connect", "连接（或启动）TIA Portal。ui=true 时打开可见界面，这样才能截图和看真实梯形图；ui=false 为后台静默模式。", @"{""ui"":{""type"":""boolean"",""description"":""是否带用户界面，默认 true""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var ui = args["ui"]?.Type != JTokenType.Boolean || (bool)args["ui"];
                    App.Tia.Connect(ui);
                    return ToolResult.Ok("已连接 TIA Portal（" + (ui ? "带界面" : "无界面") + "）");
                })));

            server.AddTool(Tool("tia_open_project", "打开已有 TIA 项目（.ap21）。路径给项目文件本身。", @"{""path"":{""type"":""string"",""description"":""项目文件绝对路径""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var path = (string)args["path"];
                    if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空");
                    var project = App.Tia.OpenProject(path);
                    return ToolResult.Ok("已打开项目: " + project.Name + " (" + project.Path + ")");
                })));

            server.AddTool(Tool("tia_create_project", "新建 TIA 项目。directory 是存放目录（不存在会自动建），name 是项目名。", @"{""directory"":{""type"":""string""},""name"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var dir = (string)args["directory"];
                    var name = (string)args["name"];
                    var project = App.Tia.CreateProject(dir, name);
                    return ToolResult.Ok("已新建项目: " + project.Name + " -> " + project.Path);
                })));

            server.AddTool(Tool("tia_save_project", "保存当前项目。", "{}",
                _ => App.Dispatcher.Invoke(() => { App.Tia.Save(); return ToolResult.Ok("已保存"); })));

            server.AddTool(Tool("tia_close_project", "关闭当前项目（不退出 TIA Portal）。", "{}",
                _ => App.Dispatcher.Invoke(() => { App.Tia.CloseProject(); return ToolResult.Ok("已关闭项目"); })));

            server.AddTool(Tool("tia_list_devices", "列出项目里的设备及其 PLC 软件名。", "{}",
                _ => App.Dispatcher.Invoke(() =>
                {
                    var list = App.Tia.Project.Devices.Select(d => new
                    {
                        name = d.Name,
                        type = d.TypeIdentifier,
                        plc = App.Tia.GetPlcSoftware(d)?.Name
                    }).ToList();
                    return ToolResult.Ok(list);
                })));

            server.AddTool(Tool("tia_search_catalog", "在博图硬件目录里搜索 CPU 等订货号，返回可用于 tia_add_plc 的 typeIdentifier。", @"{""filter"":{""type"":""string"",""description"":""如 '6ES7 515' 或 'CPU 1516'""},""max"":{""type"":""integer"",""description"":""最多返回条数，默认 20""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var filter = (string)args["filter"] ?? "";
                    var max = args["max"]?.Value<int>() ?? 20;
                    var portal = App.Tia.Connect(true);
                    var entries = portal.HardwareCatalog.Find(filter).Take(max).Select(e => new
                    {
                        typeIdentifier = e.TypeIdentifier,
                        article = e.ArticleNumber,
                        name = e.TypeName,
                        version = e.Version
                    }).ToList();
                    return ToolResult.Ok(entries);
                })));

            server.AddTool(Tool("tia_add_plc", "从硬件目录添加一个 PLC（如 S7-1500 CPU）。typeIdentifier 用 tia_search_catalog 的返回值。", @"{""typeIdentifier"":{""type"":""string""},""deviceName"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var type = (string)args["typeIdentifier"];
                    var name = (string)args["deviceName"];
                    if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("typeIdentifier 不能为空");
                    var device = App.Tia.AddPlc(type, name ?? "PLC_1");
                    return ToolResult.Ok("已添加设备: " + device.Name);
                })));

            server.AddTool(Tool("tia_list_blocks", "列出某个 PLC 的全部程序块（OB/FB/FC/DB），含编号、语言、修改时间。", @"{""device"":{""type"":""string"",""description"":""设备名，省略则取第一个 PLC""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out var device, (string)args["device"]);
                    var blocks = App.Tia.ListBlocks(plc).OrderBy(b => b.Number).Select(b => new
                    {
                        name = b.Name,
                        number = b.Number,
                        kind = b.GetType().Name,
                        language = b.ProgrammingLanguage.ToString(),
                        modified = b.ModifiedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                        consistent = b.IsConsistent
                    }).ToList();
                    return ToolResult.Ok(new { device = device.Name, count = blocks.Count, blocks });
                })));

            server.AddTool(Tool("tia_export_block", "把程序块导出为 SIMATIC ML XML（可用于审阅 LAD/SCL 结构或再导入）。", @"{""name"":{""type"":""string""},""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out var device, (string)args["device"]);
                    var dir = Path.Combine(WorkRoot, "export");
                    var path = App.Tia.ExportBlockXml(plc, (string)args["name"], dir);
                    return ToolResult.Ok(new { path, xml = File.ReadAllText(path) });
                })));

            server.AddTool(Tool("tia_import_block", "导入 SIMATIC ML XML 格式的程序块（同名覆盖）。xml 或 xmlPath 二选一。", @"{""xml"":{""type"":""string""},""xmlPath"":{""type"":""string""},""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out _, (string)args["device"]);
                    string path = (string)args["xmlPath"];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        var xml = (string)args["xml"];
                        if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("xml 或 xmlPath 必须给一个");
                        path = Path.Combine(WorkRoot, "import", "block-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".xml");
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, xml, new UTF8Encoding(false));
                    }
                    var blocks = App.Tia.ImportBlockXml(plc, path);
                    return ToolResult.Ok(new { imported = blocks.Select(b => b.Name).ToList(), path });
                })));

            server.AddTool(Tool("tia_import_scl", "把 SCL 源码导入成程序块（用外部源生成，支持 FUNCTION_BLOCK / FUNCTION / ORGANIZATION_BLOCK）。", @"{""source"":{""type"":""string"",""description"":""SCL 源码文本""},""name"":{""type"":""string"",""description"":""外部源名，默认 SclSource""},""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out _, (string)args["device"]);
                    var source = (string)args["source"];
                    if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("source 不能为空");
                    var name = (string)args["name"] ?? "SclSource";
                    var path = Path.Combine(WorkRoot, "scl", name + ".scl");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, source, new UTF8Encoding(true));

                    var blocks = App.Tia.ImportScl(plc, path, name);
                    return ToolResult.Ok(new { path, blocks = blocks.Select(b => new { b.Name, b.Number, language = b.ProgrammingLanguage.ToString() }).ToList() });
                })));

            server.AddTool(Tool("tia_compile", "编译 PLC 软件，返回错误/警告数量和明细。写完块之后必须编译。", @"{""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out var device, (string)args["device"]);
                    var result = App.Tia.Compile(plc);
                    var messages = new List<object>();
                    CollectMessages(result, messages, 0);
                    return ToolResult.Ok(new
                    {
                        device = device.Name,
                        state = result.State.ToString(),
                        errors = result.ErrorCount,
                        warnings = result.WarningCount,
                        messages = messages.Take(60).ToList()
                    });
                })));

            server.AddTool(Tool("tia_download", "把 PLC 程序下载到设备（仿真时即下载到 PLCSIM 实例）。", @"{""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var device = App.ResolveDevice((string)args["device"]);
                    var result = App.Tia.Download(device);
                    return ToolResult.Ok(new
                    {
                        device = device.Name,
                        state = result.State.ToString(),
                        messages = result.Messages.Select(m => new { state = m.State.ToString(), message = m.Message }).Take(40).ToList()
                    });
                })));

            server.AddTool(Tool("tia_show_block", "在 TIA Portal 界面里打开指定程序块（用于截图看梯形图）。需要 ui=true 连接。", @"{""name"":{""type"":""string""},""device"":{""type"":""string""}}",
                args => App.Dispatcher.Invoke(() =>
                {
                    var plc = App.ResolvePlc(out _, (string)args["device"]);
                    App.Tia.ShowBlock(plc, (string)args["name"]);
                    return ToolResult.Ok("已在编辑器中打开: " + args["name"]);
                })));

            server.AddTool(Tool("tia_screenshot", "抓取 TIA Portal 窗口截图并保存为 PNG，返回文件路径（可直接用 read_image 查看）。", "{}",
                _ =>
                {
                    var png = Web.Dashboard.CapturePortalPngProxy();
                    if (png == null) return ToolResult.Fail("没有可见的 TIA Portal 窗口（可能以无界面模式运行）");
                    var path = Path.Combine(WorkRoot, "shots", "tia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, png);
                    return ToolResult.Ok(new { path, bytes = png.Length });
                }));

            server.AddTool(Tool("dashboard_url", "返回本机图形监视台地址（项目树 / 博图画面 / 仿真实例 / 启保停演示）。", "{}",
                _ => ToolResult.Ok(App.Dashboard?.Url ?? "仪表盘未启动")));

            // ---------------- 监视台内置软 PLC（启保停） ----------------

            server.AddTool(Tool("latch_status", "读取监视台里启保停软 PLC 的实时状态（变量值 / 扫描次数 / 运行时长 / 最近事件）。", "{}",
                _ => ToolResult.Ok(App.Plc.Snapshot())));

            server.AddTool(Tool("latch_press", "按一下软 PLC 的按钮（模拟点动），可在监视台网页上看到梯形图实时变化。", @"{""input"":{""type"":""string"",""description"":""start / stop / fault / reset""},""holdMs"":{""type"":""integer"",""description"":""按下保持毫秒数，默认 220""}}",
                args =>
                {
                    var input = (string)args["input"] ?? "start";
                    var hold = args["holdMs"]?.Value<int>() ?? 220;
                    App.Plc.Press(input, hold);
                    return ToolResult.Ok(new { pressed = input, holdMs = hold, state = App.Plc.Snapshot() });
                }));

            server.AddTool(Tool("latch_reset", "清除软 PLC 的故障锁存（等同按 Reset）。", "{}",
                _ =>
                {
                    App.Plc.ClearFault();
                    return ToolResult.Ok(App.Plc.Snapshot());
                }));

            // ---------------- 仿真 ----------------

            server.AddTool(Tool("plcsim_instances", "列出本机 PLCSIM 运行时里的仿真实例（名字 / 状态 / IP）。", "{}",
                _ => ToolResult.Ok(new { available = App.Sim.IsAvailable, version = App.Sim.Version, instances = App.Sim.ListInstances() })));

            server.AddTool(Tool("plcsim_create", "创建一个新的虚拟 PLC 实例（默认 CPU 1516-3 PN/DP）。", @"{""name"":{""type"":""string""}}",
                args =>
                {
                    var name = (string)args["name"];
                    var inst = App.Sim.Create(name);
                    return ToolResult.Ok("已创建实例: " + inst.Name + " (状态 " + inst.OperatingState + ")");
                }));

            server.AddTool(Tool("plcsim_release", "释放（注销）仿真实例，回收它占用的内存。", @"{""name"":{""type"":""string""}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    var released = inst.Name;
                    App.Sim.Release(inst);
                    return ToolResult.Ok("已释放实例: " + released);
                }));

            server.AddTool(Tool("plcsim_power", "给仿真实例上电/断电。", @"{""name"":{""type"":""string""},""on"":{""type"":""boolean""}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    var on = args["on"]?.Type != JTokenType.Boolean || (bool)args["on"];
                    App.Sim.Power(inst, on);
                    return ToolResult.Ok(inst.Name + " 电源 " + (on ? "ON" : "OFF") + "，当前状态 " + inst.OperatingState);
                }));

            server.AddTool(Tool("plcsim_run", "让仿真实例进入 RUN / STOP。", @"{""name"":{""type"":""string""},""run"":{""type"":""boolean""}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    var run = args["run"]?.Type != JTokenType.Boolean || (bool)args["run"];
                    App.Sim.Run(inst, run);
                    return ToolResult.Ok(inst.Name + " 已 " + (run ? "RUN" : "STOP") + "，当前状态 " + inst.OperatingState);
                }));

            server.AddTool(Tool("plcsim_tags", "列出仿真实例里可读的变量名（编译下载后才有）。", @"{""name"":{""type"":""string""},""limit"":{""type"":""integer""}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    var limit = args["limit"]?.Value<int>() ?? 200;
                    var tags = App.Sim.TagNames(inst, limit);
                    return ToolResult.Ok(new { count = tags.Count, tags });
                }));

            server.AddTool(Tool("plcsim_read", "批量读取仿真实例的变量值。", @"{""name"":{""type"":""string""},""tags"":{""type"":""array"",""items"":{""type"":""string""}}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    var tags = (args["tags"] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
                    var values = new JObject();
                    foreach (var tag in tags)
                    {
                        try { values[tag] = JToken.FromObject(App.Sim.ReadTag(inst, tag)); }
                        catch (Exception ex) { values[tag] = "ERR: " + ex.Message; }
                    }
                    return ToolResult.Ok(values);
                }));

            server.AddTool(Tool("plcsim_write", "写仿真实例的单个变量。", @"{""name"":{""type"":""string""},""tag"":{""type"":""string""},""value"":{""type"":""string""}}",
                args =>
                {
                    var inst = App.Sim.Get((string)args["name"]);
                    App.Sim.WriteTag(inst, (string)args["tag"], (string)args["value"]);
                    return ToolResult.Ok("已写入 " + args["tag"] + " = " + args["value"]);
                }));
        }

        private static void CollectMessages(object result, List<object> target, int depth)
        {
            if (result == null || depth > 3) return;
            var prop = result.GetType().GetProperty("Messages");
            var messages = prop?.GetValue(result) as System.Collections.IEnumerable;
            if (messages == null) return;

            foreach (var message in messages)
            {
                if (message == null) continue;
                var m = message.GetType();
                target.Add(new
                {
                    state = m.GetProperty("State")?.GetValue(message)?.ToString(),
                    description = m.GetProperty("Description")?.GetValue(message)?.ToString(),
                    path = m.GetProperty("Path")?.GetValue(message)?.ToString()
                });
                CollectMessages(message, target, depth + 1);
            }
        }

        private static ToolDefinition Tool(string name, string description, string propertiesJson, Func<JObject, ToolResult> handler)
        {
            var properties = string.IsNullOrWhiteSpace(propertiesJson) ? new JObject() : JObject.Parse(propertiesJson);
            return new ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = properties
                },
                Handler = handler
            };
        }
    }
}
