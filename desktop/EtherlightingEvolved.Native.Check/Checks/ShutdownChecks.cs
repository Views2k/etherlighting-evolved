using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ShutdownChecks
{
    // A loopback fixture with its own child. It records each request and leaves the recovery journal alone. When its
    // input closes it exits cleanly only if told to, standing in for the gateway's own cleanup after a desktop exit.
    const string Gateway = """
        import http from 'node:http';
        import fs from 'node:fs';
        import {spawn} from 'node:child_process';
        const port = Number(process.env.EE_PREVIEW_PORT);
        const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {stdio:'ignore', windowsHide:true});
        fs.writeFileSync('child.pid', String(worker.pid));
        process.stdin.on('end', () => { if (fs.existsSync('exit-on-input-close')) { worker.kill(); process.exit(0); } });
        process.stdin.resume();
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
        foreach (var scenario in new[] { "confirmed", "refused-then-cleaned", "refused-unfinished" })
        {
            var refused = scenario != "confirmed";
            var directory = Directory.CreateDirectory(Path.Combine(output, "quit-" + Guid.NewGuid().ToString("N"))).FullName;
            var script = Path.Combine(directory, "gateway.mjs");
            await File.WriteAllTextAsync(script, Gateway);
            if (scenario == "refused-then-cleaned") await File.WriteAllTextAsync(Path.Combine(directory, "exit-on-input-close"), "");
            var journal = Path.Combine(directory, "recovery.json");
            await File.WriteAllTextAsync(journal, "{\"pending\":true}");
            var receipt = Path.Combine(directory, "last-session.json");
            var host = new ServiceHost("node", script, directory, packaged: false, demo: true, directory);
            try
            {
                await host.StartAsync(CancellationToken.None);
                using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child.pid"))));
                var tray = host.StopAsync(force: false);
                var requests = Path.Combine(directory, "requests");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(requests) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                var ipc = host.StopAsync(force: false);
                var os = host.StopAsync(force: false);
                check(!tray.IsCompleted && !ipc.IsCompleted && !os.IsCompleted, $"{scenario}: concurrent close requests wait for the first one");
                await File.WriteAllTextAsync(Path.Combine(directory, "reply"), refused ? "refused" : "accepted");
                var results = await Task.WhenAll(tray, ipc, os).WaitAsync(TimeSpan.FromSeconds(10));
                check(results.All(result => result == !refused), $"{scenario}: every close request gets the same result");
                check((await File.ReadAllLinesAsync(requests)).SequenceEqual(["POST /api/workspace/close"]), $"{scenario}: one workspace close is sent for concurrent requests");
                if (!refused)
                {
                    check(!host.Running && !File.Exists(receipt), "a confirmed Quit stops the service and records nothing");
                }
                else
                {
                    check(host.Running && !File.Exists(receipt), $"{scenario}: an unconfirmed Quit from the tray keeps the service running");
                    var forced = await host.StopAsync(force: true).WaitAsync(TimeSpan.FromSeconds(60));
                    var cleaned = scenario == "refused-then-cleaned";
                    check(forced == cleaned && !host.Running, cleaned
                        ? "Quit again lets the service finish its own cleanup and exit"
                        : "Quit again ends a service whose cleanup did not finish");
                    check(cleaned
                        ? !File.Exists(receipt)
                        : JsonNode.Parse(await File.ReadAllTextAsync(receipt))?["outcome"]?.GetValue<string>() == "cleanup_unconfirmed",
                        $"{scenario}: the unconfirmed exit is recorded only when the service had to be ended");
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(await File.ReadAllTextAsync(journal) == "{\"pending\":true}", $"{scenario}: the existing recovery journal survives desktop exit");
            }
            finally { if (host.Running) host.Kill(); }
        }
    }
}
