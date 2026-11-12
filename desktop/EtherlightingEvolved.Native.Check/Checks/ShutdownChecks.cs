using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ShutdownChecks
{
    // A loopback fixture with its own child. It records each request and leaves the recovery journal alone.
    const string Gateway = """
        import http from 'node:http';
        import fs from 'node:fs';
        import {spawn} from 'node:child_process';
        const port = Number(process.env.EE_PREVIEW_PORT);
        const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {stdio:'ignore', windowsHide:true});
        fs.writeFileSync('child.pid', String(worker.pid));
        const server = http.createServer(async (req, res) => {
          const reply = (code, body) => {res.writeHead(code); res.end(JSON.stringify(body));};
          if (req.url === '/api/workspace') return reply(200, {live:false});
          fs.appendFileSync('requests', req.method+' '+req.url+'\n');
          if (req.method !== 'POST' || req.url !== '/api/workspace/close') return reply(404, {});
          while (!fs.existsSync('reply')) await new Promise(resolve => setTimeout(resolve, 10));
          if (fs.readFileSync('reply','utf8') === 'refused') return reply(409, {closed:false});
          worker.once('exit', () => {reply(200, {closed:true}); server.close();});
          worker.kill();
        });
        server.listen(port, '127.0.0.1', () => console.log('Browser preview: http://127.0.0.1:'+port));
        """;

    public static async Task Run(string output, Action<bool, string> check)
    {
        foreach (var refused in new[] { false, true })
        {
            var directory = Directory.CreateDirectory(Path.Combine(output, "quit-" + Guid.NewGuid().ToString("N"))).FullName;
            var script = Path.Combine(directory, "gateway.mjs");
            await File.WriteAllTextAsync(script, Gateway);
            var journal = Path.Combine(directory, "recovery.json");
            await File.WriteAllTextAsync(journal, "{\"pending\":true}");
            var host = new ServiceHost("node", script, directory, packaged: false, demo: true, directory);
            try
            {
                await host.StartAsync(CancellationToken.None);
                using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child.pid"))));
                var tray = host.StopAsync();
                var requests = Path.Combine(directory, "requests");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(requests) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                var ipc = host.StopAsync();
                var os = host.StopAsync();
                check(ReferenceEquals(tray, ipc) && ReferenceEquals(tray, os) && !os.IsCompleted, "concurrent desktop close requests wait for the original request");
                await File.WriteAllTextAsync(Path.Combine(directory, "reply"), refused ? "refused" : "accepted");
                var confirmed = await tray.WaitAsync(TimeSpan.FromSeconds(10));
                check(confirmed != refused && !host.Running, $"one Quit exits the local workspace after {(refused ? "refused" : "confirmed")} cleanup");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check((await File.ReadAllLinesAsync(requests)).SequenceEqual(["POST /api/workspace/close"]), "the desktop sends exactly one workspace close");
                check(await File.ReadAllTextAsync(journal) == "{\"pending\":true}", "the existing recovery journal survives desktop exit");
                var receipt = Path.Combine(directory, "last-session.json");
                check(refused
                    ? JsonNode.Parse(await File.ReadAllTextAsync(receipt))?["outcome"]?.GetValue<string>() == "cleanup_unconfirmed"
                    : !File.Exists(receipt), "the desktop records unconfirmed cleanup only in the affected workspace");
            }
            finally { if (host.Running) host.Kill(); }
        }
    }
}
