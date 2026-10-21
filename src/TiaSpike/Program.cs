using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace TiaSpike
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
        /// Openness 要求程序集从安装路径加载（注册表里登记的绝对路径）。
        /// Copy Local=false 之后就靠这里解析。
        /// </summary>
        private static Assembly ResolveSiemens(object sender, ResolveEventArgs args)
        {
            var simpleName = new AssemblyName(args.Name).Name;
            foreach (var dir in ProbeDirs)
            {
                var candidate = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }

        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSiemens;
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[spike] UNHANDLED " + ex.GetType().FullName + ": " + ex.Message);
                for (var e = ex.InnerException; e != null; e = e.InnerException)
                    Console.WriteLine("   inner " + e.GetType().FullName + ": " + e.Message);
                return 3;
            }
        }

        private static void DumpApi(string outPath)
        {
            var assemblies = new[]
            {
                typeof(Siemens.Engineering.TiaPortal).Assembly,
                typeof(Siemens.Engineering.SW.PlcSoftware).Assembly,
                typeof(Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager).Assembly
            }.Distinct().ToArray();

            var sb = new System.Text.StringBuilder();
            foreach (var asm in assemblies)
            {
                sb.AppendLine("########## " + asm.GetName().Name + " " + asm.GetName().Version);
                Type[] types;
                try { types = asm.GetExportedTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types.Where(t => t.IsPublic || t.IsNestedPublic).OrderBy(t => t.FullName))
                {
                    sb.AppendLine();
                    sb.AppendLine("### " + t.FullName + (t.BaseType != null && t.BaseType != typeof(object) ? " : " + t.BaseType.FullName : ""));

                    if (t.IsEnum)
                    {
                        foreach (var n in Enum.GetNames(t)) sb.AppendLine("  ENUM " + n);
                        continue;
                    }

                    const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                    foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        sb.AppendLine("  CTOR (" + string.Join(", ", c.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");

                    foreach (var p in t.GetProperties(F).OrderBy(p => p.Name))
                        sb.AppendLine("  PROP " + p.PropertyType.Name + " " + p.Name + " { " + (p.CanRead ? "get; " : "") + (p.CanWrite ? "set; " : "") + "}");

                    foreach (var m in t.GetMethods(F).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
                        sb.AppendLine("  METH " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
                }
            }

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, sb.ToString(), new System.Text.UTF8Encoding(false));
            Console.WriteLine("[spike] types dumped: " + (sb.Length / 1024) + " KB");
        }

        private static int Run(string[] args)
        {
            var connect = args.Any(a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase));
            var ui = args.Any(a => a.Equals("--ui", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("== identity ==");
            Console.WriteLine("user      : " + Environment.UserName + " @ " + Environment.MachineName);
            Console.WriteLine("x64       : " + (IntPtr.Size == 8));
            Console.WriteLine("clr       : " + Environment.Version);

            var tiaPortalType = typeof(Siemens.Engineering.TiaPortal);
            Console.WriteLine("== openness assembly ==");
            Console.WriteLine("assembly  : " + tiaPortalType.Assembly.FullName);
            Console.WriteLine("location  : " + tiaPortalType.Assembly.Location);

            var dumpIdx = Array.FindIndex(args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
            if (dumpIdx >= 0)
            {
                var outPath = dumpIdx + 1 < args.Length && !args[dumpIdx + 1].StartsWith("--")
                    ? args[dumpIdx + 1]
                    : @"F:\DeepSeek Work Part\TIA-MCP\docs\openness-api.txt";
                DumpApi(outPath);
                Console.WriteLine("[spike] api dump -> " + outPath);
                return 0;
            }

            if (!connect)
            {
                Console.WriteLine("[spike] reflection only");
                return 0;
            }

            var mode = ui ? Siemens.Engineering.TiaPortalMode.WithUserInterface : Siemens.Engineering.TiaPortalMode.WithoutUserInterface;
            var sw = Stopwatch.StartNew();
            Console.WriteLine("[spike] creating TiaPortal(" + mode + ") ...");
            try
            {
                var portal = new Siemens.Engineering.TiaPortal(mode);
                sw.Stop();
                Console.WriteLine("[spike] CONNECTED in " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
                Console.WriteLine("  projects   : " + portal.Projects.Count);
                foreach (var p in portal.Projects)
                    Console.WriteLine("    - " + p.Name + "  " + p.Path);
                portal.Dispose();
                Console.WriteLine("[spike] disposed OK");
                return 0;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine("[spike] FAILED after " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
                for (var e = ex; e != null; e = e.InnerException)
                    Console.WriteLine("  " + e.GetType().FullName + ": " + e.Message);
                return 2;
            }
        }
    }
}

