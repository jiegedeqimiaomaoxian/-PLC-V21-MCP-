using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Simatic.Simulation.Runtime;

namespace TiaMcp.Tia
{
    public sealed class SimInstanceInfo
    {
        public int Id;
        public string Name;
        public string OperatingState;
        public string CpuType;
        public string Ip;
    }

    /// <summary>S7-PLCSIM / PLCSIM Advanced 运行时 API 封装。</summary>
    public sealed class PlcSimBridge
    {
        public bool IsAvailable
        {
            get
            {
                try { return SimulationRuntimeManager.IsRuntimeManagerAvailable; }
                catch { return false; }
            }
        }

        public string Version
        {
            get
            {
                try { return "0x" + SimulationRuntimeManager.Version.ToString("X"); }
                catch { return "unknown"; }
            }
        }

        public List<SimInstanceInfo> ListInstances()
        {
            var list = new List<SimInstanceInfo>();
            try
            {
                foreach (var info in SimulationRuntimeManager.RegisteredInstanceInfo)
                {
                    var entry = new SimInstanceInfo { Id = info.ID, Name = info.Name };
                    try
                    {
                        var inst = SimulationRuntimeManager.CreateInterface(info.Name);
                        entry.OperatingState = inst.OperatingState.ToString();
                        entry.CpuType = inst.CPUType.ToString();
                        try
                        {
                            var octets = inst.ControllerIP ?? new string[0];
                            entry.Ip = string.Join(".", octets.Take(4).Where(o => !string.IsNullOrEmpty(o)));
                        }
                        catch { }
                    }
                    catch { }
                    list.Add(entry);
                }
            }
            catch { }
            return list;
        }

        public IInstance Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("实例名不能为空");
            return SimulationRuntimeManager.CreateInterface(name);
        }

        public IInstance Create(string name, ECPUType cpuType = ECPUType.CPU1516)
        {
            return SimulationRuntimeManager.RegisterInstance(cpuType, name);
        }

        public void Power(IInstance inst, bool on)
        {
            if (on) inst.PowerOn(60000);
            else inst.PowerOff(60000);
        }

        public void Run(IInstance inst, bool run)
        {
            if (run) inst.Run(30000);
            else inst.Stop(30000);
        }

        public void Release(IInstance inst)
        {
            try { inst.PowerOff(30000); } catch { }
            inst.UnregisterInstance();
        }

        public List<string> TagNames(IInstance inst, int limit = 500)
        {
            return inst.TagInfos.Select(t => t.Name).Take(limit).ToList();
        }

        /// <summary>按标签名读取；类型不确定时按 Bool→Int32→Float→String 依次尝试。</summary>
        public object ReadTag(IInstance inst, string tag)
        {
            var name = tag.Trim();

            var info = inst.TagInfos.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var typeHint = info.Name != null ? info.DataType.ToString().ToLowerInvariant() : "";

            if (typeHint.Contains("bool")) return inst.ReadBool(name);
            if (typeHint.Contains("real") || typeHint.Contains("float")) return inst.ReadFloat(name);
            if (typeHint.Contains("lreal") || typeHint.Contains("double")) return inst.ReadDouble(name);
            if (typeHint.Contains("string") || typeHint.Contains("wstring")) return inst.ReadString(name);
            if (typeHint.Contains("dint") || typeHint.Contains("int") || typeHint.Contains("word") || typeHint.Contains("dword"))
            {
                try { return inst.ReadInt32(name); }
                catch { return inst.ReadInt16(name); }
            }

            try { return inst.ReadBool(name); } catch { }
            try { return inst.ReadInt32(name); } catch { }
            try { return inst.ReadFloat(name); } catch { }
            try { return inst.ReadString(name); } catch { }

            return inst.Read(name);
        }

        public void WriteTag(IInstance inst, string tag, string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim();
            var isBool = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                         || value == "0" || value == "1";

            if (isBool && (value.Length == 1 || value.Length >= 4))
            {
                inst.WriteBool(tag, value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
                return;
            }

            if (int.TryParse(value, out var intValue))
            {
                inst.WriteInt32(tag, intValue);
                return;
            }

            if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue))
            {
                inst.WriteFloat(tag, floatValue);
                return;
            }

            inst.WriteString(tag, value);
        }
    }
}
