using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using TiaMcp.Tia;

namespace TiaMcp
{
    /// <summary>
    /// Openness 只能在创建它的线程上安全访问，这里用一个专用 STA 线程串行化所有调用，
    /// MCP 线程和仪表盘线程都通过 Invoke 进入。
    /// </summary>
    public sealed class OpennessDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public OpennessDispatcher()
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "openness-sta" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Pump()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Mcp.McpServer.Log("dispatcher: " + ex.Message); }
            }
        }

        public T Invoke<T>(Func<T> func, int timeoutMs = 600000)
        {
            T result = default(T);
            Exception error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });

                if (!done.Wait(timeoutMs)) throw new TimeoutException("Openness 调用超时");
            }

            if (error != null) throw error;
            return result;
        }

        public void Invoke(Action action, int timeoutMs = 600000) => Invoke<object>(() => { action(); return null; }, timeoutMs);

        public void Dispose() => _queue.CompleteAdding();
    }

    /// <summary>全局应用状态：TIA 会话 + 仿真桥 + 仪表盘。</summary>
    public static class App
    {
        public static readonly TiaSession Tia = new TiaSession();
        public static readonly PlcSimBridge Sim = new PlcSimBridge();
        public static readonly SoftPlc Plc = new SoftPlc();
        public static readonly OpennessDispatcher Dispatcher = new OpennessDispatcher();
        public static Web.Dashboard Dashboard;
        public static readonly string WorkRoot = @"F:\DeepSeek Work Part\TIA-MCP\work";

        public static T Tia2<T>(Func<T> f) => Dispatcher.Invoke(f);

        public static bool IsOpennessGroupInToken()
        {
            try
            {
                var group = new System.Security.Principal.NTAccount(Environment.MachineName, "Siemens TIA Openness");
                var sid = (System.Security.Principal.SecurityIdentifier)group.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return identity.Groups != null && identity.Groups.Any(g => g.Value == sid.Value);
            }
            catch { return false; }
        }

        public static Device ResolveDevice(string name)
        {
            if (!Tia.IsProjectOpen) throw new InvalidOperationException("没有打开的项目");
            var devices = Tia.Project.Devices.ToList();
            if (string.IsNullOrWhiteSpace(name)) return devices.FirstOrDefault(d => Tia.GetPlcSoftware(d) != null);
            return devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("找不到设备: " + name);
        }

        public static PlcSoftware ResolvePlc(out Device device, string deviceName = null)
        {
            device = ResolveDevice(deviceName);
            if (device == null) throw new InvalidOperationException("项目里没有 PLC 设备");
            var plc = Tia.GetPlcSoftware(device);
            if (plc == null) throw new InvalidOperationException("设备不是 PLC 或没有软件: " + device.Name);
            return plc;
        }

        public static string Safe(string s) => s ?? string.Empty;

        public static JObject Snapshot()
        {
            var state = new JObject
            {
                ["portal"] = new JObject
                {
                    ["connected"] = Tia.IsPortalOpen,
                    ["projectOpen"] = Tia.IsProjectOpen,
                    ["opennessGroupInToken"] = IsOpennessGroupInToken()
                },
                ["project"] = Tia.IsProjectOpen
                    ? new JObject
                    {
                        ["name"] = Tia.Project.Name,
                        ["path"] = Tia.Project.Path?.FullName,
                        ["modified"] = Tia.Project.IsModified
                    }
                    : null,
                ["devices"] = new JArray(),
                ["sim"] = new JObject
                {
                    ["available"] = Sim.IsAvailable,
                    ["instances"] = new JArray(Sim.ListInstances().Select(i => new JObject
                    {
                        ["name"] = i.Name,
                        ["state"] = i.OperatingState,
                        ["cpu"] = i.CpuType,
                        ["ip"] = i.Ip
                    }))
                }
            };

            if (!Tia.IsProjectOpen) return state;

            var devices = new JArray();
            foreach (var device in Tia.Project.Devices)
            {
                var item = new JObject
                {
                    ["name"] = device.Name,
                    ["type"] = Safe(device.TypeIdentifier),
                    ["blocks"] = new JArray()
                };

                try
                {
                    var plc = Tia.GetPlcSoftware(device);
                    if (plc != null)
                    {
                        var blocks = new JArray();
                        foreach (var block in Tia.ListBlocks(plc).OrderBy(b => b.Number))
                        {
                            blocks.Add(new JObject
                            {
                                ["name"] = block.Name,
                                ["number"] = block.Number,
                                ["kind"] = block.GetType().Name.Replace("Block", "").Replace("Plc", ""),
                                ["language"] = block.ProgrammingLanguage.ToString(),
                                ["modified"] = block.ModifiedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                                ["consistent"] = block.IsConsistent
                            });
                        }
                        item["blocks"] = blocks;
                        item["software"] = plc.Name;
                    }
                }
                catch (Exception ex)
                {
                    item["error"] = ex.Message;
                }

                devices.Add(item);
            }

            state["devices"] = devices;
            return state;
        }
    }
}
