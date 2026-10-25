using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Realms;

namespace OsuCollectorLazer;

internal static class LazerDatabase
{
    private static bool _resolverInitialized;
    private static string? _resolvedLazerDir;

    // ===================================================================== Unified Router (Lazer / Stable)

    public static LazerLibrarySnapshot? ScanActiveLibrary(ChronosConfig config, out string? error) =>
        config.TargetMode == OsuTargetMode.Stable
            ? StableDatabase.ScanLibrary(config, out error)
            : ScanLibrary(config, out error);

    public static bool CreateOrUpdateActiveCollection(
        ChronosConfig config,
        string collectionName,
        IEnumerable<string> md5Hashes,
        bool replaceExisting,
        out int addedCount,
        out int totalCount,
        out bool createdNew,
        out string? error) =>
        config.TargetMode == OsuTargetMode.Stable
            ? StableDatabase.CreateOrUpdateCollection(config, collectionName, md5Hashes, replaceExisting, out addedCount, out totalCount, out createdNew, out error)
            : CreateOrUpdateCollection(config, collectionName, md5Hashes, replaceExisting, out addedCount, out totalCount, out createdNew, out error);

    public static bool DeleteActiveCollection(ChronosConfig config, LazerCollectionInfo col, out string? error) =>
        config.TargetMode == OsuTargetMode.Stable
            ? StableDatabase.DeleteCollection(config, col.Name, out error)
            : DeleteCollection(config, col.Id, out error);

    public static bool RenameActiveCollection(ChronosConfig config, LazerCollectionInfo col, string newName, out string? error) =>
        config.TargetMode == OsuTargetMode.Stable
            ? StableDatabase.RenameCollection(config, col.Name, newName, out error)
            : RenameCollection(config, col.Id, newName, out error);

    public static bool BackupActiveManual(ChronosConfig config, out string? backupPath, out string? error) =>
        config.TargetMode == OsuTargetMode.Stable
            ? StableDatabase.BackupCollectionDbManual(config, out backupPath, out error)
            : BackupRealmManual(config, out backupPath, out error);

    // ===================================================================== Lazer Paths & Resolver

    public static void InitializeNativeResolver(string? customLazerExe = null)
    {
        string? exe = FindLazerExe(customLazerExe);
        if (exe != null)
            _resolvedLazerDir = Path.GetDirectoryName(exe);

        if (_resolverInitialized) return;
        _resolverInitialized = true;

        NativeLibrary.SetDllImportResolver(typeof(Realm).Assembly, (name, _, _) =>
        {
            string dllName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string?[] dirs =
            {
                _resolvedLazerDir,
                Path.Combine(local, "osulazer", "current"),
                AppContext.BaseDirectory,
            };

            foreach (var dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, dllName);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    public static string? FindLazerExe(string? custom = null)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            if (Directory.Exists(custom)) custom = Path.Combine(custom, "osu!.exe");
            return File.Exists(custom) ? Path.GetFullPath(custom) : null;
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(local, "osulazer", "current", "osu!.exe"),
            Path.Combine(local, "osulazer", "osu!.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        string root = Path.Combine(local, "osulazer");
        if (Directory.Exists(root))
        {
            var app = Directory.GetDirectories(root, "app-*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "osu!.exe"))
                .FirstOrDefault(File.Exists);
            if (app != null) return app;
        }
        return null;
    }

    public static string? FindDataDir(string? customDataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(customDataDir))
        {
            if (File.Exists(customDataDir) && customDataDir.EndsWith(".realm", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetFullPath(customDataDir));
            if (Directory.Exists(customDataDir))
                return Path.GetFullPath(customDataDir);
        }

        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");
        string storageIni = Path.Combine(defaultDir, "storage.ini");
        if (File.Exists(storageIni))
        {
            try
            {
                foreach (var line in File.ReadAllLines(storageIni))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("FullPath", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            string custom = trimmed[(eq + 1)..].Trim();
                            if (!string.IsNullOrEmpty(custom) && Directory.Exists(custom))
                                return custom;
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        return Directory.Exists(defaultDir) ? defaultDir : null;
    }

    public static string? FindRealmPath(string? customDataDir = null)
    {
        string? dir = FindDataDir(customDataDir);
        if (dir == null) return null;
        string realmPath = Path.Combine(dir, "client.realm");
        return File.Exists(realmPath) ? realmPath : null;
    }

    public static Process? GetRunningLazerProcess(string? lazerExe)
    {
        string? exe = FindLazerExe(lazerExe);
        string? lazerRoot = null;
        if (exe != null)
        {
            string lazerDir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
            lazerRoot = Directory.GetParent(lazerDir)?.FullName ?? lazerDir;
        }

        foreach (var p in Process.GetProcessesByName("osu!"))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* ignore */ }
            if (path != null && lazerRoot != null && path.StartsWith(lazerRoot, StringComparison.OrdinalIgnoreCase))
                return p;
            if (path != null && path.Contains("osulazer", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    // ===================================================================== Lazer client.realm Scan

    public static LazerLibrarySnapshot? ScanLibrary(ChronosConfig config, out string? error)
    {
        error = null;
        InitializeNativeResolver(config.LazerExe);

        string? realmPath = FindRealmPath(config.LazerDataDir);
        if (realmPath == null)
        {
            error = "Файл client.realm не найден в папке osu!lazer.";
            return null;
        }

        var sw = Stopwatch.StartNew();
        string? tempCopy = null;

        try
        {
            Realm realm;
            try
            {
                realm = Realm.GetInstance(new RealmConfiguration(realmPath) { IsDynamic = true });
            }
            catch
            {
                tempCopy = Path.Combine(Path.GetTempPath(), $"chronos_scan_{Guid.NewGuid():N}.realm");
                CopyFileShared(realmPath, tempCopy);
                realm = Realm.GetInstance(new RealmConfiguration(tempCopy) { IsDynamic = true });
            }

            using (realm)
            {
                var snap = new LazerLibrarySnapshot { RealmPath = realmPath };

                foreach (IRealmObjectBase s in realm.DynamicApi.All("BeatmapSet"))
                {
                    if (s.DynamicApi.Get<bool>("DeletePending"))
                        continue;

                    int setOnlineId = s.DynamicApi.Get<int>("OnlineID");
                    if (setOnlineId > 0)
                        snap.SetIds.Add(setOnlineId);

                    var beatmaps = s.DynamicApi.GetList<IRealmObjectBase>("Beatmaps");
                    List<string>? setMd5List = null;
                    if (setOnlineId > 0)
                    {
                        if (!snap.SetIdToMd5s.TryGetValue(setOnlineId, out setMd5List))
                        {
                            setMd5List = new List<string>();
                            snap.SetIdToMd5s[setOnlineId] = setMd5List;
                        }
                    }

                    foreach (var b in beatmaps)
                    {
                        string? md5 = b.DynamicApi.Get<string?>("MD5Hash");
                        string? onlineMd5 = b.DynamicApi.Get<string?>("OnlineMD5Hash");
                        int bmOnlineId = b.DynamicApi.Get<int>("OnlineID");

                        if (!string.IsNullOrEmpty(md5))
                        {
                            snap.Md5Hashes.Add(md5);
                            setMd5List?.Add(md5);
                            if (bmOnlineId > 0)
                                snap.BeatmapIdToMd5[bmOnlineId] = md5;
                        }
                        if (!string.IsNullOrEmpty(onlineMd5))
                        {
                            snap.Md5Hashes.Add(onlineMd5);
                        }
                        if (bmOnlineId > 0)
                        {
                            snap.BeatmapIds.Add(bmOnlineId);
                        }
                    }
                }

                foreach (IRealmObjectBase c in realm.DynamicApi.All("BeatmapCollection"))
                {
                    Guid id = c.DynamicApi.Get<Guid>("ID");
                    string name = c.DynamicApi.Get<string?>("Name") ?? "(без названия)";
                    DateTimeOffset mod = c.DynamicApi.Get<DateTimeOffset>("LastModified");
                    var hashes = c.DynamicApi.GetList<string>("BeatmapMD5Hashes");

                    int total = hashes.Count;
                    int installed = 0;
                    foreach (var h in hashes)
                    {
                        if (!string.IsNullOrEmpty(h) && snap.Md5Hashes.Contains(h))
                            installed++;
                    }
                    snap.Collections.Add(new LazerCollectionInfo(id, name, total, installed, mod));
                }

                snap.Collections.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                sw.Stop();
                snap.ScanTimeMs = sw.ElapsedMilliseconds;
                return snap;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (tempCopy != null)
            {
                try { File.Delete(tempCopy); } catch { /* ignore */ }
                try { File.Delete(tempCopy + ".lock"); } catch { /* ignore */ }
            }
        }
    }

    // ===================================================================== Collection Hashes Resolution

    public static List<string> ResolveCollectionHashes(
        CollectorCollection col,
        LazerLibrarySnapshot? snap,
        Dictionary<int, List<string>>? downloadedOszMd5s,
        bool onlyInstalledInGame)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddHash(string? h)
        {
            if (!string.IsNullOrWhiteSpace(h) && seen.Add(h))
                result.Add(h.ToLowerInvariant());
        }

        foreach (var set in col.Sets)
        {
            // Всегда добавляем все сложности мапсета
            if (snap != null && snap.SetIdToMd5s.TryGetValue(set.Id, out var localDiffs))
            {
                foreach (var h in localDiffs) AddHash(h);
            }

            if (downloadedOszMd5s != null && downloadedOszMd5s.TryGetValue(set.Id, out var oszDiffs))
            {
                foreach (var h in oszDiffs) AddHash(h);
            }

            if (!onlyInstalledInGame)
            {
                foreach (var bm in set.Beatmaps)
                    AddHash(bm.Checksum);
            }
        }

        return result;
    }

    /// <summary>
    /// Извлекает все MD5-хэши .osu файлов из скачанного .osz архива без модификации архива.
    /// </summary>
    public static List<string> ProcessDownloadedOsz(string oszPath)
    {
        var allMd5s = new List<string>();
        try
        {
            using var fs = new FileStream(oszPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var es = entry.Open();
                byte[] hash = MD5.HashData(es);
                allMd5s.Add(Convert.ToHexString(hash).ToLowerInvariant());
            }
        }
        catch { /* ignore malformed zip */ }

        return allMd5s;
    }

    // ===================================================================== Lazer client.realm Write

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

        InitializeNativeResolver(config.LazerExe);
        string? realmPath = FindRealmPath(config.LazerDataDir);
        if (realmPath == null)
        {
            error = "Файл client.realm не найден.";
            return false;
        }

        EnsureDailyBackup(realmPath);

        try
        {
            using var realm = Realm.GetInstance(new RealmConfiguration(realmPath) { IsDynamic = true });

            int localAdded = 0;
            int localTotal = 0;
            bool localCreated = false;

            realm.Write(() =>
            {
                IRealmObjectBase? target = null;
                foreach (IRealmObjectBase c in realm.DynamicApi.All("BeatmapCollection"))
                {
                    string? name = c.DynamicApi.Get<string?>("Name");
                    if (string.Equals(name, collectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = c;
                        break;
                    }
                }

                if (target == null)
                {
                    target = realm.DynamicApi.CreateObject("BeatmapCollection", Guid.NewGuid());
                    target.DynamicApi.Set("Name", collectionName);
                    localCreated = true;
                }

                var list = target.DynamicApi.GetList<string>("BeatmapMD5Hashes");
                if (replaceExisting)
                    list.Clear();

                var existing = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
                foreach (var raw in md5Hashes)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string h = raw.Trim().ToLowerInvariant();
                    if (existing.Add(h))
                    {
                        list.Add(h);
                        localAdded++;
                    }
                }

                target.DynamicApi.Set("LastModified", DateTimeOffset.UtcNow);
                localTotal = list.Count;
            });

            addedCount = localAdded;
            totalCount = localTotal;
            createdNew = localCreated;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool DeleteCollection(ChronosConfig config, Guid id, out string? error)
    {
        error = null;
        InitializeNativeResolver(config.LazerExe);
        string? realmPath = FindRealmPath(config.LazerDataDir);
        if (realmPath == null) { error = "Файл client.realm не найден."; return false; }

        EnsureDailyBackup(realmPath);

        try
        {
            using var realm = Realm.GetInstance(new RealmConfiguration(realmPath) { IsDynamic = true });
            bool found = false;
            realm.Write(() =>
            {
                foreach (IRealmObjectBase c in realm.DynamicApi.All("BeatmapCollection"))
                {
                    if (c.DynamicApi.Get<Guid>("ID") == id)
                    {
                        realm.Remove(c);
                        found = true;
                        break;
                    }
                }
            });
            if (!found) error = "Коллекция не найдена в базе.";
            return found;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool RenameCollection(ChronosConfig config, Guid id, string newName, out string? error)
    {
        error = null;
        InitializeNativeResolver(config.LazerExe);
        string? realmPath = FindRealmPath(config.LazerDataDir);
        if (realmPath == null) { error = "Файл client.realm не найден."; return false; }

        EnsureDailyBackup(realmPath);

        try
        {
            using var realm = Realm.GetInstance(new RealmConfiguration(realmPath) { IsDynamic = true });
            bool found = false;
            realm.Write(() =>
            {
                foreach (IRealmObjectBase c in realm.DynamicApi.All("BeatmapCollection"))
                {
                    if (c.DynamicApi.Get<Guid>("ID") == id)
                    {
                        c.DynamicApi.Set("Name", newName);
                        c.DynamicApi.Set("LastModified", DateTimeOffset.UtcNow);
                        found = true;
                        break;
                    }
                }
            });
            if (!found) error = "Коллекция не найдена в базе.";
            return found;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool BackupRealmManual(ChronosConfig config, out string? backupPath, out string? error)
    {
        backupPath = null;
        error = null;
        string? realmPath = FindRealmPath(config.LazerDataDir);
        if (realmPath == null) { error = "Файл client.realm не найден."; return false; }

        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(realmPath)!, "chronos_backups");
            Directory.CreateDirectory(dir);
            backupPath = Path.Combine(dir, $"client_{DateTime.Now:yyyyMMdd_HHmmss}.realm.bak");
            CopyFileShared(realmPath, backupPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnsureDailyBackup(string realmPath)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(realmPath)!, "chronos_backups");
            Directory.CreateDirectory(dir);
            string autoBak = Path.Combine(dir, "client_auto.realm.bak");
            if (!File.Exists(autoBak) || File.GetLastWriteTimeUtc(autoBak) < DateTime.UtcNow.AddHours(-12))
                CopyFileShared(realmPath, autoBak);
        }
        catch { /* ignore */ }
    }

    private static void CopyFileShared(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }
}
