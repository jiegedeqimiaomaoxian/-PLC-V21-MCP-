using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Web
{
    /// <summary>本机仪表盘：http://127.0.0.1:5199/ 看图 / 看变量 / 看启保停在跑。</summary>
    public sealed class Dashboard : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly int _port;
        private Thread _thread;
        private volatile bool _running;

        public Dashboard(int port)
        {
            _port = port;
        }

        public string Url => "http://127.0.0.1:" + _port + "/";

        public void Start()
        {
            _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
            _listener.Start();
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "dashboard" };
            _thread.Start();
            Mcp.McpServer.Log("dashboard: " + Url);
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try { WriteText(ctx, 500, "error: " + ex.Message); } catch { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            if (path.Length == 0) path = "/";

            switch (path)
            {
                case "/":
                    WriteText(ctx, 200, Html, "text/html; charset=utf-8");
                    break;

                case "/api/state":
                    WriteText(ctx, 200, App.Snapshot().ToString(Formatting.None), "application/json; charset=utf-8");
                    break;

                case "/api/latch":
                    WriteText(ctx, 200, App.Plc.Snapshot().ToString(Formatting.None), "application/json; charset=utf-8");
                    break;

                case "/api/latch/press":
                    var input = ctx.Request.QueryString["input"] ?? "start";
                    int.TryParse(ctx.Request.QueryString["ms"], out var holdMs);
                    App.Plc.Press(input, holdMs > 0 ? holdMs : 220);
                    WriteText(ctx, 200, "pressed " + input);
                    break;

                case "/api/latch/clear":
                    App.Plc.ClearFault();
                    WriteText(ctx, 200, "cleared");
                    break;

                case "/api/screenshot.png":
                    var png = Capture.CapturePortalPng();
                    if (png == null) WriteText(ctx, 404, "no TIA Portal window (headless mode?)");
                    else
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "image/png";
                        ctx.Response.ContentLength64 = png.Length;
                        ctx.Response.OutputStream.Write(png, 0, png.Length);
                        ctx.Response.Close();
                    }
                    break;

                case "/api/show-block":
                    var blockName = ctx.Request.QueryString["name"];
                    var info = App.Dispatcher.Invoke(() =>
                    {
                        var plc = App.ResolvePlc(out var device);
                        App.Tia.ShowBlock(plc, blockName);
                        return "opened " + blockName;
                    });
                    WriteText(ctx, 200, info);
                    break;

                default:
                    WriteText(ctx, 404, "not found");
                    break;
            }
        }

        private static void WriteText(HttpListenerContext ctx, int status, string body, string contentType = "text/plain; charset=utf-8")
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        /// <summary>给 MCP 工具用的截图入口。</summary>
        public static byte[] CapturePortalPngProxy() => Capture.CapturePortalPng();

        private const string Html = @"<!DOCTYPE html>
<html lang='zh-CN'><head><meta charset='utf-8'><title>TIA Portal 监视台</title>
<style>
:root{--bg:#0f1115;--panel:#161a21;--line:#242a35;--fg:#d7dce5;--dim:#7d8797;--ok:#3ddc97;--bad:#ff5d5d;--acc:#4da3ff;--warn:#ffc857}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:13px/1.5 'Segoe UI',system-ui,sans-serif}
header{display:flex;align-items:center;gap:14px;padding:10px 16px;border-bottom:1px solid var(--line);background:var(--panel)}
h1{font-size:15px;margin:0;font-weight:600}
.badge{padding:2px 8px;border-radius:10px;background:#1e2530;color:var(--dim);font-size:12px}
.badge.on{background:rgba(61,220,151,.15);color:var(--ok)}
.badge.off{background:rgba(255,93,93,.15);color:var(--bad)}
main{display:grid;grid-template-columns:250px 1fr 300px;gap:1px;background:var(--line);height:calc(100vh - 45px)}
section{background:var(--bg);overflow:auto;padding:10px}
h2{font-size:12px;text-transform:uppercase;letter-spacing:.08em;color:var(--dim);margin:0 0 8px}
.dev{margin-bottom:10px}.dev b{color:var(--acc)}
.block{padding:3px 6px;border-radius:4px;cursor:pointer;display:flex;justify-content:space-between}
.block:hover{background:#1c2230}
.block .k{color:var(--dim);font-size:11px}
#shot{width:100%;border:1px solid var(--line);border-radius:4px;background:#000;min-height:120px}
.tags{display:flex;flex-direction:column;gap:2px;font-family:Consolas,monospace;font-size:12px}
.tag{display:flex;justify-content:space-between;gap:8px;padding:2px 6px;border-radius:3px;background:#12161d}
.tag .v{color:var(--ok)}
.tabs{display:flex;gap:6px;margin-bottom:10px}
.tab{background:#1a1f28;border:1px solid var(--line);color:var(--dim);padding:5px 12px;border-radius:5px;cursor:pointer;font-size:12px}
.tab.active{background:#22304a;color:#cfe4ff;border-color:#33507d}
.hidden{display:none}
.ladder{background:#0b0e13;border:1px solid var(--line);border-radius:6px;padding:6px}
svg text{font-family:Consolas,monospace;font-size:11px;fill:#7d8797}
svg .lbl{fill:#9fb0c4}
.contact .bar{stroke:#3a4452;stroke-width:3}
.contact .wire{stroke:#3a4452;stroke-width:3}
.contact.on .bar{stroke:var(--ok)}
.contact.on .wire{stroke:var(--ok)}
.coil circle{stroke:#3a4452;stroke-width:3;fill:none}
.coil.on circle{stroke:var(--ok);fill:rgba(61,220,151,.25)}
.coil .w{stroke:#3a4452;stroke-width:3}
.coil.on .w{stroke:var(--ok)}
.btn{background:#1d2431;color:#cfe4ff;border:1px solid #2c3a52;padding:6px 14px;border-radius:6px;cursor:pointer;font-size:12px;margin:0 6px 6px 0}
.btn:hover{background:#25314a}
.btn.go{border-color:#2f6b4d;color:#9ff0c4}
.btn.stop{border-color:#6b2f2f;color:#ffb3b3}
.btn.demo{border-color:#4a3f7a;color:#cdbcff}
.chips{display:flex;gap:8px;margin:8px 0;flex-wrap:wrap}
.chip{padding:3px 10px;border-radius:12px;background:#1a1f28;color:var(--dim);font-size:12px;border:1px solid var(--line)}
.chip.on{background:rgba(61,220,151,.15);color:var(--ok);border-color:#2f6b4d}
.chip.fault.on{background:rgba(255,93,93,.15);color:var(--bad);border-color:#6b2f2f}
.log{font-family:Consolas,monospace;font-size:11px;color:#8a97a8;max-height:170px;overflow:auto;background:#0b0e13;border:1px solid var(--line);border-radius:6px;padding:6px}
.log div{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.log .t{color:#5d6775;margin-right:6px}
</style></head>
<body>
<header>
  <h1>TIA Portal 监视台</h1>
  <span class='badge' id='b-portal'>Portal</span>
  <span class='badge' id='b-group'>Openness 组</span>
  <span class='badge' id='b-sim'>仿真</span>
  <span class='badge' id='b-proj'>项目</span>
  <span class='badge' style='margin-left:auto'>刷新 2s / 梯形图 120ms</span>
</header>
<main>
  <section><h2>设备 / 程序块</h2><div id='tree'>连接中…</div></section>

  <section>
    <div class='tabs'>
      <button class='tab' id='tab-shot'>博图画面</button>
      <button class='tab active' id='tab-ladder'>启保停演示</button>
    </div>

    <div id='pane-shot' class='hidden'>
      <h2>博图窗口 <span id='shotinfo' style='color:var(--dim);font-weight:400'></span></h2>
      <img id='shot' src=''>
    </div>

    <div id='pane-ladder'>
      <h2>启保停 · 软 PLC 实时运行（扫描周期 20ms）</h2>

      <div class='ladder'>
        <svg viewBox='0 0 660 300' width='100%' height='300'>
          <line x1='20' y1='30' x2='20' y2='270' stroke='#3a4452' stroke-width='3'/>
          <line x1='620' y1='30' x2='620' y2='270' stroke='#3a4452' stroke-width='3'/>

          <g id='c-start' class='contact'>
            <line class='wire' x1='20' y1='70' x2='72' y2='70'/>
            <line class='bar' x1='78' y1='54' x2='78' y2='86'/>
            <line class='bar' x1='92' y1='54' x2='92' y2='86'/>
            <text class='lbl' x='66' y='44'>Start_Btn</text>
          </g>
          <g id='c-run1' class='contact'>
            <line class='wire' x1='72' y1='110' x2='78' y2='110'/>
            <line class='bar' x1='78' y1='94' x2='78' y2='126'/>
            <line class='bar' x1='92' y1='94' x2='92' y2='126'/>
            <line class='wire' x1='92' y1='110' x2='140' y2='110'/>
            <text class='lbl' x='66' y='88'>Run（自锁）</text>
          </g>
          <line x1='72' y1='70' x2='72' y2='110' stroke='#3a4452' stroke-width='3'/>
          <line x1='140' y1='110' x2='140' y2='70' stroke='#3a4452' stroke-width='3'/>
          <line x1='100' y1='70' x2='140' y2='70' stroke='#3a4452' stroke-width='3'/>

          <g id='c-stop' class='contact'>
            <line class='wire' x1='140' y1='70' x2='228' y2='70'/>
            <line class='bar' x1='234' y1='54' x2='234' y2='86'/>
            <line class='bar' x1='248' y1='54' x2='248' y2='86'/>
            <line class='wire' x1='248' y1='70' x2='256' y2='86'/>
            <text class='lbl' x='220' y='44'>Stop_Btn</text>
          </g>

          <g id='c-faultnc' class='contact'>
            <line class='wire' x1='256' y1='70' x2='344' y2='70'/>
            <line class='bar' x1='350' y1='54' x2='350' y2='86'/>
            <line class='bar' x1='364' y1='54' x2='364' y2='86'/>
            <line class='wire' x1='364' y1='70' x2='372' y2='86'/>
            <text class='lbl' x='324' y='44'>Fault_Lamp</text>
          </g>

          <g id='coil-run' class='coil'>
            <line class='w' x1='372' y1='70' x2='508' y2='70'/>
            <circle cx='528' cy='70' r='17'/>
            <line class='w' x1='548' y1='70' x2='620' y2='70'/>
            <text class='lbl' x='512' y='44'>Run</text>
          </g>

          <g id='c-run2' class='contact'>
            <line class='wire' x1='20' y1='170' x2='72' y2='170'/>
            <line class='bar' x1='78' y1='154' x2='78' y2='186'/>
            <line class='bar' x1='92' y1='154' x2='92' y2='186'/>
            <line class='wire' x1='92' y1='170' x2='508' y2='170'/>
            <text class='lbl' x='66' y='144'>Run</text>
          </g>
          <g id='coil-motor' class='coil'>
            <circle cx='528' cy='170' r='17'/>
            <line class='w' x1='548' y1='170' x2='620' y2='170'/>
            <text class='lbl' x='486' y='144'>Motor_Out</text>
          </g>

          <g id='c-faultbtn' class='contact'>
            <line class='wire' x1='20' y1='240' x2='72' y2='240'/>
            <line class='bar' x1='78' y1='224' x2='78' y2='256'/>
            <line class='bar' x1='92' y1='224' x2='92' y2='256'/>
            <line class='wire' x1='92' y1='240' x2='228' y2='240'/>
            <text class='lbl' x='66' y='214'>Fault_Btn</text>
          </g>
          <g id='c-reset' class='contact'>
            <line class='bar' x1='234' y1='224' x2='234' y2='256'/>
            <line class='bar' x1='248' y1='224' x2='248' y2='256'/>
            <line class='wire' x1='248' y1='240' x2='256' y2='256'/>
            <line class='wire' x1='256' y1='240' x2='508' y2='240'/>
            <text class='lbl' x='222' y='214'>Reset_Btn</text>
          </g>
          <g id='coil-fault' class='coil'>
            <circle cx='528' cy='240' r='17'/>
            <line class='w' x1='548' y1='240' x2='620' y2='240'/>
            <text class='lbl' x='486' y='214'>Fault_Lamp</text>
          </g>
        </svg>
      </div>

      <div class='chips'>
        <span class='chip' id='chip-run'>Run</span>
        <span class='chip' id='chip-motor'>Motor_Out</span>
        <span class='chip fault' id='chip-fault'>Fault_Lamp</span>
        <span class='chip' id='chip-scans'>扫描 0</span>
        <span class='chip' id='chip-time'>运行 0.0 s</span>
      </div>

      <div>
        <button class='btn go' onclick=""press('start')"">启动 Start</button>
        <button class='btn stop' onclick=""press('stop')"">停止 Stop</button>
        <button class='btn' onclick=""press('fault')"">故障 Fault</button>
        <button class='btn' onclick=""press('reset')"">复位 Reset</button>
        <button class='btn demo' onclick=""autoDemo()"">自动演示</button>
      </div>

      <h2 style='margin-top:12px'>事件</h2>
      <div class='log' id='latchlog'></div>
    </div>
  </section>

  <section><h2>仿真实例</h2><div id='sims'></div></section>
</main>
<script>
let state = null;
function badge(id, ok, text){ const el=document.getElementById(id); el.className='badge '+(ok?'on':'off'); el.textContent=text; }
async function refresh(){
  try{
    const r = await fetch('/api/state',{cache:'no-store'});
    state = await r.json();
    badge('b-portal', state.portal.connected, 'Portal ' + (state.portal.connected?'已连':'未连'));
    badge('b-group', state.portal.opennessGroupInToken, 'Openness 组 ' + (state.portal.opennessGroupInToken?'OK':'缺失'));
    badge('b-sim', state.sim.available, '仿真 ' + (state.sim.available?'可用':'不可用'));
    badge('b-proj', !!state.project, state.project ? state.project.name : '无项目');
    renderTree(); renderSims();
  }catch(e){ console.error(e); }
}
function renderTree(){
  const root=document.getElementById('tree'); root.innerHTML='';
  if(!state || !state.devices.length){ root.textContent='（未打开项目）'; return; }
  for(const d of state.devices){
    const div=document.createElement('div'); div.className='dev';
    div.innerHTML='<b>'+d.name+'</b> <span style=""color:#7d8797;font-size:11px"">'+d.type+'</span>';
    for(const b of d.blocks){
      const el=document.createElement('div'); el.className='block';
      el.innerHTML='<span>'+b.name+'</span><span class=""k"">'+b.kind+' '+b.number+' · '+b.language+'</span>';
      el.onclick=async()=>{ await fetch('/api/show-block?name='+encodeURIComponent(b.name)); setTimeout(loadShot,800); };
      div.appendChild(el);
    }
    root.appendChild(div);
  }
}
function renderSims(){
  const el=document.getElementById('sims');
  if(!state || !state.sim.instances.length){ el.textContent='（没有运行中的仿真实例）'; return; }
  el.innerHTML = state.sim.instances.map(i=>'<div class=""tag""><span>'+i.name+'</span><span class=""v"">'+i.state+'</span></div>').join('');
}
function loadShot(){
  if(document.getElementById('pane-shot').classList.contains('hidden')) return;
  const img=document.getElementById('shot');
  img.src='/api/screenshot.png?t='+Date.now();
  img.onload=()=>document.getElementById('shotinfo').textContent='('+new Date().toLocaleTimeString()+')';
  img.onerror=()=>document.getElementById('shotinfo').textContent='(博图窗口不可见)';
}
function setOn(id,on){ const el=document.getElementById(id); if(el) el.classList.toggle('on', !!on); }
function chip(id,on,txt){ const el=document.getElementById(id); el.classList.toggle('on', !!on); if(txt) el.textContent=txt; }
async function latchRefresh(){
  if(document.getElementById('pane-ladder').classList.contains('hidden')) return;
  try{
    const s = await (await fetch('/api/latch',{cache:'no-store'})).json();
    const v = s.vars;
    setOn('c-start', v.Start_Btn); setOn('c-run1', v.SelfHold);
    setOn('c-stop', !v.Stop_Btn); setOn('c-faultnc', !v.Fault_Lamp);
    setOn('coil-run', v.Run); setOn('c-run2', v.Run); setOn('coil-motor', v.Motor_Out);
    setOn('c-faultbtn', v.Fault_Btn); setOn('c-reset', v.Reset_Btn); setOn('coil-fault', v.Fault_Lamp);
    chip('chip-run', v.Run, 'Run ' + (v.Run?'1':'0'));
    chip('chip-motor', v.Motor_Out, 'Motor_Out ' + (v.Motor_Out?'1':'0'));
    chip('chip-fault', v.Fault_Lamp, 'Fault_Lamp ' + (v.Fault_Lamp?'1':'0'));
    chip('chip-scans', true, '扫描 ' + s.scans);
    chip('chip-time', s.runSeconds>0, '运行 ' + s.runSeconds.toFixed(1) + ' s');
    document.getElementById('latchlog').innerHTML = s.events.slice(-9).reverse().map(e=>'<div><span class=""t"">'+e.t+'</span>'+e.msg+'</div>').join('');
  }catch(e){}
}
async function press(name){ await fetch('/api/latch/press?input='+name,{method:'POST'}); latchRefresh(); }
async function autoDemo(){
  await press('start'); await new Promise(r=>setTimeout(r,1600));
  await press('stop');  await new Promise(r=>setTimeout(r,1200));
  await press('start'); await new Promise(r=>setTimeout(r,1200));
  await press('fault'); await new Promise(r=>setTimeout(r,1200));
  await press('reset');
}
document.getElementById('tab-shot').onclick=()=>{
  document.getElementById('pane-shot').classList.remove('hidden');
  document.getElementById('pane-ladder').classList.add('hidden');
  document.getElementById('tab-shot').classList.add('active');
  document.getElementById('tab-ladder').classList.remove('active');
  loadShot();
};
document.getElementById('tab-ladder').onclick=()=>{
  document.getElementById('pane-ladder').classList.remove('hidden');
  document.getElementById('pane-shot').classList.add('hidden');
  document.getElementById('tab-ladder').classList.add('active');
  document.getElementById('tab-shot').classList.remove('active');
  latchRefresh();
};
refresh(); loadShot(); latchRefresh();
setInterval(refresh,2000); setInterval(loadShot,3000); setInterval(latchRefresh,120);
</script></body></html>";
    }

    /// <summary>Win32 抓 TIA Portal 主窗口。</summary>
    internal static class Capture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public static byte[] CapturePortalPng()
        {
            foreach (var process in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !IsWindowVisible(handle)) continue;

                    if (!GetWindowRect(handle, out var rect)) continue;
                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width <= 0 || height <= 0) continue;

                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        var hdc = graphics.GetHdc();
                        try { PrintWindow(handle, hdc, 2); }
                        finally { graphics.ReleaseHdc(hdc); }

                        using (var ms = new MemoryStream())
                        {
                            bitmap.Save(ms, ImageFormat.Png);
                            return ms.ToArray();
                        }
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
