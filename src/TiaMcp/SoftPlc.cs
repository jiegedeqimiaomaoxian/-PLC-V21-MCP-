using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TiaMcp
{
    /// <summary>
    /// 监视台内置的"软 PLC"：20ms 扫描周期跑经典启保停逻辑。
    /// 用它把"程序在跑、变量在变"的过程可视化出来；等 Openness 可用后，
    /// 同一段逻辑用 samples\MotorCtrl.scl 下发到真 PLC，监视台换成读 PLCSIM 变量即可。
    /// </summary>
    public sealed class SoftPlc : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Dictionary<string, DateTime> _releaseAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private Thread _thread;
        private volatile bool _running;
        private long _scans;
        private double _runSeconds;

        // 输入（按钮，点动）
        public bool Start_Btn;
        public bool Stop_Btn;
        public bool Fault_Btn;
        public bool Reset_Btn;

        // 输出
        public bool Run;
        public bool Motor_Out;
        public bool Fault_Lamp;

        // 内部
        public bool SelfHold;

        private readonly List<JObject> _events = new List<JObject>();

        public void Start()
        {
            if (_running) return;
            _clock.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "soft-plc" };
            _thread.Start();
            AddEvent("软 PLC 启动，扫描周期 20 ms");
        }

        private void Loop()
        {
            while (_running)
            {
                try { Scan(); }
                catch (Exception ex) { Mcp.McpServer.Log("soft-plc: " + ex.Message); }
                Thread.Sleep(20);
            }
        }

        private void Scan()
        {
            var now = DateTime.Now;
            lock (_gate)
            {
                // 按钮自动复位（模拟点动）
                foreach (var key in new[] { "Start", "Stop", "Fault", "Reset" })
                {
                    if (!_releaseAt.TryGetValue(key, out var release)) continue;
                    if (now < release) continue;

                    _releaseAt.Remove(key);
                    switch (key)
                    {
                        case "Start": Start_Btn = false; break;
                        case "Stop": Stop_Btn = false; break;
                        case "Fault": Fault_Btn = false; break;
                        case "Reset": Reset_Btn = false; break;
                    }
                }

                // 1) 故障锁存 / 复位
                if (Fault_Btn && !Fault_Lamp)
                {
                    Fault_Lamp = true;
                    AddEvent("故障信号 → 故障锁存置位");
                }
                if (Reset_Btn && Fault_Lamp)
                {
                    Fault_Lamp = false;
                    AddEvent("复位 → 故障锁存清除");
                }

                // 2) 启保停本体：(Start_Btn ∥ Run) ∧ ¬Stop_Btn ∧ ¬Fault_Lamp
                var nextRun = (Start_Btn || SelfHold) && !Stop_Btn && !Fault_Lamp;

                if (nextRun != Run)
                {
                    Run = nextRun;
                    AddEvent(nextRun ? "启动：自锁形成，输出 Run = 1" : (Stop_Btn ? "停止：Stop_Btn 断开，Run = 0" : "Run = 0"));
                }

                SelfHold = Run;
                Motor_Out = Run;

                _scans++;
                if (Run) _runSeconds += 0.02;
            }
        }

        public void Press(string input, int holdMs = 220)
        {
            lock (_gate)
            {
                switch (input.ToLowerInvariant())
                {
                    case "start":
                        Start_Btn = true;
                        AddEvent("按下 Start_Btn");
                        break;
                    case "stop":
                        Stop_Btn = true;
                        AddEvent("按下 Stop_Btn");
                        break;
                    case "fault":
                        Fault_Btn = true;
                        AddEvent("按下 Fault_Btn");
                        break;
                    case "reset":
                        Reset_Btn = true;
                        AddEvent("按下 Reset_Btn");
                        break;
                    default:
                        throw new ArgumentException("未知输入: " + input + "（可用 start/stop/fault/reset）");
                }

                _releaseAt[input] = DateTime.Now.AddMilliseconds(holdMs);
            }
        }

        public void ClearFault()
        {
            lock (_gate)
            {
                Fault_Lamp = false;
                AddEvent("复位 → 故障锁存清除");
            }
        }

        private void AddEvent(string message)
        {
            _events.Add(new JObject
            {
                ["t"] = DateTime.Now.ToString("HH:mm:ss.fff"),
                ["msg"] = message
            });

            while (_events.Count > 60) _events.RemoveAt(0);
        }

        public JObject Snapshot()
        {
            lock (_gate)
            {
                return new JObject
                {
                    ["vars"] = new JObject
                    {
                        ["Start_Btn"] = Start_Btn,
                        ["Stop_Btn"] = Stop_Btn,
                        ["Fault_Btn"] = Fault_Btn,
                        ["Reset_Btn"] = Reset_Btn,
                        ["Run"] = Run,
                        ["SelfHold"] = SelfHold,
                        ["Motor_Out"] = Motor_Out,
                        ["Fault_Lamp"] = Fault_Lamp
                    },
                    ["scans"] = _scans,
                    ["runSeconds"] = Math.Round(_runSeconds, 1),
                    ["events"] = new JArray(_events)
                };
            }
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
