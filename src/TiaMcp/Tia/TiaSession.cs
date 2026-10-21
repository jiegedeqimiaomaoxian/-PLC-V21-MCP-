using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Tia
{
    /// <summary>Openness 会话封装：一个进程内只连一个 TIA Portal 实例。</summary>
    public sealed class TiaSession : IDisposable
    {
        private TiaPortal _portal;
        private Project _project;
        private readonly object _gate = new object();

        public bool IsPortalOpen => _portal != null;
        public bool IsProjectOpen => _project != null;

        public Project Project => _project;
        public TiaPortal Portal => _portal;

        public TiaPortal Connect(bool withUserInterface)
        {
            lock (_gate)
            {
                if (_portal != null) return _portal;

                try
                {
                    _portal = new TiaPortal(withUserInterface ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface);
                    return _portal;
                }
                catch (Exception ex)
                {
                    var text = Flatten(ex);
                    if (text.IndexOf("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new InvalidOperationException(
                            "当前登录令牌里没有 'Siemens TIA Openness' 组，Openness 拒绝连接。\n" +
                            "组已经加过了，但 Windows 只在新登录时刷新组成员：请注销后重新登录（或重启），再重开 DSH。\n" +
                            "原始错误: " + text);
                    }
                    throw new InvalidOperationException("连接 TIA Portal 失败: " + text);
                }
            }
        }

        private static string Flatten(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null && parts.Count < 4; e = e.InnerException) parts.Add(e.Message);
            return string.Join(" << ", parts);
        }

        public Project OpenProject(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("项目文件不存在: " + path);

            // 已有同名项目打开则直接复用
            var portal = Connect(true);
            foreach (var p in portal.Projects)
            {
                if (string.Equals(p.Path?.FullName, file.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    _project = p;
                    return p;
                }
            }

            _project = portal.Projects.Open(file);
            return _project;
        }

        public Project CreateProject(string directory, string name)
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) dir.Create();
            var portal = Connect(true);
            _project = portal.Projects.Create(dir, name);
            return _project;
        }

        public void Save() => _project?.Save();

        public void CloseProject()
        {
            _project?.Close();
            _project = null;
        }

        // ---------------- 设备 ----------------

        public Device AddPlc(string typeIdentifier, string deviceName)
        {
            RequireProject();
            return _project.Devices.CreateWithItem(typeIdentifier, deviceName, deviceName);
        }

        public PlcSoftware GetPlcSoftware(Device device)
        {
            if (device == null) return null;
            foreach (var item in device.DeviceItems)
            {
                var container = item.GetService<SoftwareContainer>();
                if (container?.Software is PlcSoftware plc) return plc;
            }
            return null;
        }

        public PlcSoftware GetFirstPlcSoftware(out Device device)
        {
            RequireProject();
            foreach (var d in _project.Devices)
            {
                var plc = GetPlcSoftware(d);
                if (plc != null) { device = d; return plc; }
            }
            device = null;
            return null;
        }

        // ---------------- 块 ----------------

        public IReadOnlyList<PlcBlock> ListBlocks(PlcSoftware plc)
        {
            var result = new List<PlcBlock>();
            CollectBlocks(plc.BlockGroup, result);
            return result;
        }

        private static void CollectBlocks(PlcBlockGroup group, List<PlcBlock> target)
        {
            foreach (var b in group.Blocks) target.Add(b);
            foreach (var g in group.Groups) CollectBlocks(g, target);
        }

        public PlcBlock FindBlock(PlcSoftware plc, string name)
        {
            return ListBlocks(plc).FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public string ExportBlockXml(PlcSoftware plc, string name, string outDir)
        {
            var block = FindBlock(plc, name) ?? throw new InvalidOperationException("找不到块: " + name);
            var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(outDir, safe + ".xml");
            Directory.CreateDirectory(outDir);
            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return path;
        }

        public IList<PlcBlock> ImportBlockXml(PlcSoftware plc, string xmlPath)
        {
            return plc.BlockGroup.Blocks.Import(new FileInfo(xmlPath), ImportOptions.Override);
        }

        public IList<PlcBlock> ImportScl(PlcSoftware plc, string sclPath, string sourceName)
        {
            var before = new HashSet<string>(ListBlocks(plc).Select(b => b.Name));
            var sources = plc.ExternalSourceGroup.ExternalSources;
            var src = sources.CreateFromFile(sourceName, sclPath);
            src.GenerateBlocksFromSource();

            return ListBlocks(plc).Where(b => !before.Contains(b.Name)).ToList();
        }

        public PlcBlock CreateFb(PlcSoftware plc, string name, int number, ProgrammingLanguage language, bool autoNumber = false)
        {
            return plc.BlockGroup.Blocks.CreateFB(name, autoNumber, number, language);
        }

        public PlcBlock CreateDb(PlcSoftware plc, string name, int number, bool autoNumber = false)
        {
            return plc.BlockGroup.Blocks.CreateInstanceDB(name, autoNumber, number, name);
        }

        public void ShowBlock(PlcSoftware plc, string name)
        {
            var block = FindBlock(plc, name) ?? throw new InvalidOperationException("找不到块: " + name);
            block.ShowInEditor();
        }

        public void DeleteBlock(PlcSoftware plc, string name)
        {
            var block = FindBlock(plc, name) ?? throw new InvalidOperationException("找不到块: " + name);
            block.Delete();
        }

        // ---------------- 变量表 ----------------

        public PlcTagTable GetDefaultTagTable(PlcSoftware plc)
        {
            return plc.TagTableGroup.TagTables.FirstOrDefault(t => t.IsDefault) ?? plc.TagTableGroup.TagTables.FirstOrDefault();
        }

        // ---------------- 编译 / 下载 ----------------

        public CompilerResult Compile(PlcSoftware plc)
        {
            var compilable = plc.GetService<ICompilable>()
                ?? throw new InvalidOperationException("PlcSoftware 不支持编译服务");
            return compilable.Compile();
        }

        public DownloadResult Download(Device device)
        {
            var provider = device.GetService<DownloadProvider>()
                ?? throw new InvalidOperationException("设备不支持下载服务 (DownloadProvider)");

            // V21 里 Configuration 是 ConnectionConfiguration，Download() 要的是 IConfiguration，
            // 实际可用目标在 Modes 集合里，这里用反射挑第一个目标下载。
            var configuration = provider.Configuration;
            var modes = configuration?.GetType().GetProperty("Modes")?.GetValue(configuration) as System.Collections.IEnumerable;

            object chosen = null;
            if (modes != null)
                foreach (var mode in modes) { chosen = mode; break; }

            if (chosen != null)
            {
                var method = provider.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "Download" && m.GetParameters().Length == 4);

                if (method != null)
                {
                    try
                    {
                        return (DownloadResult)method.Invoke(provider, new[] { chosen, null, null, (object)DownloadOptions.Software });
                    }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        throw ex.InnerException;
                    }
                }
            }

            throw new InvalidOperationException("没有可用的下载目标：请先在 TIA Portal 里为设备配置目标（例如 PLCSIM 实例），或用 tia_show_block/界面手动下载。");
        }

        private void RequireProject()
        {
            if (_project == null) throw new InvalidOperationException("没有打开的项目，先调用 tia_open_project 或 tia_create_project");
        }

        public void Dispose()
        {
            try { _project?.Close(); } catch { }
            try { _portal?.Dispose(); } catch { }
            _project = null;
            _portal = null;
        }
    }
}
