using System.Text.Json;

namespace OsuCollectorLazer;

internal enum OsuTargetMode
{
    Lazer = 0,
    Stable = 1,
}

internal static class ChronosStorage
{
    public static string StorageDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CHRONOS");

    public static string ConfigPath => Path.Combine(StorageDir, "chronos.json");

    public static string GetHistoryPath(OsuTargetMode mode) =>
        Path.Combine(StorageDir, mode == OsuTargetMode.Stable ? "history_stable.txt" : "history.txt");

    public static void InitializeAndMigrate()
    {
        try
        {
            if (!Directory.Exists(StorageDir))
                Directory.CreateDirectory(StorageDir);

            // Ищем файлы в старых местах (рабочий стол, папка с программой) и переносим в хранилище %APPDATA%\CHRONOS
            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop)) candidateDirs.Add(desktop);

            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir)) candidateDirs.Add(baseDir);

            foreach (var dir in candidateDirs)
            {
                if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(StorageDir), StringComparison.OrdinalIgnoreCase))
                    continue;

                MigrateHistoryFile(Path.Combine(dir, "history.txt"), GetHistoryPath(OsuTargetMode.Lazer));
                MigrateHistoryFile(Path.Combine(dir, "history_stable.txt"), GetHistoryPath(OsuTargetMode.Stable));
                MigrateConfigFile(Path.Combine(dir, "chronos.json"), ConfigPath);
            }
        }
        catch { /* ignore migration failures */ }
    }

    private static void MigrateHistoryFile(string legacyPath, string targetPath)
    {
        try
        {
            if (!File.Exists(legacyPath)) return;

            var ids = new HashSet<int>();
            if (File.Exists(targetPath))
            {
                foreach (var line in File.ReadLines(targetPath))
                    if (int.TryParse(line.Trim(), out int id) && id > 0)
                        ids.Add(id);
            }

            foreach (var line in File.ReadLines(legacyPath))
                if (int.TryParse(line.Trim(), out int id) && id > 0)
                    ids.Add(id);

            File.WriteAllLines(targetPath, ids.Select(i => i.ToString()));

            // Удаляем старый файл с рабочего стола / папки приложения, чтобы не мусорить
            File.Delete(legacyPath);
        }
        catch { /* ignore */ }
    }

    private static void MigrateConfigFile(string legacyPath, string targetPath)
    {
        try
        {
            if (!File.Exists(legacyPath)) return;
            if (!File.Exists(targetPath))
            {
                File.Copy(legacyPath, targetPath, true);
            }
            File.Delete(legacyPath);
        }
        catch { /* ignore */ }
    }
}

internal sealed class ChronosConfig
{
    public OsuTargetMode TargetMode { get; set; } = OsuTargetMode.Lazer;
    public int Threads { get; set; } = 4;
    public bool NoVideo { get; set; } = false;
    public bool AutoCreateCollection { get; set; } = true;
    public int BatchSize { get; set; } = 25;
    public string? SaveDir { get; set; }
    public string? LazerExe { get; set; }
    public string? LazerDataDir { get; set; }
    public string? StableDir { get; set; }

    // osu! Official Account Auth
    public string? OsuAccessToken { get; set; }
    public string? OsuRefreshToken { get; set; }
    public DateTime? OsuTokenExpiry { get; set; }
    public string? OsuUsername { get; set; }
    public int? OsuUserId { get; set; }
    public bool OsuIsSupporter { get; set; }

    private static string ConfigPath => ChronosStorage.ConfigPath;

    public static ChronosConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<ChronosConfig>(json);
                if (cfg != null)
                {
                    cfg.Threads = Math.Clamp(cfg.Threads, 1, 16);
                    cfg.BatchSize = Math.Clamp(cfg.BatchSize, 1, 200);
                    return cfg;
                }
            }
        }
        catch { /* ignore broken config */ }
        return new ChronosConfig();
    }

    public void Save()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
        }
        catch { /* ignore */ }
    }
}

internal sealed class CliOptions
{
    public List<int> CollectionIds { get; } = new();
    public OsuTargetMode? TargetMode { get; set; }
    public int? Threads { get; set; }
    public bool? NoVideo { get; set; }
    public bool NoImport { get; set; }
    public bool Force { get; set; }
    public bool CollectionOnly { get; set; }
    public bool ListCollections { get; set; }
    public string? DeleteCollectionName { get; set; }
    public bool? CreateCollection { get; set; }
    public string? CustomCollectionName { get; set; }
    public string? SaveDir { get; set; }
    public string? LazerExe { get; set; }
    public string? StableDir { get; set; }
    public int? BatchSize { get; set; }
}

internal sealed record CollectorBeatmap(int Id, string Checksum);

internal sealed record CollectorSet(int Id, List<CollectorBeatmap> Beatmaps);

internal sealed record CollectorCollection(
    int Id,
    string Name,
    string Uploader,
    string Description,
    int BeatmapCount,
    int Favourites,
    List<CollectorSet> Sets)
{
    public List<int> SetIds => Sets.Select(s => s.Id).ToList();
}

internal sealed record PopularCollectionSummary(
    int Id,
    string Name,
    string Uploader,
    int BeatmapCount,
    int Favourites);

internal sealed record CollectionPageResult(
    List<PopularCollectionSummary> Collections,
    bool HasMore,
    int? NextCursor,
    int? TotalResults);

internal sealed record LazerCollectionInfo(
    Guid Id,
    string Name,
    int TotalHashes,
    int InstalledHashes,
    DateTimeOffset LastModified);

internal sealed class LazerLibrarySnapshot
{
    public HashSet<int> SetIds { get; } = new();
    public HashSet<int> BeatmapIds { get; } = new();
    public HashSet<string> Md5Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> BeatmapIdToMd5 { get; } = new();
    public Dictionary<int, List<string>> SetIdToMd5s { get; } = new();
    public List<LazerCollectionInfo> Collections { get; } = new();
    public long ScanTimeMs { get; set; }
    public string RealmPath { get; set; } = "";

    public bool HasSet(int setId) => SetIds.Contains(setId);

    public bool HasBeatmap(CollectorBeatmap bm) =>
        (!string.IsNullOrEmpty(bm.Checksum) && Md5Hashes.Contains(bm.Checksum)) ||
        (bm.Id > 0 && BeatmapIds.Contains(bm.Id));
}

/// <summary>Локальная история скачанных мапсетов.</summary>
internal sealed class History
{
    private readonly string _path;
    private readonly HashSet<int> _ids = new();
    private readonly object _lock = new();

    public History(string path)
    {
        _path = path;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                foreach (var line in File.ReadLines(path))
                    if (int.TryParse(line.Trim(), out int id) && id > 0) _ids.Add(id);
        }
        catch { /* ignore */ }
    }

    public int Count
    {
        get { lock (_lock) return _ids.Count; }
    }

    public bool Contains(int id)
    {
        lock (_lock) return _ids.Contains(id);
    }

    public void Add(int id)
    {
        if (string.IsNullOrEmpty(_path)) return;
        lock (_lock)
        {
            if (!_ids.Add(id)) return;
            try { File.AppendAllText(_path, id + Environment.NewLine); } catch { /* ignore */ }
        }
    }

    public int SyncFromRealm(IEnumerable<int> realmSetIds)
    {
        if (string.IsNullOrEmpty(_path)) return 0;
        lock (_lock)
        {
            int added = 0;
            var sb = new System.Text.StringBuilder();
            foreach (int id in realmSetIds)
            {
                if (id > 0 && _ids.Add(id))
                {
                    sb.AppendLine(id.ToString());
                    added++;
                }
            }
            if (added > 0)
            {
                try { File.AppendAllText(_path, sb.ToString()); } catch { /* ignore */ }
            }
            return added;
        }
    }
}
