using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Mcp
{
    /// <summary>工具执行结果：文本 + 是否错误。</summary>
    public sealed class ToolResult
    {
        public string Text;
        public bool IsError;

        public static ToolResult Ok(string text) => new ToolResult { Text = text ?? "ok" };
        public static ToolResult Ok(object payload) => new ToolResult { Text = Serialize(payload) };
        public static ToolResult Fail(string text) => new ToolResult { Text = text, IsError = true };

        public static string Serialize(object payload)
        {
            if (payload == null) return "ok";
            if (payload is string s) return s;
            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }
    }

    public sealed class ToolDefinition
    {
        public string Name;
        public string Description;
        public JObject InputSchema = new JObject(new JProperty("type", "object"), new JProperty("properties", new JObject()));
        public Func<JObject, ToolResult> Handler;
    }

    /// <summary>
    /// 极简 MCP 服务器：换行分隔的 JSON-RPC 2.0（MCP stdio 传输）。
    /// stdout 只允许协议消息，日志一律走 stderr。
    /// </summary>
    public sealed class McpServer
    {
        private readonly Dictionary<string, ToolDefinition> _tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
        private readonly string _serverName;
        private readonly string _serverVersion;
        private readonly object _writeLock = new object();

        public McpServer(string serverName, string serverVersion)
        {
            _serverName = serverName;
            _serverVersion = serverVersion;
        }

        public void AddTool(ToolDefinition tool)
        {
            _tools[tool.Name] = tool;
        }

        public void RunStdio()
        {
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

            Log("mcp: stdio loop ready, tools=" + _tools.Count);

            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JObject request;
                try
                {
                    request = JObject.Parse(line);
                }
                catch (Exception ex)
                {
                    WriteMessage(stdout, RpcError(null, -32700, "parse error: " + ex.Message));
                    continue;
                }

                var response = Handle(request);
                if (response != null) WriteMessage(stdout, response);
            }

            Log("mcp: stdin closed, exiting");
        }

        private void WriteMessage(StreamWriter stdout, JObject message)
        {
            var json = message.ToString(Formatting.None);
            lock (_writeLock)
            {
                stdout.WriteLine(json);
                stdout.Flush();
            }
        }

        private JObject Handle(JObject request)
        {
            var method = (string)request["method"];
            var id = request["id"];
            var isNotification = id == null || id.Type == JTokenType.Null;

            if (isNotification)
            {
                switch (method)
                {
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return null;
                    default:
                        return null;
                }
            }

            switch (method)
            {
                case "initialize":
                    return RpcResult(id, new JObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = _serverName,
                            ["version"] = _serverVersion
                        }
                    });

                case "ping":
                    return RpcResult(id, new JObject());

                case "tools/list":
                    return RpcResult(id, new JObject { ["tools"] = new JArray(_tools.Values.Select(DescribeTool)) });

                case "tools/call":
                    return HandleToolCall(id, request["params"] as JObject);

                case "resources/list":
                    return RpcResult(id, new JObject { ["resources"] = new JArray() });

                case "prompts/list":
                    return RpcResult(id, new JObject { ["prompts"] = new JArray() });

                case "shutdown":
                    return RpcResult(id, new JObject());

                default:
                    return RpcError(id, -32601, "method not found: " + method);
            }
        }

        private JObject DescribeTool(ToolDefinition tool)
        {
            return new JObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema
            };
        }

        private JObject HandleToolCall(JToken id, JObject p)
        {
            var name = (string)p?["name"];
            if (string.IsNullOrEmpty(name)) return RpcError(id, -32602, "tools/call: missing name");
            if (!_tools.TryGetValue(name, out var tool)) return RpcError(id, -32602, "unknown tool: " + name);

            var args = p["arguments"] as JObject ?? new JObject();
            ToolResult result;
            try
            {
                result = tool.Handler(args) ?? ToolResult.Ok("ok");
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail(Describe(ex));
                Log("tool " + name + " threw: " + ex);
            }

            return RpcResult(id, new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = result.Text ?? "" }),
                ["isError"] = result.IsError
            });
        }

        private static JObject RpcResult(JToken id, JObject result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        /// <summary>把包装异常的根因摊平，方便判断到底是权限、许可证还是调用错误。</summary>
        internal static string Describe(Exception ex)
        {
            var builder = new StringBuilder();
            var depth = 0;
            for (var e = ex; e != null && depth < 5; e = e.InnerException, depth++)
            {
                if (depth > 0) builder.Append("  <<  ");
                builder.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return builder.ToString();
        }

        private static JObject RpcError(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        internal static void Log(string message)
        {
            try { Console.Error.WriteLine("[tia-mcp] " + message); } catch { }
        }
    }
}
