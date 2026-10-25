using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OsuCollectorLazer;

internal static class Program
{
    private static readonly Regex CollectionUrlRegex = new(@"collections/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static LazerLibrarySnapshot? _snap;
    private static string? _snapError;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!Console.IsInputRedirected)
            Console.InputEncoding = Encoding.UTF8;
        ChronosUi.SetWindowTitle();

        ChronosStorage.InitializeAndMigrate();
        var config = ChronosConfig.Load();
        LazerDatabase.InitializeNativeResolver(config.LazerExe);

        string tempDir = Path.Combine(Path.GetTempPath(), "chronos-osu-lazer");
        Directory.CreateDirectory(tempDir);
        CleanupOldTemp(tempDir);

        var history = GetHistoryForMode(config);

        if (args.Length > 0)
        {
            return await RunCliModeAsync(args, config, tempDir, history);
        }

        await RunInteractiveMenuAsync(config, tempDir);
        return 0;
    }

    private static History GetHistoryForMode(ChronosConfig config)
    {
        return new History(ChronosStorage.GetHistoryPath(config.TargetMode));
    }

    private static void RefreshSnapshot(ChronosConfig config, History? history = null)
    {
        _snap = LazerDatabase.ScanActiveLibrary(config, out _snapError);
        if (_snap != null && history != null)
            history.SyncFromRealm(_snap.SetIds);
    }

    private static string GameLabel(ChronosConfig config) =>
        config.TargetMode == OsuTargetMode.Stable ? "osu!stable" : "osu!lazer";

    // ===================================================================== Interactive Menu

    private static async Task RunInteractiveMenuAsync(ChronosConfig config, string tempDir)
    {
        var history = GetHistoryForMode(config);
        RefreshSnapshot(config, history);

        while (true)
        {
            ChronosUi.DrawBanner(config, _snap, _snapError);

            string currentGame = config.TargetMode == OsuTargetMode.Stable ? "OSU!STABLE" : "OSU!LAZER";
            string otherGame = config.TargetMode == OsuTargetMode.Stable ? "OSU!LAZER" : "OSU!STABLE";

            string authLabel = string.IsNullOrEmpty(config.OsuUsername)
                ? "Войти в аккаунт osu! (официальный OAuth)"
                : $"Аккаунт osu!: [{config.OsuUsername}] (Выйти / сменить)";

            int choice = ChronosUi.SelectMenu(
                "ЧТО ХОТИТЕ СДЕЛАТЬ?",
                new[]
                {
                    $"Скачать коллекцию по ссылке или ID (в {GameLabel(config)})",
                    $"Создать / почистить коллекцию в {GameLabel(config)} без скачивания",
                    "Каталог и поиск коллекций",
                    $"Мои коллекции в {GameLabel(config)}",
                    $"Переключить игру: [ Сейчас: {currentGame}  ──►  Сменить на {otherGame} ]",
                    authLabel,
                    "Настройки",
                    "Выход",
                });

            switch (choice)
            {
                case 0:
                    await InteractiveDownloadCollectionAsync(config, tempDir, history);
                    break;
                case 1:
                    await InteractiveCreateCollectionOnlyAsync(config);
                    break;
                case 2:
                    await InteractiveCatalogAndSearchAsync(config, tempDir, history);
                    break;
                case 3:
                    InteractiveManageCollections(config);
                    break;
                case 4:
                    config.TargetMode = config.TargetMode == OsuTargetMode.Stable
                        ? OsuTargetMode.Lazer
                        : OsuTargetMode.Stable;
                    config.Save();
                    history = GetHistoryForMode(config);
                    RefreshSnapshot(config, history);
                    break;
                case 5:
                    if (string.IsNullOrEmpty(config.OsuUsername))
                    {
                        await OsuAuthService.LoginInteractiveAsync(config);
                    }
                    else
                    {
                        int authChoice = ChronosUi.SelectMenu(
                            $"Вы вошли как {config.OsuUsername}:",
                            new[] { "Выйти из аккаунта", "Переподключить заново", "Назад" });
                        if (authChoice == 0)
                            OsuAuthService.Logout(config);
                        else if (authChoice == 1)
                            await OsuAuthService.LoginInteractiveAsync(config);
                    }
                    break;
                case 6:
                    InteractiveSettingsMenu(config);
                    history = GetHistoryForMode(config);
                    RefreshSnapshot(config, history);
                    break;
                case 7:
                    return;
            }
        }
    }

    private static async Task InteractiveDownloadCollectionAsync(
        ChronosConfig config,
        string tempDir,
        History history)
    {
        Console.WriteLine();
        ChronosUi.WriteLine("  Вставьте ссылку (например, https://osucollector.com/collections/44) или просто номер 44:", ConsoleColor.Gray);
        string input = ChronosUi.Prompt("Ссылка или ID коллекции");
        var ids = ParseCollectionIdsFromText(input);
        if (ids.Count == 0)
        {
            ChronosUi.WriteLine("  Не удалось распознать ссылку или номер коллекции.", ConsoleColor.Yellow);
            ChronosUi.WaitEnter();
            return;
        }

        RefreshSnapshot(config, history);

        foreach (int id in ids)
        {
            await HandleSingleCollectionInteractiveAsync(id, config, tempDir, history);
        }

        ChronosUi.WaitEnter();
    }

    private static async Task HandleSingleCollectionInteractiveAsync(
        int collectionId,
        ChronosConfig config,
        string tempDir,
        History history)
    {
        Console.WriteLine();
        ChronosUi.WriteLine($"  Проверяю коллекцию #{collectionId}...", ConsoleColor.Cyan);

        CollectorCollection col;
        try
        {
            col = await CollectorService.FetchCollectionAsync(collectionId);
        }
        catch (Exception ex)
        {
            ChronosUi.WriteLine($"  ✖ Ошибка: {ex.Message}", ConsoleColor.Red);
            return;
        }

        PrintCollectionAnalysis(col, config, _snap, history, out var missingSetIds, out int installedSets, out int installedDiffs, out int totalDiffs);

        // Если ВСЕ карты уже есть в игре
        if (missingSetIds.Count == 0)
        {
            ChronosUi.WriteLine($"  ★ Все карты этой коллекции уже есть у тебя в {GameLabel(config)}!", ConsoleColor.Green);

            int act = ChronosUi.SelectMenu(
                $"Что сделать с коллекцией «{col.Name}»?",
                new[]
                {
                    $"Создать / обновить коллекцию «{col.Name}» в {GameLabel(config)}",
                    "Задать своё название для коллекции",
                    "Перекачать все карты заново",
                    "Назад",
                });

            if (act == 0)
            {
                CreateCollectionInGameUI(col, col.Name, config, _snap, null, onlyInstalledInGame: false);
                RefreshSnapshot(config);
            }
            else if (act == 1)
            {
                string customName = ChronosUi.Prompt("Введите название коллекции", col.Name);
                CreateCollectionInGameUI(col, customName, config, _snap, null, onlyInstalledInGame: false);
                RefreshSnapshot(config);
            }
            else if (act == 2)
            {
                await ExecuteDownloadAndCollectionFlowAsync(
                    col, col.SetIds, createCollection: true, col.Name, config, noImport: false, tempDir, history);
                RefreshSnapshot(config);
            }
            return;
        }

        // Если часть или все карты нужно докачать
        var menuItems = new List<string>
        {
            $"Начать: скачать недостающие карты ({missingSetIds.Count} шт.) и создать коллекцию «{col.Name}» в {GameLabel(config)}",
        };
        if (installedSets > 0)
        {
            menuItems.Add($"Не качать — сразу создать коллекцию «{col.Name}» из уже имеющихся у тебя карт");
        }
        menuItems.Add($"Только скачать недостающие карты ({missingSetIds.Count} шт.) без создания коллекции");
        menuItems.Add("Назад");

        int sel = ChronosUi.SelectMenu($"Выберите действие для «{col.Name}»:", menuItems);

        if (sel == 0)
        {
            await ExecuteDownloadAndCollectionFlowAsync(
                col, missingSetIds, createCollection: true, col.Name, config, noImport: false, tempDir, history);
            RefreshSnapshot(config);
        }
        else if (installedSets > 0 && sel == 1)
        {
            string customName = ChronosUi.Prompt("Название коллекции в игре", col.Name);
            CreateCollectionInGameUI(col, customName, config, _snap, null, onlyInstalledInGame: true);
            RefreshSnapshot(config);
        }
        else if ((installedSets > 0 && sel == 2) || (installedSets == 0 && sel == 1))
        {
            await ExecuteDownloadAndCollectionFlowAsync(
                col, missingSetIds, createCollection: false, col.Name, config, noImport: false, tempDir, history);
            RefreshSnapshot(config);
        }
    }

    private static async Task InteractiveCreateCollectionOnlyAsync(ChronosConfig config)
    {
        Console.WriteLine();
        ChronosUi.WriteLine("  Создание (или очистка от лишних диффов) коллекции в игре без скачивания карт.", ConsoleColor.Gray);
        string input = ChronosUi.Prompt("Ссылка или ID коллекции с osucollector.com");
        var ids = ParseCollectionIdsFromText(input);
        if (ids.Count == 0)
        {
            ChronosUi.WriteLine("  Не удалось распознать ссылку или номер коллекции.", ConsoleColor.Yellow);
            ChronosUi.WaitEnter();
            return;
        }

        RefreshSnapshot(config);

        foreach (int id in ids)
        {
            Console.WriteLine();
            ChronosUi.WriteLine($"  Загрузка информации о коллекции #{id}...", ConsoleColor.Cyan);
            CollectorCollection col;
            try
            {
                col = await CollectorService.FetchCollectionAsync(id);
            }
            catch (Exception ex)
            {
                ChronosUi.WriteLine($"  ✖ Ошибка: {ex.Message}", ConsoleColor.Red);
                continue;
            }

            var dummyHistory = new History("");
            PrintCollectionAnalysis(col, config, _snap, dummyHistory, out _, out int installedSets, out int installedDiffs, out int totalDiffs);

            string colName = ChronosUi.Prompt("Название коллекции в игре", col.Name);
            CreateCollectionInGameUI(col, colName, config, _snap, null, onlyInstalledInGame: false);
        }

        RefreshSnapshot(config);
        ChronosUi.WaitEnter();
    }

    private static async Task InteractiveCatalogAndSearchAsync(
        ChronosConfig config,
        string tempDir,
        History history)
    {
        while (true)
        {
            int mode = ChronosUi.SelectMenu(
                "КАТАЛОГ КОЛЛЕКЦИЙ OSU!COLLECTOR:",
                new[]
                {
                    "Поиск по названию, тегам или автору (stream, aim, farm, tech, dt...)",
                    "Все коллекции списком (все 17 800+ коллекций подряд)",
                    "По популярности (топ за всё время / месяц / неделю / день)",
                    "Назад в главное меню",
                });

            if (mode == 3) return;

            if (mode == 0)
            {
                Console.WriteLine();
                string query = ChronosUi.Prompt("Введите запрос для поиска (или Enter для всех коллекций)");
                int sortIdx = ChronosUi.SelectMenu(
                    "Сортировка результатов:",
                    new[]
                    {
                        "По релевантности (лучшее совпадение)",
                        "По популярности (больше всего лайков ★)",
                        "Сначала новые",
                        "Назад",
                    });
                if (sortIdx == 3) continue;

                string? sortBy = sortIdx switch
                {
                    1 => "favourites",
                    2 => "dateUploaded",
                    _ => null,
                };

                string title = string.IsNullOrWhiteSpace(query)
                    ? "Все коллекции"
                    : $"Поиск: «{query}»";

                bool backToMain = await BrowsePaginatedCollectionsAsync(
                    title,
                    cursor => CollectorService.SearchCollectionsPageAsync(query, sortBy, 12, cursor),
                    config,
                    tempDir,
                    history);
                if (backToMain) return;
            }
            else if (mode == 1)
            {
                int sortIdx = ChronosUi.SelectMenu(
                    "В каком порядке показать все коллекции?",
                    new[]
                    {
                        "Сначала самые популярные (по лайкам ★)",
                        "Сначала самые новые (недавно загруженные)",
                        "Назад",
                    });
                if (sortIdx == 2) continue;

                string sortBy = sortIdx == 0 ? "favourites" : "dateUploaded";
                string title = sortIdx == 0 ? "Все коллекции (по популярности)" : "Все коллекции (сначала новые)";

                bool backToMain = await BrowsePaginatedCollectionsAsync(
                    title,
                    cursor => CollectorService.SearchCollectionsPageAsync("", sortBy, 12, cursor),
                    config,
                    tempDir,
                    history);
                if (backToMain) return;
            }
            else if (mode == 2)
            {
                int rangeIdx = ChronosUi.SelectMenu(
                    "Период популярности:",
                    new[]
                    {
                        "За всё время",
                        "За последний месяц",
                        "За последнюю неделю",
                        "За сегодня",
                        "Назад",
                    });

                if (rangeIdx == 4) continue;
                var (range, rangeTitle) = rangeIdx switch
                {
                    0 => ("alltime", "Топ за всё время"),
                    1 => ("month", "Топ за месяц"),
                    2 => ("week", "Топ за неделю"),
                    _ => ("day", "Топ за сегодня"),
                };

                bool backToMain = await BrowsePaginatedCollectionsAsync(
                    rangeTitle,
                    cursor => CollectorService.FetchPopularPageAsync(range, 12, cursor),
                    config,
                    tempDir,
                    history);
                if (backToMain) return;
            }
        }
    }

    private static async Task<bool> BrowsePaginatedCollectionsAsync(
        string sectionTitle,
        Func<int?, Task<CollectionPageResult>> fetchPage,
        ChronosConfig config,
        string tempDir,
        History history)
    {
        var cursorHistory = new List<int?> { null };
        int pageIndex = 0;

        while (true)
        {
            int? currentCursor = cursorHistory[pageIndex];
            Console.WriteLine();
            ChronosUi.WriteLine($"  Загружаю список ({sectionTitle}, стр. {pageIndex + 1})...", ConsoleColor.Cyan);

            CollectionPageResult page;
            try
            {
                page = await fetchPage(currentCursor);
            }
            catch (Exception ex)
            {
                ChronosUi.WriteLine($"  ✖ Ошибка загрузки: {ex.Message}", ConsoleColor.Red);
                ChronosUi.WaitEnter();
                return false;
            }

            if (page.Collections.Count == 0)
            {
                ChronosUi.WriteLine("  Ничего не найдено.", ConsoleColor.Yellow);
                ChronosUi.WaitEnter("Нажмите Enter...");
                return false;
            }

            string totalInfo = page.TotalResults.HasValue ? $" (всего найдено: {page.TotalResults.Value:N0})" : "";
            string menuHeader = $"{sectionTitle}{totalInfo} — Страница {pageIndex + 1}:";

            var items = new List<string>();
            foreach (var c in page.Collections)
            {
                items.Add($"#{c.Id,-6} │ {Truncate(c.Name, 34),-34} │ от {Truncate(c.Uploader, 14),-14} │ {c.BeatmapCount,5} сложн. │ ★ {c.Favourites}");
            }

            int nextBtnIdx = -1;
            int prevBtnIdx = -1;

            if (page.HasMore && page.NextCursor.HasValue)
            {
                nextBtnIdx = items.Count;
                items.Add($"──► Следующая страница (стр. {pageIndex + 2})");
            }
            if (pageIndex > 0)
            {
                prevBtnIdx = items.Count;
                items.Add($"◄── Предыдущая страница (стр. {pageIndex})");
            }

            int changeFilterIdx = items.Count;
            items.Add("Сменить поиск / фильтр");

            int backMainIdx = items.Count;
            items.Add("В главное меню");

            int pick = ChronosUi.SelectMenu(menuHeader, items);

            if (pick >= 0 && pick < page.Collections.Count)
            {
                RefreshSnapshot(config, history);
                await HandleSingleCollectionInteractiveAsync(page.Collections[pick].Id, config, tempDir, history);
                ChronosUi.WaitEnter("Нажмите Enter, чтобы вернуться к списку коллекций...");
            }
            else if (pick == nextBtnIdx)
            {
                if (pageIndex + 1 >= cursorHistory.Count)
                    cursorHistory.Add(page.NextCursor);
                pageIndex++;
            }
            else if (pick == prevBtnIdx)
            {
                pageIndex = Math.Max(0, pageIndex - 1);
            }
            else if (pick == changeFilterIdx)
            {
                return false;
            }
            else if (pick == backMainIdx)
            {
                return true;
            }
        }
    }

    private static void InteractiveManageCollections(ChronosConfig config)
    {
        while (true)
        {
            RefreshSnapshot(config);
            Console.WriteLine();
            if (_snap == null)
            {
                ChronosUi.WriteLine($"  ✖ Не удалось прочитать базу {GameLabel(config)}: {_snapError}", ConsoleColor.Red);
                ChronosUi.WaitEnter();
                return;
            }

            ChronosUi.WriteLine($"  Твои коллекции в {GameLabel(config)} ({_snap.Collections.Count} шт.):", ConsoleColor.Cyan);
            if (_snap.Collections.Count == 0)
            {
                ChronosUi.WriteLine("    (Коллекций пока нет)", ConsoleColor.DarkGray);
            }

            var menu = _snap.Collections
                .Select(c => $"{c.Name,-38} │ В игре: {c.InstalledHashes,4} из {c.TotalHashes,-4} сложн.")
                .ToList();

            menu.Add($"[Сделать резервную копию базы коллекций {GameLabel(config)}]");
            menu.Add("Назад в главное меню");

            int idx = ChronosUi.SelectMenu("Выберите коллекцию:", menu);
            if (idx == menu.Count - 1) return;

            if (idx == menu.Count - 2)
            {
                if (LazerDatabase.BackupActiveManual(config, out string? bakPath, out string? bakErr))
                    ChronosUi.WriteLine($"  ✔ Резервная копия сохранена: {bakPath}", ConsoleColor.Green);
                else
                    ChronosUi.WriteLine($"  ✖ Ошибка: {bakErr}", ConsoleColor.Red);
                ChronosUi.WaitEnter("Нажмите Enter...");
                continue;
            }

            var selectedCol = _snap.Collections[idx];
            int action = ChronosUi.SelectMenu(
                $"Коллекция «{selectedCol.Name}» ({selectedCol.InstalledHashes} сложностей):",
                new[]
                {
                    "Переименовать коллекцию",
                    $"Удалить коллекцию из {GameLabel(config)} (сами карты останутся)",
                    "Назад",
                });

            if (action == 0)
            {
                string newName = ChronosUi.Prompt("Новое название", selectedCol.Name);
                if (!string.IsNullOrWhiteSpace(newName) && newName != selectedCol.Name)
                {
                    if (LazerDatabase.RenameActiveCollection(config, selectedCol, newName, out string? err))
                        ChronosUi.WriteLine($"  ✔ Переименовано в «{newName}».", ConsoleColor.Green);
                    else
                        ChronosUi.WriteLine($"  ✖ Ошибка: {err}", ConsoleColor.Red);
                    ChronosUi.WaitEnter("Нажмите Enter...");
                }
            }
            else if (action == 1)
            {
                int confirm = ChronosUi.SelectMenu(
                    $"Удалить коллекцию «{selectedCol.Name}»?",
                    new[] { "Нет, оставить", $"Да, удалить «{selectedCol.Name}»" });
                if (confirm == 1)
                {
                    if (LazerDatabase.DeleteActiveCollection(config, selectedCol, out string? err))
                        ChronosUi.WriteLine($"  ✔ Коллекция «{selectedCol.Name}» удалена.", ConsoleColor.Green);
                    else
                        ChronosUi.WriteLine($"  ✖ Ошибка: {err}", ConsoleColor.Red);
                    ChronosUi.WaitEnter("Нажмите Enter...");
                }
            }
        }
    }

    private static void InteractiveSettingsMenu(ChronosConfig config)
    {
        while (true)
        {
            Console.WriteLine();
            int choice = ChronosUi.SelectMenu(
                "НАСТРОЙКИ CHRONOS (нажмите Enter на пункте, чтобы переключить):",
                new[]
                {
                    $"Куда качать и добавлять коллекции : {(config.TargetMode == OsuTargetMode.Stable ? "OSU!STABLE" : "OSU!LAZER")}",
                    $"Скачивать карты без видео         : {(config.NoVideo ? "ДА (быстрее, меньше размер)" : "НЕТ (с видео)")}",
                    $"Одновременных загрузок (потоки)   : {config.Threads} (1–16)",
                    $"Путь к osu!lazer                  : {config.LazerExe ?? LazerDatabase.FindLazerExe() ?? "(не найден)"}",
                    $"Путь к папке osu!stable           : {config.StableDir ?? StableDatabase.FindStableDir() ?? "(не найдена)"}",
                    $"Доп. папка сохранения .osz        : {config.SaveDir ?? "(выкл.)"}",
                    $"Хранилище CHRONOS (история, контур): {ChronosStorage.StorageDir}",
                    "Готово (вернуться в главное меню)",
                });

            switch (choice)
            {
                case 0:
                    config.TargetMode = config.TargetMode == OsuTargetMode.Stable ? OsuTargetMode.Lazer : OsuTargetMode.Stable;
                    config.Save();
                    break;
                case 1:
                    config.NoVideo = !config.NoVideo;
                    config.Save();
                    break;
                case 2:
                    string tStr = ChronosUi.Prompt("Сколько карт качать одновременно (1–16)", config.Threads.ToString());
                    if (int.TryParse(tStr, out int t)) config.Threads = Math.Clamp(t, 1, 16);
                    config.Save();
                    break;
                case 3:
                    string lExe = ChronosUi.Prompt("Путь к osu!.exe для lazer (оставьте пустым для автопоиска)");
                    config.LazerExe = string.IsNullOrWhiteSpace(lExe) ? null : lExe;
                    config.Save();
                    LazerDatabase.InitializeNativeResolver(config.LazerExe);
                    break;
                case 4:
                    string sDir = ChronosUi.Prompt("Путь к папке osu!stable, например C:\\osu! (оставьте пустым для автопоиска)");
                    config.StableDir = string.IsNullOrWhiteSpace(sDir) ? null : sDir;
                    config.Save();
                    break;
                case 5:
                    string saveDir = ChronosUi.Prompt("Папка для сохранения копий .osz (оставьте пустым, чтобы не сохранять)");
                    config.SaveDir = string.IsNullOrWhiteSpace(saveDir) ? null : Path.GetFullPath(saveDir);
                    config.Save();
                    break;
                case 6:
                    try
                    {
                        Directory.CreateDirectory(ChronosStorage.StorageDir);
                        Process.Start(new ProcessStartInfo("explorer.exe", ChronosStorage.StorageDir) { UseShellExecute = true });
                    }
                    catch { /* ignore */ }
                    break;
                case 7:
                    config.Save();
                    return;
            }
        }
    }

    // ===================================================================== Shared Workflows

    private static void PrintCollectionAnalysis(
        CollectorCollection col,
        ChronosConfig config,
        LazerLibrarySnapshot? snap,
        History history,
        out List<int> missingSetIds,
        out int installedSetsCount,
        out int installedDiffsCount,
        out int totalDiffsCount)
    {
        missingSetIds = new List<int>();
        installedSetsCount = 0;
        installedDiffsCount = 0;
        totalDiffsCount = 0;

        foreach (var set in col.Sets)
        {
            // Проверяем, установлены ли в игре конкретные нужные диффы этого сета (или сам сет)
            int matchedDiffsInSet = 0;
            foreach (var bm in set.Beatmaps)
            {
                totalDiffsCount++;
                if (snap != null && snap.HasBeatmap(bm))
                {
                    installedDiffsCount++;
                    matchedDiffsInSet++;
                }
            }

            bool hasAllTargetDiffs = set.Beatmaps.Count > 0 && matchedDiffsInSet == set.Beatmaps.Count;
            bool inGame = snap != null && (hasAllTargetDiffs || snap.HasSet(set.Id));
            bool inHist = history.Contains(set.Id);

            if (inGame || inHist)
            {
                installedSetsCount++;
            }
            else
            {
                missingSetIds.Add(set.Id);
            }
        }

        var existingCol = snap?.Collections.FirstOrDefault(
            c => string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        ChronosUi.WriteLine("  ┌──────────────────────────────────────────────────────────────────────────┐", ConsoleColor.DarkCyan);
        ChronosUi.WriteLine($"  │ КОЛЛЕКЦИЯ #{col.Id}: «{col.Name}» (автор: {col.Uploader}, ★ {col.Favourites})", ConsoleColor.Cyan);
        ChronosUi.WriteLine("  ├──────────────────────────────────────────────────────────────────────────┤", ConsoleColor.DarkCyan);
        Console.WriteLine($"  │ Выбранная игра         : {GameLabel(config).ToUpperInvariant()}");
        Console.WriteLine($"  │ В коллекции на сайте   : {col.Sets.Count} карт ({totalDiffsCount} конкретных сложностей)");
        ChronosUi.WriteLine(
            $"  │ Уже есть у тебя в игре : {installedSetsCount} из {col.Sets.Count} карт ({installedDiffsCount} из {totalDiffsCount} нужных сложностей)",
            installedSetsCount == col.Sets.Count ? ConsoleColor.Green : ConsoleColor.Yellow);
        ChronosUi.WriteLine(
            $"  │ Осталось докачать      : {missingSetIds.Count} карт",
            missingSetIds.Count == 0 ? ConsoleColor.Green : ConsoleColor.White);

        if (existingCol != null)
        {
            ChronosUi.WriteLine(
                $"  │ В твоих коллекциях     : уже есть «{existingCol.Name}» ({existingCol.TotalHashes} сложн.)",
                ConsoleColor.Magenta);
        }
        ChronosUi.WriteLine("  └──────────────────────────────────────────────────────────────────────────┘", ConsoleColor.DarkCyan);
    }

    private static async Task<(int ok, int fail)> ExecuteDownloadAndCollectionFlowAsync(
        CollectorCollection col,
        List<int> setsToDownload,
        bool createCollection,
        string collectionName,
        ChronosConfig config,
        bool noImport,
        string tempDir,
        History history)
    {
        bool isStable = config.TargetMode == OsuTargetMode.Stable;
        string? lazerExe = null;
        string? stableSongsDir = null;

        if (!noImport && setsToDownload.Count > 0)
        {
            if (isStable)
            {
                stableSongsDir = StableDatabase.FindSongsDir(config.StableDir);
                if (stableSongsDir == null)
                {
                    ChronosUi.WriteLine("  ✖ Папка Songs в osu!stable не найдена. Проверьте путь к osu!stable в настройках.", ConsoleColor.Red);
                    return (0, setsToDownload.Count);
                }
            }
            else
            {
                lazerExe = LazerDatabase.FindLazerExe(config.LazerExe);
                if (lazerExe == null)
                {
                    ChronosUi.WriteLine("  ✖ Не найден osu!lazer (osu!.exe). Проверьте путь в настройках.", ConsoleColor.Red);
                    return (0, setsToDownload.Count);
                }
            }
        }

        if (config.SaveDir != null)
            Directory.CreateDirectory(config.SaveDir);

        Dictionary<int, List<string>>? downloadedOszMd5s = null;
        int ok = 0, fail = 0;

        if (setsToDownload.Count > 0)
        {
            Console.WriteLine();
            ChronosUi.WriteLine(
                $"  Скачиваю {setsToDownload.Count} карт(ы) для {GameLabel(config)} ({config.Threads} потоков)...",
                ConsoleColor.Cyan);

            var dlRes = await CollectorService.DownloadAndImportSetsAsync(
                col, setsToDownload, config, noImport, lazerExe, stableSongsDir, tempDir, history);
            ok = dlRes.OkCount;
            fail = dlRes.FailCount;
            downloadedOszMd5s = dlRes.DownloadedOszMd5s;
        }

        if (createCollection)
        {
            var freshSnap = LazerDatabase.ScanActiveLibrary(config, out _);
            CreateCollectionInGameUI(col, collectionName, config, freshSnap, downloadedOszMd5s, onlyInstalledInGame: false);
        }

        Console.WriteLine();
        ChronosUi.WriteLine(
            $"  ✔ Готово! Скачано новых карт: {ok}, пропущено (уже были): {col.Sets.Count - setsToDownload.Count}" +
            (fail > 0 ? $", ошибок: {fail}" : "") + ".",
            fail == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);

        if (isStable && ok > 0 && !noImport)
        {
            ChronosUi.WriteLine(
                $"  Подсказка для osu!stable: карты сохранены в {stableSongsDir}. Зайдите в выбор песен в osu!stable (или нажмите F5), чтобы игра их распаковала.",
                ConsoleColor.Cyan);
        }

        return (ok, fail);
    }

    private static void CreateCollectionInGameUI(
        CollectorCollection col,
        string collectionName,
        ChronosConfig config,
        LazerLibrarySnapshot? snap,
        Dictionary<int, List<string>>? downloadedOszMd5s,
        bool onlyInstalledInGame)
    {
        var hashes = LazerDatabase.ResolveCollectionHashes(
            col, snap, downloadedOszMd5s, onlyInstalledInGame);

        if (hashes.Count == 0)
        {
            ChronosUi.WriteLine(
                "  ⚠ Не найдено ни одной подходящей сложности для добавления в коллекцию.",
                ConsoleColor.Yellow);
            return;
        }

        bool replaceExisting = false;

        if (LazerDatabase.CreateOrUpdateActiveCollection(
            config,
            collectionName,
            hashes,
            replaceExisting,
            out int addedCount,
            out int totalCount,
            out bool createdNew,
            out string? error))
        {
            string actionWord = createdNew ? "Создана коллекция" : "Обновлена коллекция";
            ChronosUi.WriteLine(
                $"  ✔ [{GameLabel(config)}] {actionWord} «{collectionName}»: {totalCount} сложн.",
                ConsoleColor.Green);

            if (config.TargetMode == OsuTargetMode.Stable && StableDatabase.GetRunningStableProcess(config.StableDir) != null)
            {
                ChronosUi.WriteLine(
                    "    ⚠ Внимание: osu!stable сейчас запущен! Чтобы osu!stable не перезаписал collection.db при выходе, лучше создавать коллекции при закрытом osu!stable.",
                    ConsoleColor.Yellow);
            }
            else if (config.TargetMode == OsuTargetMode.Lazer && LazerDatabase.GetRunningLazerProcess(config.LazerExe) != null)
            {
                ChronosUi.WriteLine(
                    "    (Если коллекция не обновилась в списке сразу — просто перезапустите osu!lazer)",
                    ConsoleColor.DarkGray);
            }
        }
        else
        {
            ChronosUi.WriteLine($"  ✖ Не удалось сохранить коллекцию в {GameLabel(config)}: {error}", ConsoleColor.Red);
        }
    }

    // ===================================================================== CLI Mode

    private static async Task<int> RunCliModeAsync(string[] args, ChronosConfig config, string tempDir, History history)
    {
        var cli = new CliOptions();
        if (!ParseCliArgs(args, cli))
            return 1;

        if (cli.TargetMode.HasValue)
        {
            config.TargetMode = cli.TargetMode.Value;
            history = GetHistoryForMode(config);
        }
        if (cli.Threads.HasValue) config.Threads = Math.Clamp(cli.Threads.Value, 1, 16);
        if (cli.NoVideo.HasValue) config.NoVideo = cli.NoVideo.Value;
        if (cli.BatchSize.HasValue) config.BatchSize = Math.Clamp(cli.BatchSize.Value, 1, 200);
        if (cli.SaveDir != null) config.SaveDir = cli.SaveDir;
        if (cli.LazerExe != null) config.LazerExe = cli.LazerExe;
        if (cli.StableDir != null) config.StableDir = cli.StableDir;
        if (cli.NoImport && config.SaveDir == null)
            config.SaveDir = Path.Combine(Environment.CurrentDirectory, "osz");

        if (cli.ListCollections || cli.DeleteCollectionName != null)
        {
            RefreshSnapshot(config, history);
            if (_snap == null)
            {
                ChronosUi.WriteLine($"  ✖ Ошибка базы {GameLabel(config)}: {_snapError}", ConsoleColor.Red);
                return 1;
            }

            if (cli.DeleteCollectionName != null)
            {
                var target = _snap.Collections.FirstOrDefault(
                    c => string.Equals(c.Name, cli.DeleteCollectionName, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    ChronosUi.WriteLine($"  ✖ Коллекция «{cli.DeleteCollectionName}» не найдена.", ConsoleColor.Yellow);
                    return 1;
                }
                if (LazerDatabase.DeleteActiveCollection(config, target, out string? err))
                    ChronosUi.WriteLine($"  ✔ Коллекция «{target.Name}» удалена из {GameLabel(config)}.", ConsoleColor.Green);
                else
                    ChronosUi.WriteLine($"  ✖ Ошибка удаления: {err}", ConsoleColor.Red);
                RefreshSnapshot(config);
            }

            if (cli.ListCollections && _snap != null)
            {
                ChronosUi.DrawBanner(config, _snap, _snapError);
                ChronosUi.WriteLine($"  Коллекции в {GameLabel(config)} ({_snap.Collections.Count} шт.):", ConsoleColor.Cyan);
                foreach (var c in _snap.Collections)
                    Console.WriteLine($"    • {c.Name,-38} | Установлено: {c.InstalledHashes,4} / {c.TotalHashes,-4}");
            }
            return 0;
        }

        if (cli.CollectionIds.Count == 0)
        {
            ChronosUi.WriteLine("  Не указан ID или ссылка на коллекцию.", ConsoleColor.Red);
            PrintHelp();
            return 1;
        }

        bool createCollection = cli.CreateCollection ?? config.AutoCreateCollection;
        if (cli.CollectionOnly) createCollection = true;

        RefreshSnapshot(config, history);
        ChronosUi.DrawBanner(config, _snap, _snapError);

        int totalOk = 0, totalFail = 0;

        foreach (int id in cli.CollectionIds)
        {
            CollectorCollection col;
            try
            {
                col = await CollectorService.FetchCollectionAsync(id);
            }
            catch (Exception ex)
            {
                ChronosUi.WriteLine($"  ✖ Коллекция #{id}: {ex.Message}", ConsoleColor.Red);
                totalFail++;
                continue;
            }

            PrintCollectionAnalysis(col, config, _snap, history, out var missingSetIds, out _, out _, out _);
            string targetColName = cli.CustomCollectionName ?? col.Name;

            if (cli.CollectionOnly)
            {
                CreateCollectionInGameUI(col, targetColName, config, _snap, null, onlyInstalledInGame: false);
                continue;
            }

            var toDownload = cli.Force ? col.SetIds : missingSetIds;
            var (ok, fail) = await ExecuteDownloadAndCollectionFlowAsync(
                col,
                toDownload,
                createCollection: createCollection && !cli.NoImport,
                targetColName,
                config,
                cli.NoImport,
                tempDir,
                history);

            totalOk += ok;
            totalFail += fail;
            RefreshSnapshot(config);
        }

        return totalFail == 0 ? 0 : 2;
    }

    private static List<int> ParseCollectionIdsFromText(string text)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var token in text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = CollectionUrlRegex.Match(token);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int urlId) && urlId > 0)
                result.Add(urlId);
            else if (int.TryParse(token.TrimStart('#'), out int rawId) && rawId > 0)
                result.Add(rawId);
        }
        return result.Distinct().ToList();
    }

    private static bool ParseCliArgs(string[] args, CliOptions opt)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next()
            {
                if (i + 1 < args.Length) return args[++i];
                ChronosUi.WriteLine($"  Для {a} нужно указать значение.", ConsoleColor.Red);
                return null;
            }

            switch (a.ToLowerInvariant())
            {
                case "-h": case "--help": case "/?":
                    PrintHelp();
                    return false;
                case "--stable": case "-s":
                    opt.TargetMode = OsuTargetMode.Stable;
                    break;
                case "--lazer": case "-l":
                    opt.TargetMode = OsuTargetMode.Lazer;
                    break;
                case "-t": case "--threads":
                    if (!int.TryParse(Next(), out int t)) return false;
                    opt.Threads = t;
                    break;
                case "-nv": case "--no-video":
                    opt.NoVideo = true;
                    break;
                case "--no-import":
                    opt.NoImport = true;
                    break;
                case "-f": case "--force":
                    opt.Force = true;
                    break;
                case "-co": case "--collection-only":
                    opt.CollectionOnly = true;
                    break;
                case "--list-collections":
                    opt.ListCollections = true;
                    break;
                case "--delete-collection":
                    opt.DeleteCollectionName = Next();
                    if (opt.DeleteCollectionName == null) return false;
                    break;
                case "-nc": case "--no-collection":
                    opt.CreateCollection = false;
                    break;
                case "-c": case "--collection-name":
                    opt.CustomCollectionName = Next();
                    if (opt.CustomCollectionName == null) return false;
                    break;
                case "-o": case "--save-dir":
                    string? dir = Next();
                    if (dir == null) return false;
                    opt.SaveDir = Path.GetFullPath(dir);
                    break;
                case "--lazer-path":
                    opt.LazerExe = Next();
                    if (opt.LazerExe == null) return false;
                    break;
                case "--stable-path":
                    opt.StableDir = Next();
                    if (opt.StableDir == null) return false;
                    break;
                case "--batch":
                    if (!int.TryParse(Next(), out int b) || b < 1) return false;
                    opt.BatchSize = b;
                    break;
                default:
                    var ids = ParseCollectionIdsFromText(a);
                    if (ids.Count > 0)
                        opt.CollectionIds.AddRange(ids);
                    else
                    {
                        ChronosUi.WriteLine($"  Неизвестный аргумент: {a}", ConsoleColor.Red);
                        PrintHelp();
                        return false;
                    }
                    break;
            }
        }
        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""

        CHRONOS v3.0 — Загрузчик коллекций osu!collector для osu!lazer и osu!stable

        Запуск удобного меню:
          CHRONOS

        Быстрый запуск через консоль:
          CHRONOS <ссылка или ID коллекции> [опции]

        Примеры:
          CHRONOS https://osucollector.com/collections/44
          CHRONOS 44 --stable
          CHRONOS 44 --collection-only

        Опции:
          -l,  --lazer              Работать с osu!lazer
          -s,  --stable             Работать с osu!stable
          -co, --collection-only    НЕ качать карты, только создать/обновить коллекцию в игре
          -nc, --no-collection      Скачать карты без создания коллекции в игре
          -c,  --collection-name X  Задать своё имя коллекции
          -t,  --threads N          Количество потоков скачивания (1–16)
          -nv, --no-video           Качать карты без видео
          -f,  --force              Перекачать все карты заново
          --list-collections        Показать список коллекций в выбранной игре
          -h,  --help               Справка
        """);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void CleanupOldTemp(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1))
                    try { File.Delete(f); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }
}
