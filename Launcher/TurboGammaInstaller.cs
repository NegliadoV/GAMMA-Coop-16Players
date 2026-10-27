using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GammaZoneLauncher
{
    public class GammaModEntry
    {
        public int Counter { get; set; }
        public bool IsSeparator { get; set; }
        public bool IsModDb { get; set; }
        public bool IsGitHub { get; set; }
        public string DlLink { get; set; } = "";
        public string Instructions { get; set; } = "0";
        public string Patch { get; set; } = "";
        public string AddonName { get; set; } = "";
        public string ModDbUrl { get; set; } = "";
        public string ArchiveFileName { get; set; } = "";
        public string ExpectedMd5 { get; set; } = "";

        public string ModFolderName => IsSeparator
            ? $"{Counter}-{DlLink}_separator"
            : $"{Counter}-{AddonName}{Patch}";
    }

    public class TurboInstallProgress
    {
        public string Phase { get; set; } = "Подготовка...";
        public int DownloadedArchives { get; set; }
        public int TotalArchives { get; set; }
        public int InstalledMods { get; set; }
        public int TotalMods { get; set; }
        public int FailedArchives { get; set; }
        public double SpeedMbps { get; set; }
        public string CurrentItem { get; set; } = "";
    }

    /// <summary>
    /// Resolves ModDB /addons/start/{id} links into direct dbolical.com CDN URLs
    /// using a persistent Chrome DevTools Protocol (CDP) session that bypasses Cloudflare Turnstile
    /// and intercepts the 302 Location header on /downloads/mirror/* without downloading in Chrome.
    /// </summary>
    public sealed class ModDbCdpResolver : IDisposable
    {
        private readonly string _profileDir;
        private readonly int _debugPort;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private ClientWebSocket _ws;
        private CancellationTokenSource _rxCts;
        private int _cmdId = 1;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
        private readonly ConcurrentDictionary<string, string> _mirrorLocations = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] Mirrors = { null, "135", "122", "131", "130", "134", "136" };

        public ModDbCdpResolver(string profileDir, Action<string> log = null, int debugPort = 9333)
        {
            _profileDir = profileDir;
            _log = log;
            _debugPort = debugPort;
        }

        public async Task EnsureConnectedAsync(CancellationToken ct)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                await EnsureConnectedInternalAsync(ct);
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task EnsureConnectedInternalAsync(CancellationToken ct)
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
                return;

            CleanupSocket();

            string wsUrl = await TryGetModDbPageWsUrlAsync(ct);
            if (string.IsNullOrEmpty(wsUrl))
            {
                LaunchChromeBrowser();
                for (int i = 0; i < 25; i++)
                {
                    await Task.Delay(500, ct);
                    wsUrl = await TryGetModDbPageWsUrlAsync(ct);
                    if (!string.IsNullOrEmpty(wsUrl))
                        break;
                }
            }

            if (string.IsNullOrEmpty(wsUrl))
                throw new InvalidOperationException("Не удалось подключиться к Chrome CDP на порту " + _debugPort);

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wsUrl), ct);
            _rxCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_ws, _rxCts.Token));

            await SendCdpCommandAsync("Page.enable", new { }, ct);
            await SendCdpCommandAsync("Runtime.enable", new { }, ct);
            await SendCdpCommandAsync("Fetch.enable", new
            {
                patterns = new[]
                {
                    new { urlPattern = "*://www.moddb.com/downloads/mirror/*", requestStage = "Response" }
                }
            }, ct);

            await EnsureCloudflarePassedInternalAsync(ct);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return;
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    if (string.IsNullOrEmpty(json)) continue;

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement.Clone();

                    if (root.TryGetProperty("method", out var mProp) && mProp.GetString() == "Fetch.requestPaused")
                    {
                        var prms = root.GetProperty("params");
                        string reqId = prms.GetProperty("requestId").GetString();
                        string reqUrl = prms.TryGetProperty("request", out var reqObj) && reqObj.TryGetProperty("url", out var uProp)
                            ? uProp.GetString() ?? ""
                            : "";

                        if (prms.TryGetProperty("responseHeaders", out var headers))
                        {
                            foreach (var h in headers.EnumerateArray())
                            {
                                string hName = h.GetProperty("name").GetString() ?? "";
                                if (hName.Equals("location", StringComparison.OrdinalIgnoreCase))
                                {
                                    string loc = h.GetProperty("value").GetString();
                                    if (!string.IsNullOrEmpty(reqUrl) && !string.IsNullOrEmpty(loc))
                                    {
                                        _mirrorLocations[reqUrl] = loc;
                                    }
                                    break;
                                }
                            }
                        }

                        // Immediately abort the mirror redirect so Chrome does not download the file
                        _ = SendFireAndForgetAsync("Fetch.failRequest", new { requestId = reqId, errorReason = "Aborted" });
                    }
                    else if (root.TryGetProperty("id", out var idProp))
                    {
                        int id = idProp.GetInt32();
                        if (_pendingCommands.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(root);
                        }
                    }
                }
            }
            catch
            {
                foreach (var kv in _pendingCommands)
                    kv.Value.TrySetCanceled();
                _pendingCommands.Clear();
            }
        }

        private async Task<string> TryGetModDbPageWsUrlAsync(CancellationToken ct)
        {
            try
            {
                string json = await _http.GetStringAsync($"http://127.0.0.1:{_debugPort}/json/list", ct);
                using var doc = JsonDocument.Parse(json);
                string fallbackWs = null;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string type = el.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type != "page") continue;
                    string url = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    string ws = el.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
                    if (string.IsNullOrEmpty(ws)) continue;
                    if (url.Contains("moddb.com", StringComparison.OrdinalIgnoreCase))
                        return ws;
                    fallbackWs ??= ws;
                }
                return fallbackWs;
            }
            catch
            {
                return null;
            }
        }

        private void LaunchChromeBrowser()
        {
            Directory.CreateDirectory(_profileDir);
            string[] candidates =
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };
            string exe = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(exe))
                throw new FileNotFoundException("Не найден браузер Google Chrome или Microsoft Edge для обхода Cloudflare ModDB.");

            _log?.Invoke($"[CDP] Запуск браузера для обхода Cloudflare ModDB ({Path.GetFileName(exe)}, порт {_debugPort})...");
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--remote-debugging-port={_debugPort} --user-data-dir=\"{_profileDir}\" --no-first-run --no-default-browser-check --window-size=920,680 https://www.moddb.com/addons/start/222467",
                UseShellExecute = true
            });
        }

        private async Task EnsureCloudflarePassedInternalAsync(CancellationToken ct)
        {
            string checkJs = @"
(async () => {
    if (!location.hostname.includes('moddb.com')) {
        location.href = 'https://www.moddb.com/addons/start/222467';
        return 'NAVIGATING';
    }
    const t = document.title || '';
    const b = document.body?.innerText || '';
    const hasTurnstile = !!document.querySelector('input[name=""cf-turnstile-response""]');
    if (hasTurnstile || t.includes('Just a moment') || t.includes('Один момент') || b.includes('Verify you are human') || b.includes('Подтвердите')) {
        return 'CF_CHALLENGE';
    }
    window.stop();
    if (!t.includes('ModDB CDP Ready')) {
        document.head.innerHTML = '<title>ModDB CDP Ready</title>';
        document.body.innerHTML = '<h3 style=""font-family:sans-serif;color:#2ecc71;background:#111;padding:20px"">G.A.M.M.A. Turbo ModDB Resolver Active (Do not close while downloading)</h3>';
    }
    return 'READY';
})()";
            string state = await EvaluateJsStringAsync(checkJs, ct);
            if (state == "NAVIGATING")
            {
                await Task.Delay(2500, ct);
                state = await EvaluateJsStringAsync(checkJs, ct);
            }

            if (state == "CF_CHALLENGE")
            {
                _log?.Invoke("[CDP] Обнаружена проверка Cloudflare Turnstile на ModDB — выполняю автоматический обход...");
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    await Task.Delay(2000, ct);
                    string coordsJson = await EvaluateJsStringAsync(@"
(() => {
    const inp = document.querySelector('input[name=""cf-turnstile-response""]');
    if (inp && inp.parentElement) {
        const r = inp.parentElement.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) {
            return JSON.stringify({ x: Math.round(r.left + 28), y: Math.round(r.top + r.height / 2) });
        }
    }
    return JSON.stringify({ x: 52, y: 338 });
})()", ct);

                    int clickX = 52, clickY = 338;
                    try
                    {
                        using var cdoc = JsonDocument.Parse(coordsJson);
                        clickX = cdoc.RootElement.GetProperty("x").GetInt32();
                        clickY = cdoc.RootElement.GetProperty("y").GetInt32();
                    }
                    catch { }

                    await SendCdpCommandAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x = clickX, y = clickY }, ct);
                    await Task.Delay(60, ct);
                    await SendCdpCommandAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x = clickX, y = clickY, button = "left", clickCount = 1 }, ct);
                    await Task.Delay(70, ct);
                    await SendCdpCommandAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x = clickX, y = clickY, button = "left", clickCount = 1 }, ct);

                    await Task.Delay(3000, ct);
                    string afterState = await EvaluateJsStringAsync(checkJs, ct);
                    if (afterState == "READY")
                    {
                        _log?.Invoke("[CDP] Cloudflare Turnstile успешно пройден! Сессия cf_clearance сохранена.");
                        return;
                    }
                }
            }
        }

        public async Task<string> ResolveDirectUrlAsync(string startUrl, int attemptIndex = 0, CancellationToken ct = default)
        {
            await _mutex.WaitAsync(ct);
            try
            {
                await EnsureConnectedInternalAsync(ct);

                for (int mOffset = 0; mOffset < Mirrors.Length; mOffset++)
                {
                    string mirror = Mirrors[(attemptIndex + mOffset) % Mirrors.Length];
                    var (status, cdnUrl) = await FetchAndInterceptMirrorAsync(startUrl, mirror, ct);
                    if (!string.IsNullOrEmpty(cdnUrl))
                    {
                        await Task.Delay(120, ct);
                        return cdnUrl;
                    }

                    if (status == "CF_CHALLENGE")
                    {
                        await SendCdpCommandAsync("Page.navigate", new { url = startUrl }, ct);
                        await Task.Delay(2500, ct);
                        await EnsureCloudflarePassedInternalAsync(ct);
                    }
                }

                return null;
            }
            catch
            {
                CleanupSocket();
                return null;
            }
            finally
            {
                _mutex.Release();
            }
        }

        private async Task<(string Status, string CdnUrl)> FetchAndInterceptMirrorAsync(string startUrl, string mirror, CancellationToken ct)
        {
            string mirrorArg = mirror ?? "";
            string js = $@"
(async () => {{
    try {{
        const r = await fetch('{startUrl}', {{ credentials: 'include' }});
        const html = await r.text();
        if (r.status === 403 || html.includes('Just a moment') || html.includes('Один момент') || html.includes('cf-turnstile')) return 'CF_CHALLENGE';
        const m = html.match(/https?:\/\/www\.moddb\.com\/downloads\/mirror\/[^\s""'<>]+/i) || html.match(/\/downloads\/mirror\/[^\s""'<>]+/i);
        if (!m) return 'NO_MIRROR';
        let mUrl = m[0].startsWith('http') ? m[0] : 'https://www.moddb.com' + m[0];
        if ('{mirrorArg}' !== '') {{
            mUrl = mUrl.replace(/(\/downloads\/mirror\/\d+\/)\d+\//, '$1{mirrorArg}/');
        }}
        try {{ await fetch(mUrl, {{ credentials: 'include' }}); }} catch (e) {{}}
        return mUrl;
    }} catch (err) {{
        return 'ERR:' + err.message;
    }}
}})()";

            string evalResult = await EvaluateJsStringAsync(js, ct);
            if (string.IsNullOrEmpty(evalResult) || evalResult == "CF_CHALLENGE" || evalResult == "NO_MIRROR" || evalResult.StartsWith("ERR:"))
                return (evalResult ?? "ERR", null);

            // Look up the exact mUrl in _mirrorLocations captured by ReceiveLoopAsync
            for (int wait = 0; wait < 10; wait++)
            {
                if (_mirrorLocations.TryRemove(evalResult, out string cdnUrl) && !string.IsNullOrEmpty(cdnUrl))
                    return ("OK", cdnUrl);
                await Task.Delay(50, ct);
            }

            return ("NO_LOCATION", null);
        }

        private async Task<string> EvaluateJsStringAsync(string expression, CancellationToken ct)
        {
            var root = await SendCdpCommandAsync("Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true
            }, ct);

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("result", out var res) &&
                res.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var val))
            {
                return val.GetString() ?? "";
            }
            return "";
        }

        private async Task<JsonElement> SendCdpCommandAsync(string method, object parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _cmdId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCommands[id] = tcs;

            try
            {
                await SendRawJsonAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), ct);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
                return await tcs.Task;
            }
            finally
            {
                _pendingCommands.TryRemove(id, out _);
            }
        }

        private async Task SendFireAndForgetAsync(string method, object parameters)
        {
            try
            {
                int id = Interlocked.Increment(ref _cmdId);
                await SendRawJsonAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), CancellationToken.None);
            }
            catch { }
        }

        private async Task SendRawJsonAsync(string json, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void CleanupSocket()
        {
            try { _rxCts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
            _pendingCommands.Clear();
        }

        public void Dispose()
        {
            CleanupSocket();
            try { _http?.Dispose(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            try { _sendLock?.Dispose(); } catch { }
        }
    }

    public sealed class TurboGammaInstaller
    {
        private readonly string _gammaPath;
        private readonly string _anomalyPath;
        private readonly string _coopWorkspacePath;
        private readonly Action<string> _log;
        private readonly Action<TurboInstallProgress> _onProgress;
        private readonly Action _onReapplyCoopEngine;

        private long _bytesSinceLastTick;
        private double _currentSpeedMbps;

        public TurboGammaInstaller(
            string gammaPath,
            string anomalyPath,
            string coopWorkspacePath,
            Action<string> log,
            Action<TurboInstallProgress> onProgress,
            Action onReapplyCoopEngine)
        {
            _gammaPath = gammaPath;
            _anomalyPath = anomalyPath;
            _coopWorkspacePath = coopWorkspacePath;
            _log = log;
            _onProgress = onProgress;
            _onReapplyCoopEngine = onReapplyCoopEngine;
        }

        public static List<GammaModEntry> ParseModsTxt(string modsTxtPath)
        {
            var result = new List<GammaModEntry>();
            if (!File.Exists(modsTxtPath)) return result;

            string[] lines = File.ReadAllLines(modsTxtPath, Encoding.UTF8);
            int counter = 0;
            foreach (string rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                counter++;
                string[] fields = rawLine.Split('\t');
                string dlLink = fields.Length > 0 ? fields[0] : "";

                if (!dlLink.Contains("http", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new GammaModEntry
                    {
                        Counter = counter,
                        IsSeparator = true,
                        DlLink = dlLink,
                        AddonName = dlLink.Trim()
                    });
                    continue;
                }

                string instructions = fields.Length > 1 ? fields[1].Trim() : "0";
                string patch = fields.Length > 2 ? fields[2] : "0";
                if (patch.Trim() == "0") patch = "";
                string addonName = fields.Length > 3 ? fields[3] : $"Addon_{counter}";
                string modDbUrl = fields.Length > 4 ? fields[4].Trim() : dlLink.Trim();
                string zipName = fields.Length > 5 ? fields[5].Trim() : "";
                string md5 = fields.Length > 6 ? fields[6].Trim().ToLowerInvariant() : "";

                bool isModDb = dlLink.Contains("moddb.com", StringComparison.OrdinalIgnoreCase);
                bool isGitHub = dlLink.Contains("github.com", StringComparison.OrdinalIgnoreCase);

                string archiveFileName = zipName;
                if (isGitHub || string.IsNullOrEmpty(archiveFileName))
                {
                    var parts = dlLink.Trim().Split('/');
                    string repoPart = parts.Length > 4 ? parts[4] : $"github_mod_{counter}";
                    archiveFileName = $"{repoPart}.zip";
                }

                result.Add(new GammaModEntry
                {
                    Counter = counter,
                    IsSeparator = false,
                    IsModDb = isModDb,
                    IsGitHub = isGitHub,
                    DlLink = dlLink.Trim(),
                    Instructions = string.IsNullOrEmpty(instructions) ? "0" : instructions,
                    Patch = patch,
                    AddonName = addonName,
                    ModDbUrl = modDbUrl,
                    ArchiveFileName = archiveFileName,
                    ExpectedMd5 = md5
                });
            }

            return result;
        }

        public async Task RunAsync(int maxConcurrency, CancellationToken ct)
        {
            string installerDir = Path.Combine(_gammaPath, ".Grok's Modpack Installer");
            string modsTxtPath = Path.Combine(installerDir, "mods.txt");
            string downloadsDir = Path.Combine(_gammaPath, "downloads");
            string modsDir = Path.Combine(_gammaPath, "mods");
            string sevenZipExe = Path.Combine(installerDir, "resources", "7zip", "7z.exe");
            if (!File.Exists(sevenZipExe))
                sevenZipExe = Path.Combine(installerDir, "resources", "7z.exe");

            if (!File.Exists(modsTxtPath))
                throw new FileNotFoundException($"Не найден список модов G.A.M.M.A.: {modsTxtPath}");
            if (!File.Exists(sevenZipExe))
                throw new FileNotFoundException($"Не найден архиватор 7z.exe: {sevenZipExe}");

            Directory.CreateDirectory(downloadsDir);
            Directory.CreateDirectory(modsDir);

            // 1. Best-effort Windows Defender exclusions so 7-Zip extraction isn't throttled
            TryAddDefenderExclusions();

            // 2. Ensure ModOrganizer.ini & G.A.M.M.A profile exist right away
            EnsureModOrganizerIni();
            SetupMo2ProfileAndCoopModlist();

            // 3. Parse mods.txt
            var allEntries = ParseModsTxt(modsTxtPath);
            int totalMods = allEntries.Count;
            _log($"[ТУРБО-GAMMA] Загружен список mods.txt: {totalMods} записей. Потоков скачивания и распаковки: {maxConcurrency}.");

            // 4. Create all separator folders immediately & identify disabled (-) mods from modlist.txt
            var disabledModCounters = GetDisabledModCounters(installerDir);
            string sepMetaSrc = Path.Combine(installerDir, "resources", "separator", "meta.ini");
            int installedModsCount = 0;
            foreach (var sep in allEntries.Where(e => e.IsSeparator))
            {
                string sepDir = Path.Combine(modsDir, sep.ModFolderName);
                Directory.CreateDirectory(sepDir);
                string dstMeta = Path.Combine(sepMetaSrc);
                string dstFile = Path.Combine(sepDir, "meta.ini");
                if (File.Exists(sepMetaSrc) && !File.Exists(dstFile))
                    File.Copy(sepMetaSrc, dstFile, true);
                installedModsCount++;
            }

            // Count already-disabled mods as skipped/done so we only download active (+) mods
            int skippedDisabledMods = allEntries.Count(e => !e.IsSeparator && disabledModCounters.Contains(e.Counter));
            installedModsCount += skippedDisabledMods;

            // 5. Group active (+) addon entries by unique archive filename
            var archiveGroups = allEntries
                .Where(e => !e.IsSeparator && !disabledModCounters.Contains(e.Counter))
                .GroupBy(e => e.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalArchives = archiveGroups.Count;
            int completedArchives = 0;
            int failedArchives = 0;

            string chromeProfileDir = Path.Combine(_coopWorkspacePath, "chrome-profile");
            using var cdpResolver = new ModDbCdpResolver(chromeProfileDir, _log);

            using var httpHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 32,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(20)
            };
            using var httpClient = new HttpClient(httpHandler)
            {
                Timeout = TimeSpan.FromMinutes(30)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36");

            // Background speed meter timer
            using var speedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var speedTask = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                while (!speedCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, speedCts.Token).ConfigureAwait(false);
                    long bytes = Interlocked.Exchange(ref _bytesSinceLastTick, 0);
                    double elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    sw.Restart();
                    _currentSpeedMbps = (bytes / (1024.0 * 1024.0)) / elapsedSec;
                }
            }, speedCts.Token);

            // 6. Parallel download & extraction across unique archive groups
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(archiveGroups, parallelOptions, async (group, token) =>
            {
                var primary = group.First();
                string archivePath = Path.Combine(downloadsDir, primary.ArchiveFileName);
                bool freshlyDownloaded = false;

                try
                {
                    bool allModsExtracted = File.Exists(archivePath) &&
                        new FileInfo(archivePath).Length >= 64 &&
                        group.All(e => IsModAlreadyExtracted(Path.Combine(modsDir, e.ModFolderName)));

                    if (allModsExtracted)
                    {
                        Interlocked.Increment(ref completedArchives);
                        int doneCached = Interlocked.Add(ref installedModsCount, group.Count());
                        ReportProgress("Скачивание и распаковка модов (10 потоков)...", completedArchives, totalArchives, doneCached, totalMods, failedArchives, primary.AddonName.Trim());
                        return;
                    }

                    bool archiveValid = IsArchiveAlreadyValid(archivePath, primary.ExpectedMd5);
                    if (!archiveValid)
                    {
                        ReportProgress("Скачивание и распаковка модов (10 потоков)...", completedArchives, totalArchives, installedModsCount, totalMods, failedArchives, primary.AddonName.Trim());
                        bool dlOk = await DownloadArchiveWithRetryAsync(primary, archivePath, cdpResolver, httpClient, token);
                        if (!dlOk)
                        {
                            Interlocked.Increment(ref failedArchives);
                            _log($"[ОШИБКА] Не удалось скачать архив {primary.ArchiveFileName} ({primary.AddonName.Trim()}) после всех попыток.");
                            return;
                        }
                        freshlyDownloaded = true;
                    }

                    Interlocked.Increment(ref completedArchives);

                    // Extract and apply instructions for each mod entry using this archive
                    foreach (var entry in group)
                    {
                        token.ThrowIfCancellationRequested();
                        string extractPath = Path.Combine(modsDir, entry.ModFolderName);

                        if (!freshlyDownloaded && IsModAlreadyExtracted(extractPath))
                        {
                            int doneCached = Interlocked.Increment(ref installedModsCount);
                            ReportProgress("Скачивание и распаковка модов (10 потоков)...", completedArchives, totalArchives, doneCached, totalMods, failedArchives, entry.AddonName.Trim());
                            continue;
                        }

                        await ExtractAndInstallModEntryAsync(entry, archivePath, extractPath, sevenZipExe, token);
                        int doneNow = Interlocked.Increment(ref installedModsCount);
                        _log($"[OK] [{doneNow}/{totalMods}] Установлен мод: {entry.ModFolderName}");
                        ReportProgress("Скачивание и распаковка модов (10 потоков)...", completedArchives, totalArchives, doneNow, totalMods, failedArchives, entry.AddonName.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedArchives);
                    _log($"[ОШИБКА] Сбой обработки {primary.ArchiveFileName}: {ex.Message}");
                }
            });

            speedCts.Cancel();

            // 7. Post-install finalization: copy pre-cloned large repos, G.A.M.M.A modpack_addons, patches, and re-apply 16p Co-op Engine
            await RunPostInstallFinalizationAsync(installerDir, modsDir, completedArchives, totalArchives, installedModsCount, totalMods, failedArchives, ct);
        }

        private async Task<bool> DownloadArchiveWithRetryAsync(
            GammaModEntry entry,
            string archivePath,
            ModDbCdpResolver cdpResolver,
            HttpClient httpClient,
            CancellationToken ct)
        {
            string partPath = archivePath + ".part";

            for (int attempt = 0; attempt < 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string downloadUrl = entry.DlLink;
                    if (entry.IsModDb)
                    {
                        downloadUrl = await cdpResolver.ResolveDirectUrlAsync(entry.DlLink, attempt, ct);
                        if (string.IsNullOrEmpty(downloadUrl))
                        {
                            await Task.Delay(1000, ct);
                            continue;
                        }
                    }

                    if (File.Exists(partPath))
                        File.Delete(partPath);

                    long? expectedContentLength = null;
                    using (var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
                    {
                        if (entry.IsModDb)
                            req.Headers.Referrer = new Uri("https://www.moddb.com/");

                        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        headerCts.CancelAfter(TimeSpan.FromSeconds(25));
                        using var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
                        if (!resp.IsSuccessStatusCode)
                        {
                            await Task.Delay(800, ct);
                            continue;
                        }

                        expectedContentLength = resp.Content.Headers.ContentLength;
                        await using var netStream = await resp.Content.ReadAsStreamAsync(headerCts.Token);
                        await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                        byte[] buffer = new byte[65536];
                        while (true)
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            readCts.CancelAfter(TimeSpan.FromSeconds(20));
                            int read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                            if (read <= 0) break;
                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            Interlocked.Add(ref _bytesSinceLastTick, read);
                        }
                    }

                    bool exactLengthReceived = expectedContentLength.HasValue &&
                        File.Exists(partPath) &&
                        new FileInfo(partPath).Length == expectedContentLength.Value;

                    if (IsArchiveAlreadyValid(partPath, entry.ExpectedMd5) ||
                        ((exactLengthReceived || attempt >= 1) && HasValidArchiveHeader(partPath)))
                    {
                        if (File.Exists(archivePath))
                            File.Delete(archivePath);
                        File.Move(partPath, archivePath);
                        return true;
                    }
                    else
                    {
                        _log($"[MD5] Повтор скачивания {entry.ArchiveFileName} с другого зеркала (попытка {attempt + 1}/5)...");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    await Task.Delay(1000, ct);
                }
            }

            return false;
        }

        private static bool IsArchiveAlreadyValid(string filePath, string expectedMd5)
        {
            if (!File.Exists(filePath)) return false;
            var fi = new FileInfo(filePath);
            if (fi.Length < 64) return false;

            if (string.IsNullOrWhiteSpace(expectedMd5))
                return true;

            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hash = md5.ComputeHash(stream);
                string actual = Convert.ToHexString(hash).ToLowerInvariant();
                return string.Equals(actual, expectedMd5.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsModAlreadyExtracted(string extractPath)
        {
            if (!Directory.Exists(extractPath)) return false;
            string metaIni = Path.Combine(extractPath, "meta.ini");
            if (!File.Exists(metaIni)) return false;
            return Directory.GetFileSystemEntries(extractPath).Length > 1;
        }

        private static bool HasValidArchiveHeader(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var fi = new FileInfo(filePath);
                if (fi.Length < 64) return false;
                byte[] hdr = new byte[6];
                using var fs = File.OpenRead(filePath);
                int read = fs.Read(hdr, 0, hdr.Length);
                if (read < 4) return false;
                // ZIP (PK\x03\x04), 7z (37 7A BC AF 27 1C), RAR (52 61 72 21)
                if (hdr[0] == 0x50 && hdr[1] == 0x4B) return true;
                if (hdr[0] == 0x37 && hdr[1] == 0x7A && hdr[2] == 0xBC && hdr[3] == 0xAF) return true;
                if (hdr[0] == 0x52 && hdr[1] == 0x61 && hdr[2] == 0x72 && hdr[3] == 0x21) return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static HashSet<int> GetDisabledModCounters(string installerDir)
        {
            var disabled = new HashSet<int>();
            var enabled = new HashSet<int>();
            try
            {
                string modlistPath = Path.Combine(installerDir, "G.A.M.M.A", "modpack_data", "modlist.txt");
                if (!File.Exists(modlistPath)) return disabled;
                foreach (string raw in File.ReadAllLines(modlistPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length < 3) continue;
                    bool isPlus = line[0] == '+';
                    bool isMinus = line[0] == '-';
                    if (!isPlus && !isMinus) continue;
                    int dashIdx = line.IndexOf('-', 1);
                    if (dashIdx > 1 && int.TryParse(line.Substring(1, dashIdx - 1), out int num))
                    {
                        if (isPlus) enabled.Add(num);
                        else if (isMinus) disabled.Add(num);
                    }
                }
                disabled.ExceptWith(enabled);
            }
            catch { }
            return disabled;
        }

        private static async Task ExtractAndInstallModEntryAsync(
            GammaModEntry entry,
            string archivePath,
            string extractPath,
            string sevenZipExe,
            CancellationToken ct)
        {
            if (Directory.Exists(extractPath))
            {
                foreach (var dir in Directory.GetDirectories(extractPath))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            Directory.CreateDirectory(extractPath);

            var psi = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = $"x \"{archivePath}\" -aoa -y -o\"{extractPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var proc = Process.Start(psi))
            {
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct);
                }
            }

            // Apply subfolder instructions if not "0"
            if (!string.IsNullOrWhiteSpace(entry.Instructions) && entry.Instructions.Trim() != "0")
            {
                string[] subFolders = entry.Instructions.Split(':', StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawSub in subFolders)
                {
                    string rel = rawSub.Trim().TrimStart('\\', '/').Replace('/', '\\');
                    if (string.IsNullOrEmpty(rel)) continue;

                    string sourceSub = Path.Combine(extractPath, rel);
                    if (!Directory.Exists(sourceSub)) continue;

                    string gamedataInsideSub = Path.Combine(sourceSub, "gamedata");
                    if (Directory.Exists(gamedataInsideSub))
                    {
                        CopyDirectoryRecursive(sourceSub, extractPath);
                    }
                    else
                    {
                        string targetGamedata = Path.Combine(extractPath, "gamedata");
                        Directory.CreateDirectory(targetGamedata);
                        CopyDirectoryRecursive(sourceSub, targetGamedata);
                    }
                }

                // Clean top-level directories except gamedata, appdata, db, fomod
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "gamedata", "appdata", "db", "fomod"
                };
                foreach (var dir in Directory.GetDirectories(extractPath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!allowed.Contains(dirName))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }

            // Write meta.ini
            string metaContent =
                "[General]\r\n" +
                "gameName=stalkeranomaly\r\n" +
                "modid=0\r\n" +
                "ignoredVersion=\r\n" +
                $"version={entry.ArchiveFileName}\r\n" +
                "newestVersion=\r\n" +
                "category=\"-1,\"\r\n" +
                "nexusFileStatus=1\r\n" +
                $"installationFile={entry.ArchiveFileName}\r\n" +
                "repository=\r\n" +
                "userData=\r\n" +
                "comments=\r\n" +
                "notes=\r\n" +
                "endorsestate=0\r\n" +
                "hasCustomURL=true\r\n" +
                $"url={entry.ModDbUrl}\r\n";

            await File.WriteAllTextAsync(Path.Combine(extractPath, "meta.ini"), metaContent, Encoding.UTF8, ct);
        }

        private async Task RunPostInstallFinalizationAsync(
            string installerDir,
            string modsDir,
            int completedArchives,
            int totalArchives,
            int installedMods,
            int totalMods,
            int failedArchives,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                ReportProgress("Финализация: копирование gamma_large_files_v2...", completedArchives, totalArchives, installedMods, totalMods, failedArchives, "gamma_large_files_v2");
                string largeFilesDir = Path.Combine(installerDir, "resources", "gamma_large_files_v2");
                if (Directory.Exists(largeFilesDir))
                {
                    _log("[ФИНАЛИЗАЦИЯ] Копирование крупных пакетов G.A.M.M.A. (gamma_large_files_v2)...");
                    foreach (var sub in Directory.GetDirectories(largeFilesDir))
                    {
                        if (Path.GetFileName(sub).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        CopyDirectoryRecursive(sub, Path.Combine(modsDir, Path.GetFileName(sub)), true);
                    }
                }

                ct.ThrowIfCancellationRequested();
                ReportProgress("Финализация: копирование Gunslinger Guns (Teivaz)...", completedArchives, totalArchives, installedMods, totalMods, failedArchives, "teivaz_anomaly_gunslinger");
                string teivazDir = Path.Combine(installerDir, "resources", "teivaz_anomaly_gunslinger");
                string gunslingerTarget = Path.Combine(modsDir, "312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team", "gamedata");
                if (Directory.Exists(teivazDir))
                {
                    _log("[ФИНАЛИЗАЦИЯ] Установка стволов Gunslinger (teivaz_anomaly_gunslinger)...");
                    Directory.CreateDirectory(gunslingerTarget);
                    foreach (var lvl1 in Directory.GetDirectories(teivazDir))
                    {
                        if (Path.GetFileName(lvl1).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        string gd1 = Path.Combine(lvl1, "gamedata");
                        if (Directory.Exists(gd1))
                            CopyDirectoryRecursive(gd1, gunslingerTarget, true);

                        foreach (var lvl2 in Directory.GetDirectories(lvl1))
                        {
                            string gd2 = Path.Combine(lvl2, "gamedata");
                            if (Directory.Exists(gd2))
                                CopyDirectoryRecursive(gd2, gunslingerTarget, true);
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();
                ReportProgress("Финализация: копирование 414 патчей G.A.M.M.A. (modpack_addons)...", completedArchives, totalArchives, installedMods, totalMods, failedArchives, "modpack_addons");
                string modpackAddons = Path.Combine(installerDir, "G.A.M.M.A", "modpack_addons");
                if (!Directory.Exists(modpackAddons))
                    modpackAddons = Path.Combine(_gammaPath, "G.A.M.M.A", "modpack_addons");
                if (Directory.Exists(modpackAddons))
                {
                    _log("[ФИНАЛИЗАЦИЯ] Установка 414 модульных аддонов G.A.M.M.A. (modpack_addons)...");
                    CopyDirectoryRecursive(modpackAddons, modsDir, true);
                }

                ct.ThrowIfCancellationRequested();
                ReportProgress("Финализация: настройка профиля MO2 и патчей Anomaly...", completedArchives, totalArchives, installedMods, totalMods, failedArchives, "MO2 Profile + Co-op 16p");
                SetupMo2ProfileAndCoopModlist();

                string modpackPatches = Path.Combine(installerDir, "G.A.M.M.A", "modpack_patches");
                if (Directory.Exists(modpackPatches) && Directory.Exists(_anomalyPath))
                {
                    _log("[ФИНАЛИЗАЦИЯ] Применение базовых конфигов G.A.M.M.A. (modpack_patches) и повторная активация кооп-движка 16p...");
                    CopyDirectoryRecursive(modpackPatches, _anomalyPath);
                    string shadersCache = Path.Combine(_anomalyPath, "appdata", "shaders_cache");
                    if (Directory.Exists(shadersCache))
                    {
                        try { Directory.Delete(shadersCache, true); } catch { }
                    }
                }

                _onReapplyCoopEngine?.Invoke();
                ReportProgress("Установка G.A.M.M.A. + Кооп (16 игроков) завершена!", completedArchives, totalArchives, installedMods, totalMods, failedArchives, "Готово к запуску");
                _log("[ГОТОВО] Полная сборка S.T.A.L.K.E.R. G.A.M.M.A. + xrRazom Co-op (16 игроков) готова к игре!");
            }, ct);
        }

        private void TryAddDefenderExclusions()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"Add-MpPreference -ExclusionPath '{_gammaPath}','{_anomalyPath}' -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private void EnsureModOrganizerIni()
        {
            try
            {
                string mo2Ini = Path.Combine(_gammaPath, "ModOrganizer.ini");
                string escapedAnomaly = _anomalyPath.Replace("\\", "\\\\");
                string slashAnomaly = _anomalyPath.Replace("\\", "/");
                string content =
                    "[General]\r\n" +
                    "gameName=STALKER Anomaly\r\n" +
                    $"gamePath=@ByteArray({escapedAnomaly})\r\n" +
                    "selected_profile=@ByteArray(G.A.M.M.A)\r\n" +
                    "first_start=false\r\n\r\n" +
                    "[Settings]\r\n" +
                    "profile_local_inis=false\r\n" +
                    "profile_local_saves=false\r\n\r\n" +
                    "[customExecutables]\r\n" +
                    "size=2\r\n" +
                    "1\\title=Anomaly Launcher\r\n" +
                    $"1\\binary={slashAnomaly}/AnomalyLauncher.exe\r\n" +
                    $"1\\workingDirectory={slashAnomaly}\r\n" +
                    "1\\arguments=\r\n" +
                    "1\\custom=true\r\n" +
                    "2\\title=G.A.M.M.A. Co-op 16 Players (DX11 AVX)\r\n" +
                    $"2\\binary={slashAnomaly}/bin/AnomalyDX11AVX.exe\r\n" +
                    $"2\\workingDirectory={slashAnomaly}\r\n" +
                    "2\\arguments=-noprefetch -nointro -skiplogo -nosplashwindow\r\n" +
                    "2\\custom=true\r\n";

                File.WriteAllText(mo2Ini, content, new UTF8Encoding(false));
                _log("[MO2] Обновлён файл конфигурации C:\\GAMMA\\ModOrganizer.ini (-noprefetch -nointro -skiplogo -nosplashwindow).");
            }
            catch { }
        }

        private void SetupMo2ProfileAndCoopModlist()
        {
            SetupMo2ProfileAndCoopModlistStatic(_gammaPath);
        }

        public static void SetupMo2ProfileAndCoopModlistStatic(string gammaPath)
        {
            try
            {
                string installerDir = Path.Combine(gammaPath, ".Grok's Modpack Installer");
                string profileDir = Path.Combine(gammaPath, "profiles", "G.A.M.M.A");
                Directory.CreateDirectory(profileDir);

                string profilesFilesSrc = Path.Combine(installerDir, "resources", "profiles_files");
                if (Directory.Exists(profilesFilesSrc))
                {
                    CopyDirectoryRecursive(profilesFilesSrc, profileDir);
                }

                string modlistSrc = Path.Combine(installerDir, "G.A.M.M.A", "modpack_data", "modlist.txt");
                if (!File.Exists(modlistSrc))
                    modlistSrc = Path.Combine(gammaPath, "G.A.M.M.A", "modpack_data", "modlist.txt");

                if (File.Exists(modlistSrc))
                {
                    var lines = File.ReadAllLines(modlistSrc, Encoding.UTF8)
                        .Select(l => l.TrimEnd())
                        .ToList();

                    string[] coopMods =
                    {
                        "+zz xrRazom Fix - Launcher Bridge",
                        "+zz xrRazom Fix - Fetch Reward",
                        "+zz xrRazom Fix - Kill Tracker Mirror Corpses",
                        "+zz xrRazom Fix - Laser State",
                        "+zz xrRazom Fix - Ledge Grabbing",
                        "+zz xrRazom Fix - Quest Vars Sync",
                        "+zz xrRazom Fix - Remember Belt",
                        "+zz xrRazom Fix - Taskboard Peer Details",
                        "+zz xrRazom Fix - Taskboard Refresh",
                        "+zz xrRazom Fix - Turned-in Task Sync",
                        "+zz xrRazom Fix - Weapon Parts Tooltip",
                        "+zz xrRazom 1.4 Co-op (16 Players Unleashed)"
                    };

                    lines.RemoveAll(l => l.TrimStart().StartsWith("+zz xrRazom", StringComparison.OrdinalIgnoreCase) ||
                                         l.TrimStart().StartsWith("-zz xrRazom", StringComparison.OrdinalIgnoreCase));
                    lines.InsertRange(0, coopMods);
                    File.WriteAllLines(Path.Combine(profileDir, "modlist.txt"), lines, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static int BakeActiveGammaModsToAnomaly(
            string gammaPath,
            string anomalyPath,
            string coopWorkspacePath,
            Action<string> log,
            bool forceRebake = false)
        {
            var sw = Stopwatch.StartNew();
            SetupMo2ProfileAndCoopModlistStatic(gammaPath);

            string bakedGamedata = Path.Combine(anomalyPath, "gamedata");
            string vanillaGdBackup = Path.Combine(anomalyPath, "gamedata_vanilla_backup");
            if (Directory.Exists(bakedGamedata) && !Directory.Exists(vanillaGdBackup) && !File.Exists(Path.Combine(bakedGamedata, ".gamma_baked_ready")))
            {
                CopyDirectoryRecursive(bakedGamedata, vanillaGdBackup, true);
            }

            string readyStamp = Path.Combine(bakedGamedata, ".gamma_baked_ready");
            string fsgameSrc = Path.Combine(anomalyPath, "fsgame.ltx");
            string fsgameFast = Path.Combine(anomalyPath, "fsgame_gamma_fast.ltx");
            string fsgameClient2 = Path.Combine(anomalyPath, "fsgame_gamma_client2.ltx");

            // Always ensure fsgame_gamma_fast.ltx and fsgame_gamma_client2.ltx use standard gamedata\ (required by .db0 archives)
            if (File.Exists(fsgameSrc))
            {
                string rawFs = File.ReadAllText(fsgameSrc, Encoding.UTF8);
                File.WriteAllText(fsgameFast, rawFs, new UTF8Encoding(false));

                string client2Fs = Regex.Replace(rawFs, @"\|\s*appdata\\", "| appdata_client2\\", RegexOptions.IgnoreCase);
                File.WriteAllText(fsgameClient2, client2Fs, new UTF8Encoding(false));
            }

            if (!forceRebake && File.Exists(readyStamp) && Directory.Exists(Path.Combine(bakedGamedata, "scripts")))
            {
                // Still sync top-level zz_xrr_launcher_bridge.script in case it was updated
                string bridgeSrc = Path.Combine(coopWorkspacePath, "xrRazom-1.4", "gamedata", "scripts", "zz_xrr_launcher_bridge.script");
                string bridgeDst = Path.Combine(bakedGamedata, "scripts", "zz_xrr_launcher_bridge.script");
                if (File.Exists(bridgeSrc))
                {
                    try
                    {
                        if (File.Exists(bridgeDst)) File.Delete(bridgeDst);
                        File.Copy(bridgeSrc, bridgeDst, true);
                    }
                    catch { }
                }
                return 0;
            }

            log?.Invoke("[БЫСТРЫЙ СТАРТ] Запекание 594 активных модов G.A.M.M.A. + Кооп 16p (NTFS HardLinks, 0 ГБ лишнего места)...");

            string modlistPath = Path.Combine(gammaPath, "profiles", "G.A.M.M.A", "modlist.txt");
            string modsDir = Path.Combine(gammaPath, "mods");
            var priorityGamedataDirs = new List<string>();

            if (File.Exists(modlistPath) && Directory.Exists(modsDir))
            {
                foreach (string rawLine in File.ReadAllLines(modlistPath, Encoding.UTF8))
                {
                    string line = rawLine.Trim();
                    if (!line.StartsWith("+") || line.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string modName = line.Substring(1).Trim();
                    string modFolder = Path.Combine(modsDir, modName);
                    if (!Directory.Exists(modFolder)) continue;

                    string gd = Path.Combine(modFolder, "gamedata");
                    if (Directory.Exists(gd))
                        priorityGamedataDirs.Add(gd);

                    // Also map db\ (.db0 addons) and appdata\ (.ltx shader presets)
                    string dbSub = Path.Combine(modFolder, "db");
                    if (Directory.Exists(dbSub))
                    {
                        CopyDirectoryRecursive(dbSub, Path.Combine(anomalyPath, "db"), true);
                    }
                    string appSub = Path.Combine(modFolder, "appdata");
                    if (Directory.Exists(appSub))
                    {
                        CopyDirectoryRecursive(appSub, Path.Combine(anomalyPath, "appdata"), true);
                        CopyDirectoryRecursive(appSub, Path.Combine(anomalyPath, "appdata_client2"), true);
                    }
                }
            }

            // Lowest priority: modpack_patches\gamedata -> xrRazom-1.4\Anomaly\gamedata -> base Anomaly\gamedata
            string modpackPatchesGd = Path.Combine(gammaPath, ".Grok's Modpack Installer", "G.A.M.M.A", "modpack_patches", "gamedata");
            if (Directory.Exists(modpackPatchesGd)) priorityGamedataDirs.Add(modpackPatchesGd);

            string xrRazomAnomalyGd = Path.Combine(coopWorkspacePath, "xrRazom-1.4", "Anomaly", "gamedata");
            if (Directory.Exists(xrRazomAnomalyGd)) priorityGamedataDirs.Add(xrRazomAnomalyGd);

            if (Directory.Exists(vanillaGdBackup)) priorityGamedataDirs.Add(vanillaGdBackup);
            else if (Directory.Exists(bakedGamedata)) priorityGamedataDirs.Add(bakedGamedata);

            // Resolve winning file for every relative path (Line 1 of modlist.txt wins conflicts)
            var winners = new Dictionary<string, string>(65536, StringComparer.OrdinalIgnoreCase);
            foreach (string gdDir in priorityGamedataDirs)
            {
                int prefixLen = gdDir.TrimEnd('\\', '/').Length + 1;
                foreach (string file in Directory.EnumerateFiles(gdDir, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".orig.bak", StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = file.Substring(prefixLen);
                    if (!winners.ContainsKey(rel))
                    {
                        winners[rel] = file;
                    }
                }
            }

            // Pre-create all target subdirectories
            Directory.CreateDirectory(bakedGamedata);
            var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rel in winners.Keys)
            {
                string dirPart = Path.GetDirectoryName(rel);
                if (!string.IsNullOrEmpty(dirPart))
                    targetDirs.Add(Path.Combine(bakedGamedata, dirPart));
            }
            foreach (string d in targetDirs)
            {
                Directory.CreateDirectory(d);
            }

            // Parallel NTFS HardLink / Copy
            int linkedCount = 0;
            Parallel.ForEach(winners, new ParallelOptions { MaxDegreeOfParallelism = 8 }, kv =>
            {
                string dst = Path.Combine(bakedGamedata, kv.Key);
                string src = kv.Value;
                try
                {
                    string ext = Path.GetExtension(src);
                    bool isConfigOrScript =
                        ext.Equals(".ltx", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".script", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ps", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".gs", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".s", StringComparison.OrdinalIgnoreCase);

                    if (File.Exists(dst))
                    {
                        if (!forceRebake && !isConfigOrScript)
                        {
                            var dstFi = new FileInfo(dst);
                            var srcFi = new FileInfo(src);
                            if (dstFi.Length == srcFi.Length)
                            {
                                Interlocked.Increment(ref linkedCount);
                                return;
                            }
                        }
                        File.Delete(dst);
                    }

                    if (isConfigOrScript)
                    {
                        File.Copy(src, dst, true);
                    }
                    else
                    {
                        if (!CreateHardLinkW(dst, src, IntPtr.Zero))
                        {
                            File.Copy(src, dst, true);
                        }
                    }
                    Interlocked.Increment(ref linkedCount);
                }
                catch { }
            });

            string shadersCache = Path.Combine(anomalyPath, "appdata", "shaders_cache");
            if (Directory.Exists(shadersCache))
            {
                try { Directory.Delete(shadersCache, true); } catch { }
            }
            string shadersCache2 = Path.Combine(anomalyPath, "appdata_client2", "shaders_cache");
            if (Directory.Exists(shadersCache2))
            {
                try { Directory.Delete(shadersCache2, true); } catch { }
            }

            File.WriteAllText(readyStamp, $"Baked {linkedCount} files at {DateTime.Now:O}\r\n", Encoding.UTF8);
            sw.Stop();
            log?.Invoke($"[БЫСТРЫЙ СТАРТ] Готово! Собрано {linkedCount} файлов из {priorityGamedataDirs.Count} слоёв G.A.M.M.A. за {sw.Elapsed.TotalSeconds:0.1} сек.");
            return linkedCount;
        }

        private void ReportProgress(
            string phase,
            int downloadedArchives,
            int totalArchives,
            int installedMods,
            int totalMods,
            int failedArchives,
            string currentItem)
        {
            _onProgress?.Invoke(new TurboInstallProgress
            {
                Phase = phase,
                DownloadedArchives = downloadedArchives,
                TotalArchives = totalArchives,
                InstalledMods = installedMods,
                TotalMods = totalMods,
                FailedArchives = failedArchives,
                SpeedMbps = Math.Round(_currentSpeedMbps, 1),
                CurrentItem = currentItem
            });
        }

        private static void CopyDirectoryRecursive(string sourceDir, string targetDir, bool skipSameSize = false)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                try
                {
                    if (skipSameSize && File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(file).Length)
                        continue;
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Copy(file, dest, false);
                }
                catch { }
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                string subName = Path.GetFileName(sub);
                if (subName.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                CopyDirectoryRecursive(sub, Path.Combine(targetDir, subName), skipSameSize);
            }
        }
    }
}
