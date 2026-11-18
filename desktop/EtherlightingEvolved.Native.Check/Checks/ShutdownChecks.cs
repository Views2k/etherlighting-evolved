using System.Diagnostics;
using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ShutdownChecks
{
    // A loopback fixture with its own child. It records each request, noting whether it carried the administration
    // key it was started with, and leaves the recovery journal alone. It records the lines it reads, and on "exit"
    // ends its child and exits only if told to, standing in for the gateway closing without another release.
    const string Gateway = """
        import http from 'node:http';
        import fs from 'node:fs';
        import {spawn} from 'node:child_process';
        const port = Number(process.env.EE_PREVIEW_PORT);
        const key = process.env.EE_ADMIN_KEY ?? '';
        fs.writeFileSync('key', key);
        const worker = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], {stdio:'ignore', windowsHide:true});
        fs.writeFileSync('child.pid', String(worker.pid));
        process.stdin.on('data', chunk => {
          fs.appendFileSync('input', String(chunk));
          if (String(chunk).includes('exit') && fs.existsSync('exit-on-request')) { worker.kill(); process.exit(1); }
        });
        process.stdin.resume();
        const server = http.createServer(async (req, res) => {
          const reply = (code, body) => {res.writeHead(code); res.end(JSON.stringify(body));};
          if (req.url === '/api/workspace') return reply(200, {live:false});
          fs.appendFileSync('requests', req.method+' '+req.url+(key && req.headers['x-ee-admin-key'] === key ? ' key' : ' no-key')+'\n');
          if (req.method !== 'POST' || req.url !== '/api/workspace/close') return reply(404, {});
          while (!fs.existsSync('reply')) await new Promise(resolve => setTimeout(resolve, 10));
          if (fs.readFileSync('reply','utf8') === 'refused') return reply(409, {closed:false});
          worker.once('exit', () => {reply(200, {closed:true}); server.close();});
          worker.kill();
        });
        server.listen(port, '127.0.0.1', () => console.log('Browser preview: http://127.0.0.1:'+port));
        """;

    // A fixture that fails before it is ready, or exits on its own after it started, writing to its error output first.
    const string Failing = """
        import http from 'node:http';
        import fs from 'node:fs';
        const port = Number(process.env.EE_PREVIEW_PORT);
        if (fs.existsSync('fail-start')) { console.error('Startup failed at https://user:secret@example.test/'); process.exit(4); }
        http.createServer((req, res) => { res.writeHead(200); res.end(JSON.stringify({live:false})); })
          .listen(port, '127.0.0.1', () => console.log('Browser preview: http://127.0.0.1:'+port));
        setInterval(() => { if (fs.existsSync('stop')) { console.error('The engine exited.'); process.exit(3); } }, 20);
        """;

    public static async Task Run(string output, Action<bool, string> check)
    {
        await UnexpectedExit(output, check);
        var keys = new List<string>();
        foreach (var scenario in new[] { "confirmed", "refused-then-exited", "refused-unresponsive" })
        {
            var refused = scenario != "confirmed";
            var directory = Directory.CreateDirectory(Path.Combine(output, "quit-" + Guid.NewGuid().ToString("N"))).FullName;
            var script = Path.Combine(directory, "gateway.mjs");
            await File.WriteAllTextAsync(script, Gateway);
            if (scenario == "refused-then-exited") await File.WriteAllTextAsync(Path.Combine(directory, "exit-on-request"), "");
            var journal = Path.Combine(directory, "recovery.json");
            await File.WriteAllTextAsync(journal, "{\"pending\":true}");
            var receipt = Path.Combine(directory, "last-session.json");
            var host = new ServiceHost("node", script, directory, packaged: false, demo: true, directory);
            var stops = 0;
            host.Stopped += _ => Interlocked.Increment(ref stops);
            try
            {
                await host.StartAsync(CancellationToken.None);
                var key = await File.ReadAllTextAsync(Path.Combine(directory, "key"));
                keys.Add(key);
                check(key == host.AdminKey && key.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                    $"{scenario}: the service receives this launch's administration key");
                using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child.pid"))));
                var tray = host.StopAsync();
                var requests = Path.Combine(directory, "requests");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(requests) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                var ipc = host.StopAsync();
                var os = host.StopAsync();
                check(!tray.IsCompleted && !ipc.IsCompleted && !os.IsCompleted, $"{scenario}: concurrent close requests wait for the first one");
                await File.WriteAllTextAsync(Path.Combine(directory, "reply"), refused ? "refused" : "accepted");
                var ending = Stopwatch.StartNew();
                var results = await Task.WhenAll(tray, ipc, os).WaitAsync(TimeSpan.FromSeconds(30));
                check(results.All(result => result == !refused), $"{scenario}: every close request gets the same result");
                check((await File.ReadAllLinesAsync(requests)).SequenceEqual(["POST /api/workspace/close key"]),
                    $"{scenario}: one workspace close, with the administration key, is sent for concurrent requests");
                check(!host.Running, $"{scenario}: Quit stops the service");
                var input = Path.Combine(directory, "input");
                if (!refused)
                {
                    check(!File.Exists(receipt) && !File.Exists(input), "a confirmed Quit sends no exit request and records nothing");
                }
                else
                {
                    check(File.Exists(input) && File.ReadAllText(input).Contains("exit"),
                        $"{scenario}: after a refused close the service is told to exit without another attempt");
                    check(scenario == "refused-then-exited" ? ending.Elapsed < TimeSpan.FromSeconds(10) : ending.Elapsed >= TimeSpan.FromSeconds(14),
                        scenario == "refused-then-exited"
                            ? "a service that exits on request is not waited on"
                            : "a service that ignores the exit request is ended after its time is up");
                    check(JsonNode.Parse(await File.ReadAllTextAsync(receipt))?["outcome"]?.GetValue<string>() == "cleanup_unconfirmed",
                        $"{scenario}: the unconfirmed exit is recorded so the next start recovers the lighting");
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(await File.ReadAllTextAsync(journal) == "{\"pending\":true}", $"{scenario}: the existing recovery journal survives desktop exit");
                check(stops == 0, $"{scenario}: a requested stop is not reported as a stopped service");
            }
            finally { if (host.Running) host.Kill(); }
        }
        check(keys.Count == 3 && keys.Distinct().Count() == 3, "each service launch gets its own administration key");
    }

    static async Task UnexpectedExit(string output, Action<bool, string> check)
    {
        var directory = Directory.CreateDirectory(Path.Combine(output, "stop-" + Guid.NewGuid().ToString("N"))).FullName;
        var script = Path.Combine(directory, "gateway.mjs");
        await File.WriteAllTextAsync(script, Failing);
        var host = new ServiceHost("node", script, directory, packaged: false, demo: true, directory);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Stopped += code => stopped.TrySetResult(code);
        try
        {
            await host.StartAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "stop"), "");
            var code = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(code == 3 && !host.Running, "a service that exits on its own after starting is reported");
            check(LastEntry("service_stopped") is { } entry && entry.Int("exit_code") == 3 && entry.Arr("output").Any(line => line.Str() == "The engine exited."),
                "desktop.log records the stopped service's exit code and last output");
        }
        finally { if (host.Running) host.Kill(); }

        await File.WriteAllTextAsync(Path.Combine(directory, "fail-start"), "");
        var failing = new ServiceHost("node", script, directory, packaged: false, demo: true, directory);
        var failed = false;
        try { await failing.StartAsync(CancellationToken.None); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && LastEntry("service_start_failed") is { } start && start.Int("exit_code") == 4
              && start.Arr("output").Any(line => line.Str() == "Startup failed at https://example.test/"),
            "desktop.log records why the service did not start, without address credentials");

        var missing = new ServiceHost(Path.Combine(directory, "missing-node"), script, directory, packaged: false, demo: true, directory);
        var refused = false;
        try { await missing.StartAsync(CancellationToken.None); }
        catch (System.ComponentModel.Win32Exception) { refused = true; }
        check(refused && !missing.Running && LastEntry("service_start_failed")?.Str("errors.0.type") == typeof(System.ComponentModel.Win32Exception).FullName,
            "desktop.log records a service that could not be launched at all");
    }

    static JsonNode? LastEntry(string kind) =>
        File.ReadLines(DesktopLog.File).Select(line => JsonNode.Parse(line)).LastOrDefault(entry => entry.Str("event") == kind);
}
