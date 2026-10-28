using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GammaZoneLauncher
{
    public class ReleaseVolumeEntry
    {
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public class ReleaseManifest
    {
        public string Version { get; set; } = "1.0.0-gamma-coop-16p";
        public string BuildDateUtc { get; set; } = DateTime.UtcNow.ToString("O");
        public string BaseReleaseUrl { get; set; } = "";
        public string PatchArchiveName { get; set; } = "gamma_coop_patch.7z";
        public long PatchArchiveSize { get; set; }
        public string PatchArchiveSha256 { get; set; } = "";
        public int ActiveModsCount { get; set; } = 594;
        public int TotalBakedFiles { get; set; } = 61020;
        public List<ReleaseVolumeEntry> BaseVolumes { get; set; } = new List<ReleaseVolumeEntry>();
    }

    public class ReleaseProgressInfo
    {
        public string Phase { get; set; } = "";
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public double SpeedMbps { get; set; }
        public string Detail { get; set; } = "";
    }

    public sealed class ReleasePackagerAndUpdater
    {
        private readonly string _anomalyPath;
        private readonly string _gammaPath;
        private readonly string _releaseOutDir;
        private readonly Action<string> _log;
        private readonly Action<ReleaseProgressInfo> _onProgress;

        public ReleasePackagerAndUpdater(
            string anomalyPath,
            string gammaPath,
            string releaseOutDir,
            Action<string> log,
            Action<ReleaseProgressInfo> onProgress = null)
        {
            _anomalyPath = anomalyPath;
            _gammaPath = gammaPath;
            _releaseOutDir = releaseOutDir;
            _log = log ?? (_ => { });
            _onProgress = onProgress ?? (_ => { });
        }

        public const string DefaultGitHubReleaseUrl = "https://github.com/NegliadoV/GAMMA-Coop-16Players/releases/download/v1.0.0";

        public static string NormalizeReleaseUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return DefaultGitHubReleaseUrl;

            string u = rawUrl.Trim();
            // If it's a local path that exists and contains manifest.json, keep it
            if (Directory.Exists(u) && File.Exists(Path.Combine(u, "manifest.json")))
                return u;
            if (File.Exists(u) && Path.GetFileName(u).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                return u;

            // If it's the old local default C:\GAMMA-Coop-Release and doesn't exist on this PC, use GitHub
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultGitHubReleaseUrl;
            }

            // Convert /releases/tag/<tag> -> /releases/download/<tag>
            if (u.Contains("/releases/tag/", StringComparison.OrdinalIgnoreCase))
            {
                u = u.Replace("/releases/tag/", "/releases/download/", StringComparison.OrdinalIgnoreCase);
            }
            else if (u.EndsWith("/GAMMA-Coop-16Players", StringComparison.OrdinalIgnoreCase) ||
                     u.EndsWith("/GAMMA-Coop-16Players/", StringComparison.OrdinalIgnoreCase) ||
                     u.EndsWith("/releases", StringComparison.OrdinalIgnoreCase) ||
                     u.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultGitHubReleaseUrl;
            }

            return u;
        }

        public static string ResolveSevenZipExe(string gammaPath)
        {
            var searchDirs = new List<string>();
            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                string procDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrEmpty(procDir))
                {
                    searchDirs.Add(procDir);
                    var parent = Directory.GetParent(procDir)?.FullName;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        searchDirs.Add(parent);
                        searchDirs.Add(Path.Combine(parent, "gamma-cli", "resources"));
                    }
                    searchDirs.Add(Path.Combine(procDir, "gamma-cli", "resources"));
                }
            }
            if (!string.IsNullOrEmpty(AppDomain.CurrentDomain.BaseDirectory))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                searchDirs.Add(baseDir);
                var parent = Directory.GetParent(baseDir)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    searchDirs.Add(Path.Combine(parent, "gamma-cli", "resources"));
                }
            }

            foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var exeName in new[] { "7z.exe", "7zz.exe" })
                {
                    string candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            if (!string.IsNullOrWhiteSpace(gammaPath))
            {
                string grok7z = Path.Combine(gammaPath, ".Grok's Modpack Installer", "resources", "7zip", "7z.exe");
                if (File.Exists(grok7z)) return grok7z;
            }

            foreach (var p in new[]
            {
                @"C:\GAMMA\.Grok's Modpack Installer\resources\7zip\7z.exe",
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            })
            {
                if (File.Exists(p)) return p;
            }

            throw new FileNotFoundException("Не найден архиватор 7z.exe / 7zz.exe.");
        }

        public static void EnsureBundledSevenZipInLauncherDir(string gammaPath)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string srcDir = Path.Combine(gammaPath ?? @"C:\GAMMA", ".Grok's Modpack Installer", "resources", "7zip");
                if (!Directory.Exists(srcDir)) return;

                foreach (var f in new[] { "7z.exe", "7z.dll" })
                {
                    string src = Path.Combine(srcDir, f);
                    string dst = Path.Combine(baseDir, f);
                    if (File.Exists(src) && !File.Exists(dst))
                    {
                        File.Copy(src, dst, true);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Packs the fast update archive (gamma_coop_patch.7z: bin + configs + scripts + shaders + fsgame*.ltx)
        /// and updates manifest.json + FriendLauncher.zip in _releaseOutDir.
        /// Takes ~5-10 seconds and allows instant delta updates for all friends.
        /// </summary>
        public async Task<ReleaseManifest> PackFastPatchAndLauncherAsync(string baseReleaseUrl, CancellationToken ct)
        {
            Directory.CreateDirectory(_releaseOutDir);
            EnsureBundledSevenZipInLauncherDir(_gammaPath);
            string sevenZip = ResolveSevenZipExe(_gammaPath);

            string manifestPath = Path.Combine(_releaseOutDir, "manifest.json");
            ReleaseManifest manifest = File.Exists(manifestPath)
                ? (JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? new ReleaseManifest())
                : new ReleaseManifest();

            if (!string.IsNullOrWhiteSpace(baseReleaseUrl))
                manifest.BaseReleaseUrl = baseReleaseUrl.Trim();

            manifest.BuildDateUtc = DateTime.UtcNow.ToString("O");
            manifest.Version = $"1.0.{DateTime.UtcNow:MMdd.HHmm}-gamma-coop-16p";

            string patchArchivePath = Path.Combine(_releaseOutDir, manifest.PatchArchiveName);
            bool reuseRecentPatch = File.Exists(patchArchivePath) &&
                new FileInfo(patchArchivePath).Length > 1024 * 1024 &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(patchArchivePath)).TotalMinutes < 30;

            if (!reuseRecentPatch)
            {
                if (File.Exists(patchArchivePath)) File.Delete(patchArchivePath);

                _log($"[РЕЛИЗ-ПАТЧ] Упаковка быстрого патча синхронизации ({manifest.PatchArchiveName})...");
                Report("Упаковка быстрого патча (bin + configs + scripts + shaders)...", 1, 4, manifest.PatchArchiveName);

                var patchItems = new List<string>();
                foreach (var rel in new[]
                {
                    "bin",
                    @"gamedata\configs",
                    @"gamedata\scripts",
                    @"gamedata\shaders",
                    "fsgame.ltx",
                    "fsgame_gamma_fast.ltx",
                    "fsgame_gamma_client2.ltx",
                    "steam_appid.txt"
                })
                {
                    string full = Path.Combine(_anomalyPath, rel);
                    if (Directory.Exists(full) || File.Exists(full))
                        patchItems.Add($"\"{rel}\"");
                }

                string args = $"a -t7z -mx=1 -mmt=on \"{patchArchivePath}\" {string.Join(" ", patchItems)} -xr!*.bak -xr!*.orig.bak";
                await RunSevenZipAsync(sevenZip, _anomalyPath, args, ct);
            }

            var patchFi = new FileInfo(patchArchivePath);
            manifest.PatchArchiveSize = patchFi.Length;
            manifest.PatchArchiveSha256 = await ComputeSha256Async(patchArchivePath, ct);

            _log($"[РЕЛИЗ-ПАТЧ] Патч собран: {patchFi.Length / (1024.0 * 1024.0):0.1} МБ (SHA256: {manifest.PatchArchiveSha256[..12]}...)");

            // Also scan existing base volumes in _releaseOutDir if present
            var volumeFiles = Directory.GetFiles(_releaseOutDir, "gamma_coop_base.7z.*")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (volumeFiles.Count > 0)
            {
                var prevByFile = manifest.BaseVolumes.ToDictionary(v => v.FileName, v => v, StringComparer.OrdinalIgnoreCase);
                manifest.BaseVolumes.Clear();
                int vIdx = 0;
                foreach (var vf in volumeFiles)
                {
                    vIdx++;
                    var vfi = new FileInfo(vf);
                    if (prevByFile.TryGetValue(vfi.Name, out var prev) && prev.SizeBytes == vfi.Length && !string.IsNullOrEmpty(prev.Sha256))
                    {
                        manifest.BaseVolumes.Add(prev);
                        continue;
                    }
                    Report($"Хэширование тома {vIdx}/{volumeFiles.Count}: {vfi.Name}...", 2, 4, vfi.Name);
                    manifest.BaseVolumes.Add(new ReleaseVolumeEntry
                    {
                        FileName = vfi.Name,
                        SizeBytes = vfi.Length,
                        Sha256 = await ComputeSha256Async(vf, ct)
                    });
                }
            }

            // Save manifest.json
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, json, Encoding.UTF8, ct);

            // Bundle standalone client launcher archive
            Report("Упаковка архива клиентского лаунчера (GAMMA-Coop-Launcher.zip)...", 3, 4, "GAMMA-Coop-Launcher.zip");
            PackClientLauncherZip(manifest.BaseReleaseUrl);

            Report("Готово! Дельта-патч и клиентский лаунчер собраны.", 4, 4, _releaseOutDir);
            _log($"[РЕЛИЗ-ПАТЧ] Манифест и клиентский лаунчер сохранены в: {_releaseOutDir}");
            return manifest;
        }

        /// <summary>
        /// Packs the entire ready-to-play 75.5 GB build (db + bin + baked gamedata) into 1.9 GB volumes
        /// (gamma_coop_base.7z.001, .002, ...) suitable for GitHub Releases (< 2 GB per file limit).
        /// </summary>
        public async Task<ReleaseManifest> PackFullBaseVolumesAsync(string baseReleaseUrl, CancellationToken ct)
        {
            Directory.CreateDirectory(_releaseOutDir);
            EnsureBundledSevenZipInLauncherDir(_gammaPath);
            string sevenZip = ResolveSevenZipExe(_gammaPath);

            // Clean old volumes if any
            foreach (var oldVol in Directory.GetFiles(_releaseOutDir, "gamma_coop_base.7z.*"))
            {
                try { File.Delete(oldVol); } catch { }
            }

            string baseArchivePrefix = Path.Combine(_releaseOutDir, "gamma_coop_base.7z");
            _log($"[ УПАКОВКА БАЗЫ ] Начинаю упаковку базового клиента ({_anomalyPath}) в тома по 1.9 ГБ -> {_releaseOutDir}...");
            Report("Упаковка полной базы (db + bin + gamedata) в тома по 1.9 ГБ (7-Zip Multi-Thread)...", 1, 3, "gamma_coop_base.7z.001...");

            var includeItems = new List<string>();
            foreach (var item in new[]
            {
                "db",
                "bin",
                "gamedata",
                "fsgame.ltx",
                "fsgame_gamma_fast.ltx",
                "fsgame_gamma_client2.ltx",
                "steam_appid.txt",
                "AnomalyLauncher.exe"
            })
            {
                string full = Path.Combine(_anomalyPath, item);
                if (Directory.Exists(full) || File.Exists(full))
                    includeItems.Add($"\"{item}\"");
            }

            // -mx=1 (Fast LZMA2) + -mmt=on packs 75 GB at ~400 MB/s on NVMe, -v1900m splits at 1.9 GB (<2 GB GitHub limit), -bsp1 outputs live %
            string args = $"a -t7z -mx=1 -mmt=on -v1900m -bsp1 \"{baseArchivePrefix}\" {string.Join(" ", includeItems)} -xr!*.bak -xr!*.orig.bak";
            await RunSevenZipWithProgressAsync(sevenZip, _anomalyPath, args, "Упаковка базы G.A.M.M.A. (75 ГБ) в тома по 1.9 ГБ", ct);

            var volumes = Directory.GetFiles(_releaseOutDir, "gamma_coop_base.7z.*")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            long totalPackedBytes = volumes.Sum(f => new FileInfo(f).Length);
            _log($"[ УПАКОВКА БАЗЫ ] Создано томов: {volumes.Count} шт. Общий размер: {totalPackedBytes / (1024.0 * 1024.0 * 1024.0):0.2} ГБ.");

            return await PackFastPatchAndLauncherAsync(baseReleaseUrl, ct);
        }

        private void PackClientLauncherZip(string defaultReleaseUrl)
        {
            try
            {
                string zipPath = Path.Combine(_releaseOutDir, "GAMMA-Coop-Launcher.zip");
                string compatZipPath = Path.Combine(_releaseOutDir, "GAMMA-Coop-Friend-Launcher.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (File.Exists(compatZipPath)) File.Delete(compatZipPath);

                string launcherBinDir = AppDomain.CurrentDomain.BaseDirectory;
                string tempStage = Path.Combine(_releaseOutDir, "_client_launcher_stage");
                if (Directory.Exists(tempStage)) Directory.Delete(tempStage, true);
                Directory.CreateDirectory(tempStage);

                foreach (var file in Directory.GetFiles(launcherBinDir))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("launcher_config.json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string dst = Path.Combine(tempStage, name);
                    using var srcFs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
                    srcFs.CopyTo(dstFs);
                }

                var clientCfg = new LauncherConfig
                {
                    AnomalyPath = @"C:\STALKER-GAMMA-Coop",
                    GammaPath = @"C:\STALKER-GAMMA-Coop",
                    PlayerName = "Stalker",
                    ReleaseManifestUrl = defaultReleaseUrl ?? "",
                    LaunchModeIndex = 0
                };
                File.WriteAllText(
                    Path.Combine(tempStage, "launcher_config.json"),
                    JsonSerializer.Serialize(clientCfg, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);

                ZipFile.CreateFromDirectory(tempStage, zipPath, CompressionLevel.Optimal, false);
                File.Copy(zipPath, compatZipPath, true);
                Directory.Delete(tempStage, true);
            }
            catch (Exception ex)
            {
                _log("[ПРЕДУПРЕЖДЕНИЕ] Не удалось собрать GAMMA-Coop-Launcher.zip: " + ex.Message);
            }
        }

        public static bool IsFullGammaBaseInstalled(string targetInstallDir)
        {
            if (string.IsNullOrWhiteSpace(targetInstallDir) || !Directory.Exists(targetInstallDir))
                return false;

            return Directory.Exists(Path.Combine(targetInstallDir, "db")) &&
                   Directory.Exists(Path.Combine(targetInstallDir, "gamedata", "configs")) &&
                   Directory.Exists(Path.Combine(targetInstallDir, "gamedata", "meshes")) &&
                   Directory.Exists(Path.Combine(targetInstallDir, "gamedata", "sounds")) &&
                   File.Exists(Path.Combine(targetInstallDir, "bin", "AnomalyDX11AVX.exe"));
        }

        /// <summary>
        /// Downloads and installs the full build (if not installed) or applies the fast delta patch from BaseReleaseUrl / manifest.json.
        /// </summary>
        public async Task InstallOrUpdateClientFromUrlAsync(
            string manifestOrReleaseUrl,
            string targetInstallDir,
            CancellationToken ct,
            bool forceDownloadBase = false,
            bool forceApplyPatch = false)
        {
            string normalizedUrl = NormalizeReleaseUrl(manifestOrReleaseUrl);

            Directory.CreateDirectory(targetInstallDir);
            EnsureBundledSevenZipInLauncherDir(_gammaPath);
            string sevenZip = ResolveSevenZipExe(_gammaPath);

            string trimmedUrl = normalizedUrl.Trim();
            bool isLocalDir = Directory.Exists(trimmedUrl) || (File.Exists(trimmedUrl) && Path.GetFileName(trimmedUrl).Equals("manifest.json", StringComparison.OrdinalIgnoreCase));

            ReleaseManifest manifest;
            string baseSourceUrlOrDir;

            using var http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                MaxConnectionsPerServer = 16,
                ConnectTimeout = TimeSpan.FromSeconds(25)
            })
            {
                Timeout = TimeSpan.FromHours(4)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GammaZoneLauncher/1.5");

            if (isLocalDir)
            {
                baseSourceUrlOrDir = File.Exists(trimmedUrl) ? Path.GetDirectoryName(trimmedUrl) : trimmedUrl;
                string localManifestPath = Path.Combine(baseSourceUrlOrDir, "manifest.json");
                if (!File.Exists(localManifestPath))
                    throw new FileNotFoundException($"В папке {baseSourceUrlOrDir} не найден manifest.json.");
                manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(localManifestPath, ct));
            }
            else
            {
                string manifestUrl = trimmedUrl.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)
                    ? trimmedUrl
                    : trimmedUrl.TrimEnd('/') + "/manifest.json";
                baseSourceUrlOrDir = manifestUrl[..manifestUrl.LastIndexOf('/')];

                _log($"[АПДЕЙТЕР] Загрузка манифеста сборки с GitHub: {manifestUrl}...");
                Report("Подключение к GitHub Releases и чтение manifest.json...", 1, 1000, "manifest.json");
                string json = await http.GetStringAsync(manifestUrl, ct);
                manifest = JsonSerializer.Deserialize<ReleaseManifest>(json);
            }

            if (manifest == null)
                throw new InvalidOperationException("Не удалось прочитать manifest.json.");

            _log($"[АПДЕЙТЕР] Найдена версия сборки: {manifest.Version} (Активных модов: {manifest.ActiveModsCount}, томов базы: {manifest.BaseVolumes?.Count ?? 0}).");

            bool hasBaseInstalled = !forceDownloadBase && IsFullGammaBaseInstalled(targetInstallDir);

            string cacheDir = Path.Combine(targetInstallDir, "_release_cache");
            Directory.CreateDirectory(cacheDir);

            // 1. If base game is missing (or forceDownloadBase requested), download all 21 volumes and extract
            if (!hasBaseInstalled && manifest.BaseVolumes != null && manifest.BaseVolumes.Count > 0)
            {
                int totalVols = manifest.BaseVolumes.Count;
                long totalBaseBytes = manifest.BaseVolumes.Sum(v => v.SizeBytes);
                long downloadedBytes = 0;

                // Pre-count already downloaded volumes so the progress bar starts at accurate %
                foreach (var v in manifest.BaseVolumes)
                {
                    string dv = Path.Combine(cacheDir, v.FileName);
                    if (File.Exists(dv) && new FileInfo(dv).Length == v.SizeBytes)
                    {
                        downloadedBytes += v.SizeBytes;
                    }
                    else if (File.Exists(dv + ".part"))
                    {
                        long pLen = new FileInfo(dv + ".part").Length;
                        if (pLen > 0 && pLen <= v.SizeBytes) downloadedBytes += pLen;
                    }
                }

                _log($"[УСТАНОВКА БАЗЫ 38.5 ГБ] Целевая папка: {targetInstallDir}. Скачивание {totalVols} томов (в 4 параллельных потока с докачкой)...");
                var sw = Stopwatch.StartNew();
                long sessionBytesStart = downloadedBytes;
                long lastReportMs = 0;
                int doneVols = 0;

                await Parallel.ForEachAsync(manifest.BaseVolumes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (vol, token) =>
                {
                    string dstVol = Path.Combine(cacheDir, vol.FileName);
                    if (File.Exists(dstVol) && new FileInfo(dstVol).Length == vol.SizeBytes)
                    {
                        int cur = Interlocked.Increment(ref doneVols);
                        _log($"[КЭШ] Том уже скачан ({cur}/{totalVols}): {vol.FileName}");
                        int permille = totalBaseBytes > 0 ? (int)Math.Clamp((Interlocked.Read(ref downloadedBytes) * 1000L) / totalBaseBytes, 1, 1000) : 100;
                        Report($"Том в кэше ({cur}/{totalVols}): {vol.FileName}", permille, 1000, $"{cur}/{totalVols} томов");
                        return;
                    }

                    if (isLocalDir)
                    {
                        _log($"[КОПИРОВАНИЕ] {vol.FileName} ({vol.SizeBytes / (1024 * 1024)} МБ)...");
                        File.Copy(Path.Combine(baseSourceUrlOrDir, vol.FileName), dstVol, true);
                        Interlocked.Add(ref downloadedBytes, vol.SizeBytes);
                    }
                    else
                    {
                        string volUrl = $"{baseSourceUrlOrDir}/{vol.FileName}";
                        _log($"[СКАЧИВАНИЕ] Начата загрузка {vol.FileName} ({vol.SizeBytes / (1024 * 1024)} МБ)...");
                        await DownloadFileWithResumeAsync(http, volUrl, dstVol, vol.SizeBytes, bytesDelta =>
                        {
                            long curBytes = Interlocked.Add(ref downloadedBytes, bytesDelta);
                            long nowMs = sw.ElapsedMilliseconds;
                            long prevMs = Interlocked.Read(ref lastReportMs);
                            if (nowMs - prevMs >= 250 && Interlocked.CompareExchange(ref lastReportMs, nowMs, prevMs) == prevMs)
                            {
                                double elapsedSec = Math.Max(0.5, nowMs / 1000.0);
                                double speedMbps = ((curBytes - sessionBytesStart) / (1024.0 * 1024.0)) / elapsedSec;
                                double curGb = curBytes / (1024.0 * 1024.0 * 1024.0);
                                double totGb = totalBaseBytes / (1024.0 * 1024.0 * 1024.0);
                                double pct = totalBaseBytes > 0 ? (curBytes * 100.0) / totalBaseBytes : 0;
                                int permille = (int)Math.Clamp(pct * 10.0, 1, 999);
                                int curDone = Volatile.Read(ref doneVols);
                                Report(
                                    $"Загрузка базы: {curGb:0.00} / {totGb:0.00} ГБ ({pct:0.0}%) — {speedMbps:0.1} МБ/с",
                                    permille,
                                    1000,
                                    $"Томов: {curDone}/{totalVols}");
                            }
                        }, token);
                    }

                    int completed = Interlocked.Increment(ref doneVols);
                    _log($"[ГОТОВ ТОМ {completed}/{totalVols}] {vol.FileName} успешно скачан.");
                    int pDone = totalBaseBytes > 0 ? (int)Math.Clamp((Interlocked.Read(ref downloadedBytes) * 1000L) / totalBaseBytes, 1, 1000) : 1000;
                    Report($"Скачано томов: {completed} / {totalVols}", pDone, 1000, vol.FileName);
                });

                string firstVol = Path.Combine(cacheDir, manifest.BaseVolumes[0].FileName);
                _log($"[РАСПАКОВКА БАЗЫ] Распаковка 21 тома ({firstVol}) -> {targetInstallDir}...");
                Report("Распаковка базовой сборки G.A.M.M.A. Co-op (75 ГБ)...", 990, 1000, firstVol);
                await RunSevenZipWithProgressAsync(sevenZip, targetInstallDir, $"x -y -mmt=on -bsp1 \"{firstVol}\" -o\"{targetInstallDir}\"", "Распаковка базовой сборки G.A.M.M.A. (75 ГБ)", ct);

                if (IsFullGammaBaseInstalled(targetInstallDir))
                {
                    _log("[ОЧИСТКА КЭША] Удаление временных архивов базы (38.5 ГБ) после успешной распаковки...");
                    foreach (var v in manifest.BaseVolumes)
                    {
                        try
                        {
                            string cachedVol = Path.Combine(cacheDir, v.FileName);
                            if (File.Exists(cachedVol)) File.Delete(cachedVol);
                        }
                        catch { }
                    }
                }
            }

            // 2. Check & apply fast patch archive (gamma_coop_patch.7z)
            if (!string.IsNullOrEmpty(manifest.PatchArchiveName))
            {
                string stampFile = Path.Combine(targetInstallDir, ".gamma_patch_sha256");
                string currentSha = File.Exists(stampFile) ? (await File.ReadAllTextAsync(stampFile, ct)).Trim() : "";

                if (forceApplyPatch || !hasBaseInstalled || !string.Equals(currentSha, manifest.PatchArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    string dstPatch = Path.Combine(cacheDir, manifest.PatchArchiveName);
                    _log($"[ОБНОВЛЕНИЕ ПАТЧА] Загрузка свежего патча {manifest.PatchArchiveName} ({manifest.PatchArchiveSize / (1024.0 * 1024.0):0.1} МБ)...");
                    Report($"Скачивание патча {manifest.Version}...", 10, 100, manifest.PatchArchiveName);

                    if (isLocalDir)
                    {
                        File.Copy(Path.Combine(baseSourceUrlOrDir, manifest.PatchArchiveName), dstPatch, true);
                    }
                    else
                    {
                        string patchUrl = $"{baseSourceUrlOrDir}/{manifest.PatchArchiveName}";
                        long patchRead = 0;
                        var patchSw = Stopwatch.StartNew();
                        long lastPatchReportMs = 0;
                        await DownloadFileWithResumeAsync(http, patchUrl, dstPatch, manifest.PatchArchiveSize, delta =>
                        {
                            long cur = Interlocked.Add(ref patchRead, delta);
                            long nowMs = patchSw.ElapsedMilliseconds;
                            long prevMs = Interlocked.Read(ref lastPatchReportMs);
                            if (nowMs - prevMs >= 200 && Interlocked.CompareExchange(ref lastPatchReportMs, nowMs, prevMs) == prevMs)
                            {
                                int pct = manifest.PatchArchiveSize > 0 ? (int)Math.Clamp((cur * 100L) / manifest.PatchArchiveSize, 1, 99) : 50;
                                Report($"Скачивание патча: {cur / (1024.0 * 1024.0):0.1} / {manifest.PatchArchiveSize / (1024.0 * 1024.0):0.1} МБ", pct, 100, manifest.PatchArchiveName);
                            }
                        }, ct);
                    }

                    _log($"[ОБНОВЛЕНИЕ ПАТЧА] Установка патча {manifest.Version} в {targetInstallDir}...");
                    Report($"Распаковка патча {manifest.Version}...", 95, 100, manifest.PatchArchiveName);
                    await RunSevenZipAsync(sevenZip, targetInstallDir, $"x -y -mmt=on \"{dstPatch}\" -o\"{targetInstallDir}\"", ct);

                    // Clear shaders_cache so updated shaders recompile cleanly
                    string shadersCache = Path.Combine(targetInstallDir, "appdata", "shaders_cache");
                    if (Directory.Exists(shadersCache))
                    {
                        try { Directory.Delete(shadersCache, true); } catch { }
                    }

                    await File.WriteAllTextAsync(stampFile, manifest.PatchArchiveSha256 ?? "", Encoding.UTF8, ct);
                    Directory.CreateDirectory(Path.Combine(targetInstallDir, "gamedata"));
                    File.WriteAllText(Path.Combine(targetInstallDir, "gamedata", ".gamma_baked_ready"), $"Installed {manifest.Version} at {DateTime.Now:O}\r\n", Encoding.UTF8);
                    _log($"[ГОТОВО] Сборка успешно установлена и обновлена до версии {manifest.Version}!");
                }
                else
                {
                    _log($"[ОК] У вас уже установлена самая свежая версия сборки ({manifest.Version}).");
                }
            }

            Report($"Сборка {manifest.Version} полностью готова к игре!", 100, 100, manifest.Version);
        }

        private static async Task DownloadFileWithResumeAsync(
            HttpClient http,
            string url,
            string destPath,
            long expectedSize,
            Action<long> onBytesReceived,
            CancellationToken ct)
        {
            string partPath = destPath + ".part";
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    long existingLen = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    if (existingLen > expectedSize && expectedSize > 0)
                    {
                        File.Delete(partPath);
                        existingLen = 0;
                    }
                    if (existingLen == expectedSize && expectedSize > 0)
                    {
                        if (File.Exists(destPath)) File.Delete(destPath);
                        File.Move(partPath, destPath);
                        return;
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existingLen > 0)
                        req.Headers.Range = new RangeHeaderValue(existingLen, null);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        File.Delete(partPath);
                        existingLen = 0;
                        continue;
                    }
                    resp.EnsureSuccessStatusCode();

                    bool append = resp.StatusCode == HttpStatusCode.PartialContent && existingLen > 0;
                    using (var fs = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 262144, true))
                    using (var net = await resp.Content.ReadAsStreamAsync(ct))
                    {
                        byte[] buffer = new byte[262144];
                        int read;
                        while ((read = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                            onBytesReceived?.Invoke(read);
                        }
                    }

                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(partPath, destPath);
                    return;
                }
                catch (Exception) when (attempt < 4 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(2000 * attempt, ct);
                }
            }
        }

        private async Task RunSevenZipWithProgressAsync(string sevenZipExe, string workingDir, string arguments, string phaseTitle, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            bool isPacking = arguments.TrimStart().StartsWith("a ", StringComparison.OrdinalIgnoreCase);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить 7z.exe");
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            int lastParsedPct = 0;
            var stdOutTask = Task.Run(async () =>
            {
                char[] buf = new char[512];
                var reader = proc.StandardOutput;
                int n;
                while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    string chunk = new string(buf, 0, n);
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        if (chunk[i] == '%')
                        {
                            int end = i;
                            int start = i - 1;
                            while (start >= 0 && char.IsDigit(chunk[start])) start--;
                            start++;
                            if (end > start && (end - start) <= 3 && int.TryParse(chunk.AsSpan(start, end - start), out int pct))
                            {
                                if (pct >= 0 && pct <= 100)
                                    Volatile.Write(ref lastParsedPct, pct);
                            }
                        }
                    }
                }
            }, ct);

            var sw = Stopwatch.StartNew();
            while (!proc.HasExited)
            {
                await Task.WhenAny(proc.WaitForExitAsync(ct), Task.Delay(500, ct));
                if (proc.HasExited) break;

                int pct = Volatile.Read(ref lastParsedPct);
                if (isPacking && !string.IsNullOrWhiteSpace(_releaseOutDir) && Directory.Exists(_releaseOutDir))
                {
                    var vols = Directory.GetFiles(_releaseOutDir, "gamma_coop_base.7z.*");
                    long totalPacked = vols.Sum(v => { try { return new FileInfo(v).Length; } catch { return 0L; } });
                    double gb = totalPacked / (1024.0 * 1024.0 * 1024.0);
                    int estPct = pct > 0 ? pct : (int)Math.Clamp(Math.Round(gb / 38.0 * 100.0), 1, 99);
                    Report($"{phaseTitle}: {estPct}%", estPct, 100, $"Томов: {vols.Length} шт. ({gb:0.1} ГБ)");
                }
                else
                {
                    int displayPct = Math.Clamp(pct, 1, 99);
                    Report($"{phaseTitle}: {displayPct}%", displayPct, 100, $"Прошло: {sw.Elapsed:mm\\:ss}");
                }
            }

            await Task.WhenAll(stdOutTask, stdErrTask);
            if (proc.ExitCode != 0 && proc.ExitCode != 1)
            {
                throw new InvalidOperationException($"Ошибка 7z (код {proc.ExitCode}): {stdErrTask.Result}");
            }
        }

        private static async Task RunSevenZipAsync(string sevenZipExe, string workingDir, string arguments, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipExe,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить 7z.exe");
            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(stdOutTask, stdErrTask);

            if (proc.ExitCode != 0 && proc.ExitCode != 1) // 1 = non-fatal warning in 7z
            {
                throw new InvalidOperationException($"Ошибка 7z (код {proc.ExitCode}): {stdErrTask.Result}");
            }
        }

        private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, true);
            byte[] hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void Report(string phase, int cur, int total, string detail)
        {
            _onProgress(new ReleaseProgressInfo
            {
                Phase = phase,
                CurrentStep = cur,
                TotalSteps = Math.Max(1, total),
                Detail = detail
            });
        }
    }
}
