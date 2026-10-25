#pragma warning disable CA1416
namespace OsuCollectorLazer;

internal static class ChronosUi
{
    private static readonly object ConsoleLock = new();

    public static void SetWindowTitle(string? status = null)
    {
        try
        {
            Console.Title = string.IsNullOrWhiteSpace(status)
                ? "CHRONOS // osu!collector -> osu!lazer & osu!stable"
                : $"CHRONOS // {status}";
        }
        catch { /* ignore if no console window */ }
    }

    public static void DrawBanner(ChronosConfig config, LazerLibrarySnapshot? snap, string? snapError)
    {
        bool isStable = config.TargetMode == OsuTargetMode.Stable;
        string modeTag = isStable ? "OSU!STABLE" : "OSU!LAZER";
        SetWindowTitle(modeTag);

        try { if (!Console.IsOutputRedirected) Console.Clear(); } catch { /* ignore */ }

        WriteLine(@"  ╔══════════════════════════════════════════════════════════════════════════╗", ConsoleColor.DarkCyan);
        WriteLine(@"  ║    ██████╗██╗  ██╗██████╗  ██████╗ ███╗   ██╗ ██████╗ ███████╗           ║", ConsoleColor.Cyan);
        WriteLine(@"  ║   ██╔════╝██║  ██║██╔══██╗██╔═══██╗████╗  ██║██╔═══██╗██╔════╝           ║", ConsoleColor.Cyan);
        WriteLine(@"  ║   ██║     ███████║██████╔╝██║   ██║██╔██╗ ██║██║   ██║███████╗           ║", ConsoleColor.Cyan);
        WriteLine(@"  ║   ██║     ██╔══██║██╔══██╗██║   ██║██║╚██╗██║██║   ██║╚════██║           ║", ConsoleColor.DarkCyan);
        WriteLine(@"  ║   ╚██████╗██║  ██║██║  ██║╚██████╔╝██║ ╚████║╚██████╔╝███████║           ║", ConsoleColor.DarkCyan);
        WriteLine(@"  ║    ╚═════╝╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝ ╚═╝  ╚═══╝ ╚═════╝ ╚══════╝  v3.0     ║", ConsoleColor.DarkCyan);
        WriteLine(@"  ║           Загрузчик коллекций osu!collector  ──►  lazer & stable         ║", ConsoleColor.Gray);
        WriteLine(@"  ╚══════════════════════════════════════════════════════════════════════════╝", ConsoleColor.DarkCyan);

        Console.Write("   Активный режим   : ");
        if (isStable)
        {
            Write(" [ OSU!STABLE ] ", ConsoleColor.Magenta);
            string? stableDir = StableDatabase.FindStableDir(config.StableDir);
            var proc = StableDatabase.GetRunningStableProcess(config.StableDir);
            if (stableDir != null)
            {
                Write($"({stableDir})", ConsoleColor.Gray);
                if (proc != null)
                    WriteLine("  [ИГРА ЗАПУЩЕНА]", ConsoleColor.Yellow);
                else
                    WriteLine("  [ИГРА ЗАКРЫТА]", ConsoleColor.DarkGray);
            }
            else
            {
                WriteLine("Не найдена (укажите путь в настройках)", ConsoleColor.Red);
            }
        }
        else
        {
            Write(" [ OSU!LAZER ] ", ConsoleColor.Green);
            string? lazerExe = LazerDatabase.FindLazerExe(config.LazerExe);
            var proc = LazerDatabase.GetRunningLazerProcess(config.LazerExe);
            if (lazerExe != null)
            {
                Write($"({Path.GetDirectoryName(lazerExe)})", ConsoleColor.Gray);
                if (proc != null)
                    WriteLine("  [ИГРА ЗАПУЩЕНА]", ConsoleColor.Green);
                else
                    WriteLine("  [ИГРА ЗАКРЫТА]", ConsoleColor.DarkGray);
            }
            else
            {
                WriteLine("Не найден (укажите путь в настройках)", ConsoleColor.Red);
            }
        }

        Console.Write("   Твоя библиотека  : ");
        if (snap != null)
        {
            Write($"{snap.SetIds.Count:N0} карт", ConsoleColor.Cyan);
            Write($" ({snap.Md5Hashes.Count:N0} сложностей)", ConsoleColor.Gray);
            Write(" │ ", ConsoleColor.DarkGray);
            WriteLine($"{snap.Collections.Count} коллекций в игре", ConsoleColor.Yellow);
        }
        else
        {
            WriteLine($"Недоступна ({snapError ?? "не найдена"})", ConsoleColor.Red);
        }

        Console.Write("   Загрузка         : ");
        Write($"Потоков: {config.Threads}", ConsoleColor.Gray);
        Write(" │ ", ConsoleColor.DarkGray);
        Write($"Без видео: {(config.NoVideo ? "ДА" : "НЕТ")}", config.NoVideo ? ConsoleColor.Green : ConsoleColor.Gray);
        Write(" │ ", ConsoleColor.DarkGray);
        if (!string.IsNullOrEmpty(config.OsuUsername))
        {
            Write("Аккаунт osu!: ", ConsoleColor.Gray);
            Write(config.OsuUsername, ConsoleColor.Magenta);
            if (config.OsuIsSupporter)
                Write(" ♥ [Supporter]", ConsoleColor.Yellow);
            Console.WriteLine();
        }
        else
        {
            WriteLine("Аккаунт osu!: [не подключён]", ConsoleColor.DarkGray);
        }
        WriteLine("  ────────────────────────────────────────────────────────────────────────────", ConsoleColor.DarkGray);
    }

    /// <summary>Интерактивное меню (стрелки ↑/↓ + Enter ИЛИ клавиши 1..9).</summary>
    public static int SelectMenu(string title, IReadOnlyList<string> items, int defaultIndex = 0)
    {
        if (!string.IsNullOrEmpty(title))
        {
            Console.WriteLine();
            WriteLine($"  {title}", ConsoleColor.White);
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            for (int i = 0; i < items.Count; i++)
                Console.WriteLine($"    [{i + 1}] {items[i]}");
            Console.Write("  Ваш выбор: ");
            string? line = Console.ReadLine();
            if (line == null)
                return items.Count - 1;
            if (int.TryParse(line.Trim('\uFEFF', ' ', '\t', '\r', '\n'), out int n) && n >= 1 && n <= items.Count)
                return n - 1;
            return defaultIndex;
        }

        string hint = items.Count <= 9
            ? $"  (Выбор: стрелки ↑/↓ + Enter или цифра 1–{items.Count})"
            : "  (Выбор: стрелки ↑/↓ + Enter, либо Esc для возврата)";
        WriteLine(hint, ConsoleColor.DarkGray);

        int selected = Math.Clamp(defaultIndex, 0, items.Count - 1);
        for (int i = 0; i < items.Count; i++)
            Console.WriteLine();
        int top = Console.CursorTop - items.Count;

        bool oldCursorVisible = true;
        try { oldCursorVisible = Console.CursorVisible; Console.CursorVisible = false; } catch { /* ignore */ }

        try
        {
            while (true)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Console.SetCursorPosition(0, top + i);
                    bool active = i == selected;
                    string prefix = active ? "  ► " : "    ";
                    string numTag = items.Count > 9 ? $"[{i + 1,2}]" : $"[{i + 1}]";
                    string line = $"{prefix}{numTag} {items[i]}";
                    int pad = Math.Max(0, Console.WindowWidth - 1 - line.Length);

                    Console.ForegroundColor = active ? ConsoleColor.Cyan : ConsoleColor.Gray;
                    Console.Write(line + new string(' ', pad));
                    Console.ResetColor();
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.W)
                {
                    selected = (selected - 1 + items.Count) % items.Count;
                }
                else if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.S)
                {
                    selected = (selected + 1) % items.Count;
                }
                else if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar)
                {
                    Console.SetCursorPosition(0, top + items.Count);
                    Console.WriteLine();
                    return selected;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    Console.SetCursorPosition(0, top + items.Count);
                    Console.WriteLine();
                    return items.Count - 1;
                }
                else if (items.Count <= 9 && key.KeyChar >= '1' && key.KeyChar <= '9')
                {
                    int num = key.KeyChar - '1';
                    if (num < items.Count)
                    {
                        selected = num;
                        Console.SetCursorPosition(0, top + items.Count);
                        Console.WriteLine();
                        return selected;
                    }
                }
            }
        }
        finally
        {
            try { Console.CursorVisible = oldCursorVisible; } catch { /* ignore */ }
        }
    }

    public static string Prompt(string label, string? defaultValue = null)
    {
        if (defaultValue != null)
            Console.Write($"  {label} (Enter = «{defaultValue}»): ");
        else
            Console.Write($"  {label}: ");

        string? input = Console.ReadLine()?.Trim('\uFEFF', ' ', '\t', '\r', '\n');
        if (string.IsNullOrEmpty(input) && defaultValue != null)
            return defaultValue;
        return input ?? "";
    }

    public static void WaitEnter(string message = "Нажмите Enter, чтобы вернуться в меню...")
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine();
        Write($"  {message}", ConsoleColor.DarkGray);
        Console.ReadLine();
    }

    public static void Write(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = old;
        }
    }

    public static void WriteLine(string text, ConsoleColor color)
    {
        lock (ConsoleLock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }
    }
}
