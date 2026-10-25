using System.Diagnostics;
using System.Text;

namespace OsuCollectorLazer;

internal static class StableDatabase
{
    public static string? FindStableDir(string? custom = null)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (File.Exists(custom))
                return Path.GetDirectoryName(Path.GetFullPath(custom));
            if (Directory.Exists(custom))
                return Path.GetFullPath(custom);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            @"C:\osu!",
            Path.Combine(local, "osu!"),
            @"D:\osu!",
            @"E:\osu!",
            @"C:\Games\osu!",
            @"D:\Games\osu!",
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) &&
                (File.Exists(Path.Combine(dir, "osu!.db")) || File.Exists(Path.Combine(dir, "collection.db"))))
            {
                return dir;
            }
        }
        return null;
    }

    public static string? FindSongsDir(string? customStableDir = null)
    {
        string? stableDir = FindStableDir(customStableDir);
        if (stableDir == null) return null;

        try
        {
            string cfgFile = Path.Combine(stableDir, $"osu!.{Environment.UserName}.cfg");
            if (!File.Exists(cfgFile))
            {
                cfgFile = Directory.GetFiles(stableDir, "osu!.*.cfg")
                    .FirstOrDefault(f => !f.EndsWith("osu!.cfg", StringComparison.OrdinalIgnoreCase)) ?? "";
            }

            if (File.Exists(cfgFile))
            {
                foreach (var line in File.ReadAllLines(cfgFile))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            string raw = trimmed[(eq + 1)..].Trim();
                            if (!string.IsNullOrEmpty(raw))
                            {
                                string resolved = Path.IsPathRooted(raw) ? raw : Path.Combine(stableDir, raw);
                                if (Directory.Exists(resolved))
                                    return Path.GetFullPath(resolved);
                            }
                        }
                    }
                }
            }
        }
        catch { /* ignore */ }

        string defaultSongs = Path.Combine(stableDir, "Songs");
        return Directory.Exists(defaultSongs) ? defaultSongs : stableDir;
    }

    public static Process? GetRunningStableProcess(string? customStableDir = null)
    {
        string? stableDir = FindStableDir(customStableDir);
        foreach (var p in Process.GetProcessesByName("osu!"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* ignore */ }
            if (path == null) continue;

            if (stableDir != null && path.StartsWith(stableDir, StringComparison.OrdinalIgnoreCase))
                return p;
            if (stableDir == null && !path.Contains("osulazer", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    public static LazerLibrarySnapshot? ScanLibrary(ChronosConfig config, out string? error)
    {
        error = null;
        string? stableDir = FindStableDir(config.StableDir);
        if (stableDir == null)
        {
            error = "Папка osu!stable не найдена (укажите путь в настройках).";
            return null;
        }

        string osuDbPath = Path.Combine(stableDir, "osu!.db");
        string colDbPath = Path.Combine(stableDir, "collection.db");
        var sw = Stopwatch.StartNew();
        var snap = new LazerLibrarySnapshot { RealmPath = osuDbPath };

        try
        {
            // 1. Читаем osu!.db
            if (File.Exists(osuDbPath))
            {
                ParseOsuDb(osuDbPath, snap);
            }

            // 2. Дополнительно проверяем папки и .osz в Songs (на случай если пользователь ещё не нажал F5 в игре)
            string? songsDir = FindSongsDir(config.StableDir);
            if (songsDir != null && Directory.Exists(songsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(songsDir))
                {
                    string name = Path.GetFileName(dir);
                    int space = name.IndexOf(' ');
                    string numPart = space > 0 ? name[..space] : name;
                    if (int.TryParse(numPart, out int setId) && setId > 0)
                        snap.SetIds.Add(setId);
                }
                foreach (var file in Directory.EnumerateFiles(songsDir, "*.osz"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int space = name.IndexOf(' ');
                    string numPart = space > 0 ? name[..space] : name;
                    if (int.TryParse(numPart, out int setId) && setId > 0)
                        snap.SetIds.Add(setId);
                }
            }

            // 3. Читаем collection.db
            if (File.Exists(colDbPath))
            {
                var (_, cols) = ReadCollectionDb(colDbPath);
                DateTimeOffset mod = new FileInfo(colDbPath).LastWriteTimeUtc;
                foreach (var c in cols)
                {
                    int installed = c.Hashes.Count(h => !string.IsNullOrEmpty(h) && snap.Md5Hashes.Contains(h));
                    snap.Collections.Add(new LazerCollectionInfo(Guid.NewGuid(), c.Name, c.Hashes.Count, installed, mod));
                }
            }

            snap.Collections.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            snap.ScanTimeMs = sw.ElapsedMilliseconds;
            return snap;
        }
        catch (Exception ex)
        {
            error = $"Ошибка чтения базы osu!stable: {ex.Message}";
            return null;
        }
    }

    public static bool CreateOrUpdateCollection(
        ChronosConfig config,
        string collectionName,
        IEnumerable<string> md5Hashes,
        bool replaceExisting,
        out int addedCount,
        out int totalCount,
        out bool createdNew,
        out string? error)
    {
        addedCount = 0;
        totalCount = 0;
        createdNew = false;
        error = null;

        string? stableDir = FindStableDir(config.StableDir);
        if (stableDir == null)
        {
            error = "Папка osu!stable не найдена.";
            return false;
        }

        string colDbPath = Path.Combine(stableDir, "collection.db");
        EnsureBackup(colDbPath);

        try
        {
            int version = 20260711;
            var collections = new List<(string Name, List<string> Hashes)>();

            if (File.Exists(colDbPath))
            {
                (version, collections) = ReadCollectionDb(colDbPath);
            }

            int idx = collections.FindIndex(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase));
            List<string> targetHashes;

            if (idx < 0)
            {
                targetHashes = new List<string>();
                collections.Add((collectionName, targetHashes));
                createdNew = true;
            }
            else
            {
                targetHashes = collections[idx].Hashes;
                if (replaceExisting)
                    targetHashes.Clear();
            }

            var existing = new HashSet<string>(targetHashes, StringComparer.OrdinalIgnoreCase);
            foreach (var raw in md5Hashes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim().ToLowerInvariant();
                if (existing.Add(h))
                {
                    targetHashes.Add(h);
                    addedCount++;
                }
            }

            totalCount = targetHashes.Count;
            WriteCollectionDb(colDbPath, version, collections);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool DeleteCollection(ChronosConfig config, string collectionName, out string? error)
    {
        error = null;
        string? stableDir = FindStableDir(config.StableDir);
        if (stableDir == null) { error = "Папка osu!stable не найдена."; return false; }
        string colDbPath = Path.Combine(stableDir, "collection.db");
        if (!File.Exists(colDbPath)) { error = "Файл collection.db не найден."; return false; }

        EnsureBackup(colDbPath);
        try
        {
            var (version, collections) = ReadCollectionDb(colDbPath);
            int removed = collections.RemoveAll(c => string.Equals(c.Name, collectionName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) { error = "Коллекция не найдена."; return false; }
            WriteCollectionDb(colDbPath, version, collections);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool RenameCollection(ChronosConfig config, string oldName, string newName, out string? error)
    {
        error = null;
        string? stableDir = FindStableDir(config.StableDir);
        if (stableDir == null) { error = "Папка osu!stable не найдена."; return false; }
        string colDbPath = Path.Combine(stableDir, "collection.db");
        if (!File.Exists(colDbPath)) { error = "Файл collection.db не найден."; return false; }

        EnsureBackup(colDbPath);
        try
        {
            var (version, collections) = ReadCollectionDb(colDbPath);
            int idx = collections.FindIndex(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { error = "Коллекция не найдена."; return false; }
            collections[idx] = (newName, collections[idx].Hashes);
            WriteCollectionDb(colDbPath, version, collections);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool BackupCollectionDbManual(ChronosConfig config, out string? backupPath, out string? error)
    {
        backupPath = null;
        error = null;
        string? stableDir = FindStableDir(config.StableDir);
        if (stableDir == null) { error = "Папка osu!stable не найдена."; return false; }
        string colDbPath = Path.Combine(stableDir, "collection.db");
        if (!File.Exists(colDbPath)) { error = "Файл collection.db не найден."; return false; }

        try
        {
            string dir = Path.Combine(stableDir, "chronos_backups");
            Directory.CreateDirectory(dir);
            backupPath = Path.Combine(dir, $"collection_{DateTime.Now:yyyyMMdd_HHmmss}.db.bak");
            File.Copy(colDbPath, backupPath, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ===================================================================== Binary Parsers (osu!.db & collection.db)

    private static void ParseOsuDb(string path, LazerLibrarySnapshot snap)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = new byte[fs.Length];
            _ = fs.Read(data, 0, data.Length);
        }

        int pos = 0;

        int ReadInt()
        {
            int v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }

        void SkipStr()
        {
            byte b = data[pos++];
            if (b == 0) return;
            int shift = 0, len = 0;
            while (true)
            {
                byte by = data[pos++];
                len |= (by & 0x7F) << shift;
                if ((by & 0x80) == 0) break;
                shift += 7;
            }
            pos += len;
        }

        string ReadStr()
        {
            byte b = data[pos++];
            if (b == 0) return "";
            int shift = 0, len = 0;
            while (true)
            {
                byte by = data[pos++];
                len |= (by & 0x7F) << shift;
                if ((by & 0x80) == 0) break;
                shift += 7;
            }
            string s = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            return s;
        }

        int ver = ReadInt();
        _ = ReadInt(); // folder count
        pos += 1 + 8;  // account unlocked + unlock date
        SkipStr();     // player name
        int numBeatmaps = ReadInt();

        for (int i = 0; i < numBeatmaps && pos < data.Length - 4; i++)
        {
            if (ver < 20191106) pos += 4;
            for (int s = 0; s < 7; s++) SkipStr();
            string md5 = ReadStr();
            SkipStr(); // .osu filename

            pos += 1 + 2 + 2 + 2 + 8;
            pos += ver >= 20140609 ? 16 : 4;
            pos += 8; // slider velocity

            if (ver >= 20140609)
            {
                int pairSize = ver >= 20250107 ? 10 : 14;
                for (int m = 0; m < 4; m++)
                {
                    int n = ReadInt();
                    if (n > 0)
                    {
                        // Авто-проверка тега типа (0x0c = float -> 10 байт, 0x0d = double -> 14 байт)
                        if (pos + 5 < data.Length)
                        {
                            byte valTag = data[pos + 5];
                            if (valTag == 0x0c) pairSize = 10;
                            else if (valTag == 0x0d) pairSize = 14;
                        }
                        pos += n * pairSize;
                    }
                }
            }

            pos += 12; // drain, total, preview
            int nTp = ReadInt();
            pos += nTp * 17;

            int bmId = ReadInt();
            int setId = ReadInt();
            pos += 4 + 4 + 2 + 4 + 1; // threadId, grades, localOffset, stackLeniency, mode

            SkipStr(); // source
            SkipStr(); // tags
            pos += 2;  // online offset
            SkipStr(); // font
            pos += 1 + 8 + 1; // unplayed, lastPlayed, isOsz2
            SkipStr(); // folder name

            // В старых версиях хвост 14 байт (8 + 5 + 1), в новых 18 байт (8 + 5 + 4 + 1)
            if (pos + 18 <= data.Length && (data[pos + 18] == 0x0b || data[pos + 18] == 0x00) &&
                !(data[pos + 14] == 0x0b && pos + 15 < data.Length && data[pos + 15] > 0 && data[pos + 15] < 80))
            {
                pos += 18;
            }
            else
            {
                pos += 18;
            }

            if (setId > 0) snap.SetIds.Add(setId);
            if (bmId > 0) snap.BeatmapIds.Add(bmId);
            if (!string.IsNullOrEmpty(md5))
            {
                snap.Md5Hashes.Add(md5);
                if (bmId > 0) snap.BeatmapIdToMd5[bmId] = md5;
                if (setId > 0)
                {
                    if (!snap.SetIdToMd5s.TryGetValue(setId, out var list))
                    {
                        list = new List<string>();
                        snap.SetIdToMd5s[setId] = list;
                    }
                    list.Add(md5);
                }
            }
        }
    }

    private static (int Version, List<(string Name, List<string> Hashes)> Collections) ReadCollectionDb(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        int version = br.ReadInt32();
        int count = br.ReadInt32();
        var list = new List<(string Name, List<string> Hashes)>(Math.Max(0, count));

        for (int i = 0; i < count; i++)
        {
            string name = ReadOsuString(br);
            int bmCount = br.ReadInt32();
            var hashes = new List<string>(Math.Max(0, bmCount));
            for (int j = 0; j < bmCount; j++)
            {
                string h = ReadOsuString(br);
                if (!string.IsNullOrEmpty(h))
                    hashes.Add(h.ToLowerInvariant());
            }
            list.Add((name, hashes));
        }

        return (version, list);
    }

    private static void WriteCollectionDb(string path, int version, List<(string Name, List<string> Hashes)> collections)
    {
        string tmp = path + ".chronos.tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8))
        {
            bw.Write(version);
            bw.Write(collections.Count);
            foreach (var (name, hashes) in collections)
            {
                WriteOsuString(bw, name);
                bw.Write(hashes.Count);
                foreach (var h in hashes)
                    WriteOsuString(bw, h);
            }
        }
        File.Move(tmp, path, true);
    }

    private static string ReadOsuString(BinaryReader br)
    {
        byte tag = br.ReadByte();
        return tag == 0x0b ? br.ReadString() : "";
    }

    private static void WriteOsuString(BinaryWriter bw, string? value)
    {
        if (value == null)
        {
            bw.Write((byte)0x00);
        }
        else
        {
            bw.Write((byte)0x0b);
            bw.Write(value);
        }
    }

    private static void EnsureBackup(string colDbPath)
    {
        try
        {
            if (!File.Exists(colDbPath)) return;
            string dir = Path.Combine(Path.GetDirectoryName(colDbPath)!, "chronos_backups");
            Directory.CreateDirectory(dir);
            string autoBak = Path.Combine(dir, "collection_auto.db.bak");
            if (!File.Exists(autoBak) || File.GetLastWriteTimeUtc(autoBak) < DateTime.UtcNow.AddHours(-12))
                File.Copy(colDbPath, autoBak, true);
        }
        catch { /* ignore */ }
    }
}
