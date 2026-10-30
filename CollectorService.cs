using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OsuCollectorLazer;

internal static class CollectorService
{
    private const string CollectorApi = "https://osucollector.com/api/collections/";
    private static readonly HttpClient Http = CreateHttp();
    private static bool _gameStartedByUs;
    private static int _collectorIpIndex;

    public static async Task<CollectorCollection> FetchCollectionAsync(int id)
    {
        using var resp = await SendCollectorGetWithRetryAsync(CollectorApi + id);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new Exception($"Коллекция #{id} не найдена на osu!collector.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? $"Collection #{id}" : $"Collection #{id}";
        string uploader = root.TryGetProperty("uploader", out var u) && u.TryGetProperty("username", out var un)
            ? un.GetString() ?? "?" : "?";
        string desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        int bmCount = root.TryGetProperty("beatmapCount", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
        int favs = root.TryGetProperty("favourites", out var f) && f.TryGetInt32(out int fi) ? fi : 0;

        var sets = new List<CollectorSet>();
        var seenSets = new HashSet<int>();

        if (root.TryGetProperty("beatmapsets", out var setsArr) && setsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var setEl in setsArr.EnumerateArray())
            {
                if (!setEl.TryGetProperty("id", out var sid) || !sid.TryGetInt32(out int setId) || setId <= 0 || !seenSets.Add(setId))
                    continue;

                var bms = new List<CollectorBeatmap>();
                if (setEl.TryGetProperty("beatmaps", out var bmsArr) && bmsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bmEl in bmsArr.EnumerateArray())
                    {
                        int bmId = bmEl.TryGetProperty("id", out var bid) && bid.ValueKind == JsonValueKind.Number && bid.TryGetInt32(out int bi) ? bi : 0;
                        string checksum = bmEl.TryGetProperty("checksum", out var cs) ? (cs.GetString() ?? "").Trim().ToLowerInvariant() : "";
                        bms.Add(new CollectorBeatmap(bmId, checksum));
                    }
                }
                sets.Add(new CollectorSet(setId, bms));
            }
        }

        return new CollectorCollection(id, name, uploader, desc, bmCount, favs, sets);
    }

    public static async Task<CollectionPageResult> FetchPopularPageAsync(
        string range = "alltime",
        int perPage = 12,
        int? cursor = null)
    {
        string url = $"{CollectorApi}popularv2?range={range}&perPage={perPage}";
        if (cursor.HasValue) url += $"&cursor={cursor.Value}";
        return await FetchPageFromUrlAsync(url);
    }

    public static async Task<CollectionPageResult> SearchCollectionsPageAsync(
        string searchQuery,
        string? sortBy = null,
        int perPage = 12,
        int? cursor = null)
    {
        string encoded = Uri.EscapeDataString(searchQuery ?? "");
        string url = $"{CollectorApi}search?search={encoded}&perPage={perPage}";
        if (!string.IsNullOrEmpty(sortBy))
            url += $"&sortBy={sortBy}&orderBy=desc";
        if (cursor.HasValue)
            url += $"&cursor={cursor.Value}";
        return await FetchPageFromUrlAsync(url);
    }

    private static async Task<HttpResponseMessage> SendCollectorGetWithRetryAsync(string url)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (attempt > 0)
                {
                    // Форсируем новое TCP+TLS соединение к следующему IP при повторной попытке
                    req.Headers.ConnectionClose = true;
                }

                var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                if ((int)resp.StatusCode >= 500 && attempt < 4)
                {
                    resp.Dispose();
                    Interlocked.Increment(ref _collectorIpIndex);
                    continue;
                }
                return resp;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                WorkingEndpointCache.TryRemove("osucollector.com", out _);
                Interlocked.Increment(ref _collectorIpIndex);
                if (attempt < 4)
                    await Task.Delay(250);
            }
        }

        throw lastEx ?? new HttpRequestException("Не удалось подключиться к osucollector.com.");
    }

    private static async Task<CollectionPageResult> FetchPageFromUrlAsync(string url)
    {
        using var resp = await SendCollectorGetWithRetryAsync(url);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        bool hasMore = root.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
        int? nextCursor = root.TryGetProperty("nextPageCursor", out var nc) && nc.TryGetInt32(out int nci) ? nci : null;
        int? totalResults = root.TryGetProperty("results", out var res) && res.TryGetInt32(out int ri) ? ri : null;

        var list = new List<PopularCollectionSummary>();
        if (root.TryGetProperty("collections", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                int id = item.TryGetProperty("id", out var i) && i.TryGetInt32(out int ii) ? ii : 0;
                if (id <= 0) continue;
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                string uploader = item.TryGetProperty("uploader", out var u) && u.TryGetProperty("username", out var un)
                    ? un.GetString() ?? "?" : "?";
                int count = item.TryGetProperty("beatmapCount", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
                int favs = item.TryGetProperty("favourites", out var f) && f.TryGetInt32(out int fi) ? fi : 0;
                list.Add(new PopularCollectionSummary(id, name, uploader, count, favs));
            }
        }
        return new CollectionPageResult(list, hasMore, nextCursor, totalResults);
    }

    public sealed record DownloadRunResult(
        int OkCount,
        int FailCount,
        List<int> FailedSetIds,
        Dictionary<int, List<string>> DownloadedOszMd5s);

    public static async Task<DownloadRunResult> DownloadAndImportSetsAsync(
        CollectorCollection col,
        List<int> setIds,
        ChronosConfig config,
        bool noImport,
        string? lazerExe,
        string? stableSongsDir,
        string tempDir,
        History history,
        string? customDownloadDir = null)
    {
        int done = 0, ok = 0, fail = 0;
        int total = setIds.Count;
        var failed = new List<int>();
        var oszMd5s = new ConcurrentDictionary<int, List<string>>();
        var setMap = col.Sets.ToDictionary(s => s.Id, s => s);

        bool isStable = config.TargetMode == OsuTargetMode.Stable;

        var importChannel = Channel.CreateUnbounded<(int setId, string path)>();
        Task importer = (noImport || isStable)
            ? Task.CompletedTask
            : Task.Run(() => ImportLoopAsync(importChannel.Reader, lazerExe!, config.BatchSize, history));

        using var sem = new SemaphoreSlim(Math.Clamp(config.Threads, 1, 16));
        var tasks = setIds.Select(async setId =>
        {
            await sem.WaitAsync();
            try
            {
                // Для stable при включённом импорте качаем прямо в папку Songs (или в SaveDir)
                string targetDir = customDownloadDir ?? (
                    (!noImport && isStable && stableSongsDir != null)
                        ? stableSongsDir
                        : (config.SaveDir ?? tempDir));

                var result = await DownloadSetAsync(setId, targetDir, config);
                int n = Interlocked.Increment(ref done);
                ChronosUi.SetWindowTitle($"[{n}/{total}] Скачивание «{col.Name}»");
                string prefix = $"  [{n.ToString().PadLeft(total.ToString().Length)}/{total}]";

                if (result.Path == null)
                {
                    Interlocked.Increment(ref fail);
                    lock (failed) failed.Add(setId);
                    ChronosUi.WriteLine($"{prefix} ✖ ОШИБКА #{setId}: {result.Error}", ConsoleColor.Red);
                    return;
                }

                Interlocked.Increment(ref ok);
                var targetHashes = LazerDatabase.ProcessDownloadedOsz(result.Path);
                if (targetHashes.Count > 0)
                    oszMd5s[setId] = targetHashes;

                ChronosUi.WriteLine(
                    $"{prefix} ✔ #{setId,-8} {result.Bytes / 1024.0 / 1024.0,5:0.0} MB  [{result.Mirror}] ({targetHashes.Count} дифф.)",
                    ConsoleColor.Green);

                if (noImport)
                {
                    history.Add(setId);
                }
                else if (isStable)
                {
                    // В osu!stable файл уже лежит в папке Songs\<setId>.osz — игра сама распакует его при входе в выбор песен (или по F5)
                    if (config.SaveDir != null && stableSongsDir != null &&
                        !string.Equals(Path.GetFullPath(config.SaveDir), Path.GetFullPath(stableSongsDir), StringComparison.OrdinalIgnoreCase))
                    {
                        string copyPath = Path.Combine(config.SaveDir, Path.GetFileName(result.Path));
                        File.Copy(result.Path, copyPath, true);
                    }
                    history.Add(setId);
                }
                else
                {
                    string importPath = result.Path;
                    if (config.SaveDir != null)
                    {
                        importPath = Path.Combine(tempDir, Path.GetFileName(result.Path));
                        File.Copy(result.Path, importPath, true);
                    }
                    await importChannel.Writer.WriteAsync((setId, importPath));
                }
            }
            finally
            {
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        importChannel.Writer.Complete();
        await importer;

        ChronosUi.SetWindowTitle();

        if (failed.Count > 0)
        {
            Console.WriteLine();
            ChronosUi.WriteLine("  Не удалось скачать с зеркал (можно скачать вручную с сайта osu!):", ConsoleColor.Yellow);
            foreach (int id in failed.OrderBy(x => x))
                Console.WriteLine($"    https://osu.ppy.sh/beatmapsets/{id}");
        }

        return new DownloadRunResult(ok, fail, failed, new Dictionary<int, List<string>>(oszMd5s));
    }

    // ===================================================================== Mirrors

    private sealed record Mirror(string Name, Func<int, bool, string> Url);

    private static readonly Mirror[] Mirrors =
    {
        new("beatconnect", (id, nv) => $"https://beatconnect.io/b/{id}/"),
        new("catboy.best", (id, nv) => $"https://catboy.best/d/{id}{(nv ? "n" : "")}"),
        new("osu.direct",  (id, nv) => $"https://osu.direct/api/d/{id}{(nv ? "?noVideo=1" : "")}"),
        new("nerinyan",    (id, nv) => $"https://api.nerinyan.moe/d/{id}{(nv ? "?nv=1" : "")}"),
        new("sayobot",     (id, nv) => $"https://dl.sayobot.cn/beatmaps/download/{(nv ? "novideo" : "full")}/{id}"),
    };

    private sealed record DownloadResult(string? Path, long Bytes, string? Mirror, string? Error);

    private static async Task<DownloadResult> DownloadSetAsync(int setId, string dir, ChronosConfig config)
    {
        string finalPath = Path.Combine(dir, $"{setId}.osz");
        string partPath = $"{finalPath}.{Guid.NewGuid():N}.part";
        string lastError = "нет доступных зеркал";
        bool noVideo = config.NoVideo;

        // 1. Приоритетный источник: Официальный сервер osu.ppy.sh (если выполнен вход в аккаунт)
        string? osuToken = await OsuAuthService.GetValidAccessTokenAsync(config);
        if (!string.IsNullOrEmpty(osuToken))
        {
            try
            {
                string osuUrl = $"https://osu.ppy.sh/api/v2/beatmapsets/{setId}/download{(noVideo ? "?noVideo=1" : "")}";
                using var req = new HttpRequestMessage(HttpMethod.Get, osuUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", osuToken);
                using var headerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    long bytes = 0;
                    await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await using (var src = await resp.Content.ReadAsStreamAsync(headerCts.Token))
                    {
                        byte[] buffer = new byte[65536];
                        while (true)
                        {
                            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            int read = await src.ReadAsync(buffer, readCts.Token);
                            if (read == 0) break;
                            await dst.WriteAsync(buffer.AsMemory(0, read));
                            bytes += read;
                        }
                    }

                    if (IsValidZipArchive(partPath))
                    {
                        File.Move(partPath, finalPath, true);
                        return new DownloadResult(finalPath, bytes, "osu.ppy.sh [Официальный]", null);
                    }
                    TryDelete(partPath);
                }
            }
            catch
            {
                TryDelete(partPath);
            }
        }

        // 2. Бесплатные зеркала с быстрым переключением и автодокачкой
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var mirror in Mirrors)
            {
                try
                {
                    string url = mirror.Url(setId, noVideo);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (mirror.Name == "nerinyan")
                        req.Headers.Referrer = new Uri("https://nerinyan.moe/");

                    using var headerCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

                    if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode == 503)
                    {
                        lastError = $"{mirror.Name}: лимит запросов";
                        if (pass > 0)
                        {
                            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
                            if (wait > TimeSpan.FromSeconds(8)) wait = TimeSpan.FromSeconds(8);
                            await Task.Delay(wait);
                        }
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        lastError = $"{mirror.Name}: HTTP {(int)resp.StatusCode}";
                        continue;
                    }

                    long? expectedLength = resp.Content.Headers.ContentLength;
                    string finalRangeUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;

                    long bytes = 0;
                    Exception? streamError = null;

                    await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        try
                        {
                            await using var src = await resp.Content.ReadAsStreamAsync(headerCts.Token);
                            byte[] buffer = new byte[65536];
                            while (true)
                            {
                                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                                int read = await src.ReadAsync(buffer, readCts.Token);
                                if (read == 0)
                                    break;
                                await dst.WriteAsync(buffer.AsMemory(0, read));
                                bytes += read;
                            }
                        }
                        catch (Exception ex)
                        {
                            streamError = ex;
                        }

                        // Если ТСПУ или CDN оборвал соединение посередине скачивания, докачиваем остаток файла через HTTP Range
                        if ((streamError != null || (expectedLength.HasValue && bytes < expectedLength.Value)) && bytes > 0)
                        {
                            bool resumed = await TryResumeWithRangeAsync(finalRangeUrl, dst, bytes, expectedLength);
                            if (resumed)
                            {
                                bytes = dst.Length;
                                streamError = null;
                            }
                        }
                    }

                    if (streamError != null)
                    {
                        TryDelete(partPath);
                        lastError = $"{mirror.Name}: обрыв потока ({bytes / 1024} КБ)";
                        continue;
                    }

                    if (expectedLength.HasValue && expectedLength.Value > 0 && bytes < expectedLength.Value)
                    {
                        TryDelete(partPath);
                        lastError = $"{mirror.Name}: неполный файл ({bytes / 1024}/{expectedLength.Value / 1024} КБ)";
                        continue;
                    }

                    if (!IsValidZipArchive(partPath))
                    {
                        TryDelete(partPath);
                        lastError = $"{mirror.Name}: ответ не является целым .osz архивом";
                        continue;
                    }

                    File.Move(partPath, finalPath, true);
                    return new DownloadResult(finalPath, bytes, mirror.Name, null);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException)
                {
                    lastError = $"{mirror.Name}: {ex.GetBaseException().Message}";
                    TryDelete(partPath);
                }
            }

            if (pass == 0)
                await Task.Delay(800);
        }

        return new DownloadResult(null, 0, null, lastError);
    }

    private static async Task<bool> TryResumeWithRangeAsync(
        string url,
        FileStream dst,
        long currentOffset,
        long? expectedTotalLength)
    {
        const long chunkSize = 512 * 1024;
        int consecutiveFailures = 0;

        while (!expectedTotalLength.HasValue || currentOffset < expectedTotalLength.Value)
        {
            if (consecutiveFailures >= 3)
                return false;

            try
            {
                long endOffset = expectedTotalLength.HasValue
                    ? Math.Min(currentOffset + chunkSize - 1, expectedTotalLength.Value - 1)
                    : currentOffset + chunkSize - 1;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(currentOffset, endOffset);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                    return false;

                if (!expectedTotalLength.HasValue && resp.Content.Headers.ContentRange?.Length is long totalLen and > 0)
                    expectedTotalLength = totalLen;

                await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                byte[] buffer = new byte[65536];
                long readInChunk = 0;

                while (true)
                {
                    using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    int r = await src.ReadAsync(buffer, readCts.Token);
                    if (r == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, r));
                    currentOffset += r;
                    readInChunk += r;
                }

                if (readInChunk == 0)
                {
                    consecutiveFailures++;
                    continue;
                }

                consecutiveFailures = 0;
                if (!expectedTotalLength.HasValue && readInChunk < chunkSize)
                    break;
            }
            catch
            {
                consecutiveFailures++;
                await Task.Delay(250);
            }
        }

        await dst.FlushAsync();
        return !expectedTotalLength.HasValue || currentOffset >= expectedTotalLength.Value;
    }

    private static bool IsValidZipArchive(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 22) return false;
            Span<byte> b = stackalloc byte[4];
            if (fs.Read(b) < 4 || b[0] != (byte)'P' || b[1] != (byte)'K')
                return false;

            long tailSize = Math.Min(fs.Length, 65557);
            byte[] tail = new byte[tailSize];
            fs.Seek(fs.Length - tailSize, SeekOrigin.Begin);
            int read = fs.Read(tail, 0, tail.Length);
            for (int i = read - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ===================================================================== Lazer IPC Import

    private static async Task ImportLoopAsync(
        ChannelReader<(int setId, string path)> reader,
        string lazerExe,
        int batchSize,
        History history)
    {
        var batch = new List<(int setId, string path)>();

        while (await reader.WaitToReadAsync())
        {
            await Task.Delay(1200);
            while (batch.Count < batchSize && reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count == 0) continue;

            bool imported = await ImportBatchAsync(lazerExe, batch.Select(b => b.path).ToList());
            if (imported)
            {
                foreach (var b in batch) history.Add(b.setId);
                ChronosUi.WriteLine($"  » Отправлено в osu!lazer: {batch.Count} карт(ы)", ConsoleColor.Cyan);
            }
            else
            {
                ChronosUi.WriteLine($"  » Не удалось передать {batch.Count} карт(ы) в osu!lazer.", ConsoleColor.Red);
            }
            batch.Clear();
        }
    }

    private static async Task<bool> ImportBatchAsync(string lazerExe, List<string> files)
    {
        if (!await EnsureLazerRunningAsync(lazerExe))
            return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo(lazerExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(lazerExe)!,
                };
                foreach (var f in files) psi.ArgumentList.Add(f);

                using var p = Process.Start(psi)!;
                _ = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();

                var exited = await Task.Run(() => p.WaitForExit(TimeSpan.FromSeconds(20 + files.Count * 4)));
                if (!exited || p.ExitCode == 0)
                    return true;
            }
            catch (Exception ex)
            {
                ChronosUi.WriteLine($"  Ошибка запуска osu!lazer: {ex.Message}", ConsoleColor.Red);
            }
            await Task.Delay(2500);
        }
        return false;
    }

    private static async Task<bool> EnsureLazerRunningAsync(string lazerExe)
    {
        if (LazerDatabase.GetRunningLazerProcess(lazerExe) != null)
            return true;

        ChronosUi.WriteLine(
            _gameStartedByUs
                ? "  osu!lazer был закрыт, запускаю снова..."
                : "  osu!lazer не запущен — открываю игру для импорта карт...",
            ConsoleColor.Yellow);

        _gameStartedByUs = true;
        Process.Start(new ProcessStartInfo(lazerExe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(lazerExe)!,
        });

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(1000);
            var proc = LazerDatabase.GetRunningLazerProcess(lazerExe);
            if (proc != null)
            {
                try
                {
                    proc.Refresh();
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        await Task.Delay(7000);
                        ChronosUi.WriteLine("  osu!lazer запущен и готов к импорту.", ConsoleColor.Green);
                        return true;
                    }
                }
                catch { /* ignore */ }
            }
        }

        ChronosUi.WriteLine("  Не удалось дождаться запуска osu!lazer.", ConsoleColor.Red);
        return false;
    }

    private static readonly ConcurrentDictionary<string, (IPAddress[] IPs, DateTime Expiry)> DnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, IPAddress> WorkingEndpointCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Проверенные рабочие IP Cloudflare для osucollector.com (включая разные подсети на случай блокировки отдельных подсетей ТСПУ)
    private static readonly IPAddress[] CollectorFallbackIps =
    {
        IPAddress.Parse("104.21.18.168"),
        IPAddress.Parse("172.67.182.194"),
        IPAddress.Parse("162.159.135.42"),
        IPAddress.Parse("162.159.140.98"),
        IPAddress.Parse("172.66.0.96"),
    };

    private static bool IsUsablePublicIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback))
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT / заглушки провайдеров
        }

        return true;
    }

    private static async Task<IPAddress[]> QueryDohAsync(string host, CancellationToken ct)
    {
        string[] dohUrls =
        {
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A",
            $"https://dns.google/resolve?name={Uri.EscapeDataString(host)}&type=A",
        };

        foreach (var url in dohUrls)
        {
            try
            {
                using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
                using var dohClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2.5) };
                dohClient.DefaultRequestHeaders.Add("Accept", "application/dns-json");
                string json = await dohClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Answer", out var answer) && answer.ValueKind == JsonValueKind.Array)
                {
                    var ips = new List<IPAddress>();
                    foreach (var item in answer.EnumerateArray())
                    {
                        if (item.TryGetProperty("data", out var data) &&
                            IPAddress.TryParse(data.GetString(), out var ip) &&
                            IsUsablePublicIp(ip))
                        {
                            ips.Add(ip);
                        }
                    }
                    if (ips.Count > 0)
                        return ips.ToArray();
                }
            }
            catch { /* try next DoH */ }
        }

        return Array.Empty<IPAddress>();
    }

    private static bool IsCollectorHost(string host) =>
        string.Equals(host, "osucollector.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".osucollector.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<IPAddress[]> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return new[] { parsed };

        bool isCollector = IsCollectorHost(host);

        if (DnsCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            if (isCollector && cached.IPs.Length > 1)
            {
                int offset = (int)((uint)Volatile.Read(ref _collectorIpIndex) % (uint)cached.IPs.Length);
                if (offset > 0)
                    return cached.IPs.Skip(offset).Concat(cached.IPs.Take(offset)).ToArray();
            }
            return cached.IPs;
        }

        var result = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        void AddIps(IEnumerable<IPAddress> list)
        {
            foreach (var ip in list)
            {
                if (IsUsablePublicIp(ip) && seen.Add(ip))
                    result.Add(ip);
            }
        }

        // Для osucollector.com сразу добавляем проверенные IP Cloudflare без блокировки на DoH
        if (isCollector)
        {
            AddIps(CollectorFallbackIps);
        }

        // Системный DNS (строго приоритет IPv4 над IPv6, т.к. битый IPv6 у провайдеров РФ вызывает таймауты)
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(2500);
            var sysIps = await Dns.GetHostAddressesAsync(host, dnsCts.Token);
            AddIps(sysIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork));
            if (!isCollector)
                AddIps(sysIps.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6));
        }
        catch { /* ignore system DNS error */ }

        if (result.Count == 0)
        {
            var dohIps = await QueryDohAsync(host, ct);
            AddIps(dohIps);
        }

        if (result.Count > 0)
        {
            var arr = result.ToArray();
            DnsCache[host] = (arr, DateTime.UtcNow.AddMinutes(15));
            return arr;
        }

        return await Dns.GetHostAddressesAsync(host, ct);
    }

    private static async Task<Socket> ConnectSocketWithTimeoutAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await s.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
            return s;
        }
        catch
        {
            try { s.Dispose(); } catch { }
            throw;
        }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // Исключаем зависимость от сторонних прокси (и зависание при выключенном локальном прокси)
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 16,
            ConnectCallback = async (context, cancellationToken) =>
            {
                string host = context.DnsEndPoint.Host;
                int port = context.DnsEndPoint.Port;

                var ips = await ResolveHostAsync(host, cancellationToken);

                if (WorkingEndpointCache.TryGetValue(host, out var workingIp))
                {
                    ips = new[] { workingIp }
                        .Concat(ips.Where(i => !i.Equals(workingIp)))
                        .ToArray();
                }

                Socket? socket = null;
                IPAddress? connectedIp = null;
                Exception? lastEx = null;

                foreach (var ip in ips)
                {
                    try
                    {
                        socket = await ConnectSocketWithTimeoutAsync(ip, port, 3500, cancellationToken);
                        connectedIp = ip;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                    }
                }

                if (socket == null || connectedIp == null)
                    throw lastEx ?? new SocketException((int)SocketError.HostNotFound);

                WorkingEndpointCache[host] = connectedIp;
                var netStream = new NetworkStream(socket, ownsSocket: true);

                // Применяем TCP SNI фрагментацию для заблокированного ТСПУ osucollector.com,
                // а для зеркал карт используем прямое соединение без задержек и без конфликта с внешним zapret
                if (IsCollectorHost(host))
                    return new TcpSniFragmentStream(netStream, host);

                return netStream;
            }
        };

        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Accept", "*/*");
        return http;
    }

    /// <summary>
    /// Поток с TCP-сегментацией первого пакета TLS ClientHello прямо посередине SNI-домена.
    /// Не модифицирует заголовки TLS-записей и не вмешивается в ReadAsync, обеспечивая 100% совместимость с SslStream (.NET 8)
    /// и обход блокировок по SNI на ТСПУ без VPN, прокси и zapret.
    /// </summary>
    private sealed class TcpSniFragmentStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _host;
        private bool _firstWriteDone;

        public TcpSniFragmentStream(Stream inner, string host)
        {
            _inner = inner;
            _host = host;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_firstWriteDone)
            {
                _firstWriteDone = true;
                if (buffer.Length > 5 && buffer.Span[0] == 0x16)
                {
                    byte[] data = buffer.ToArray();
                    int splitPos = FindSniSplitOffset(data, _host);
                    if (splitPos > 4 && splitPos < data.Length - 1)
                    {
                        await _inner.WriteAsync(data.AsMemory(0, 2), cancellationToken);
                        await _inner.FlushAsync(cancellationToken);
                        await _inner.WriteAsync(data.AsMemory(2, splitPos - 2), cancellationToken);
                        await _inner.FlushAsync(cancellationToken);
                        await Task.Delay(15, cancellationToken);
                        await _inner.WriteAsync(data.AsMemory(splitPos), cancellationToken);
                        await _inner.FlushAsync(cancellationToken);
                        return;
                    }
                }
            }

            await _inner.WriteAsync(buffer, cancellationToken);
        }

        private static int FindSniSplitOffset(byte[] buf, string host)
        {
            if (!string.IsNullOrEmpty(host))
            {
                byte[] hostBytes = Encoding.ASCII.GetBytes(host.ToLowerInvariant());
                int idx = buf.AsSpan(5).IndexOf(hostBytes);
                if (idx >= 0)
                {
                    return 5 + idx + Math.Max(2, hostBytes.Length / 2);
                }
            }
            return Math.Clamp(buf.Length / 2, 6, Math.Max(6, buf.Length - 2));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
