using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TiaMcp.Mcp;
using TiaMcp.Tools;

namespace TiaMcp
{
    internal static class Program
    {
        private const string PortalRoot = @"F:\DeepSeek Work Part\botu V21\Portal V21";

        private static readonly string[] ProbeDirs =
        {
            Path.Combine(PortalRoot, @"PublicAPI\V21\net48"),
            Path.Combine(PortalRoot, @"Bin\PublicAPI"),
            Path.Combine(PortalRoot, @"Bin\PublicAPI\Client"),
            Path.Combine(PortalRoot, @"Bin\PublicAPI\Server"),
        };

        /// <summary>
        /// Openness 要求 Siemens.Engineering.* 从安装路径加载（注册表登记的绝对路径），
        /// 所以这些引用都是 Copy Local=false，靠这里的解析器兜底。
        /// </summary>
        private static Assembly ResolveSiemens(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            if (simpleName == null) return null;
            if (!simpleName.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;

            foreach (var dir in ProbeDirs)
            {
                var candidate = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(candidate))
                {
                    try { return Assembly.LoadFrom(candidate); }
                    catch (Exception ex) { McpServer.Log("resolve " + simpleName + " failed: " + ex.Message); }
                }
            }
            return null;
        }

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiemens;

            var port = 5199;
            var dashboard = !args.Any(a => a.Equals("--no-dashboard", StringComparison.OrdinalIgnoreCase));
            var selfTest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));

            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--port", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(args[i + 1], out port);

            if (dashboard)
            {
                for (var attempt = 0; attempt < 3 && App.Dashboard == null; attempt++)
                {
                    try
                    {
                        var d = new Web.Dashboard(port + attempt * 100);
                        d.Start();
                        App.Dashboard = d;
                    }
                    catch (Exception ex)
                    {
                        McpServer.Log("dashboard start on " + (port + attempt * 100) + " failed: " + ex.Message);
                    }
                }
            }

            if (selfTest)
            {
                Console.WriteLine(App.Snapshot().ToString());
                return 0;
            }

            App.Plc.Start();

            var server = new McpServer("tia-portal-v21", "0.1.0");
            ToolCatalog.Register(server);

            McpServer.Log("tia-mcp ready: tools registered, dashboard=" + (App.Dashboard?.Url ?? "off"));
            server.RunStdio();
            return 0;
        }
    }
}
