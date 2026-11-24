using System.Text;
using ProdModel.Core;

namespace ProdModel.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintUsage();
            return 0;
        }

        var kbPath = GetArg(args, "--kb") ?? FindDefaultKb();
        if (kbPath is null)
        {
            Console.Error.WriteLine("Не найден файл базы знаний. Укажите --kb <путь>.");
            return 2;
        }

        KnowledgeBase kb;
        try
        {
            kb = KbLoader.Load(kbPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка загрузки БЗ: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        var showForward = !HasFlag(args, "--backward-only");
        var showBackward = !HasFlag(args, "--forward-only");
        var verbose = HasFlag(args, "--verbose");
        var goalFilter = GetArg(args, "--goal");

        PrintKb(kb, kbPath);

        var forward = new ForwardChainer().Run(kb, new ForwardOptions
        {
            MaxPasses = 200,
            MaxFacts = 50_000,
            MaxMatches = Matcher.DefaultMaxMatches
        });

        if (showForward)
            PrintForward(forward, verbose);

        var treeFilter = GetArg(args, "--tree");
        if (treeFilter is not null)
            PrintTree(forward, treeFilter);

        if (showBackward)
            PrintBackward(kb, forward, goalFilter, verbose, HasFlag(args, "--initial"));

        return 0;
    }

    // ------------------------------------------------------------------ KB ---

    private static void PrintKb(KnowledgeBase kb, string path)
    {
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"БЗ: {Path.GetFileName(path)}");
        Console.WriteLine($"Имя: {kb.Name}");
        if (!string.IsNullOrWhiteSpace(kb.Description))
            Console.WriteLine($"Описание: {kb.Description}");
        Console.WriteLine($"Фактов: {kb.Facts.Count} · Правил: {kb.Rules.Count} · Целей: {kb.Goals.Count}");

        foreach (var diagnostic in kb.Diagnostics)
            Console.WriteLine($"  {diagnostic}");

        Console.WriteLine();
    }

    // ------------------------------------------------------------- forward ---

    private static void PrintForward(ForwardResult forward, bool verbose)
    {
        Console.WriteLine(new string('=', 78));
        Console.WriteLine("ПРЯМОЙ ВЫВОД");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"Проходов: {forward.Passes} · Слоёв: {forward.Layers} · Срабатываний: {forward.Firings.Count}");
        Console.WriteLine($"Фактов в памяти: {forward.Memory.Count} (входных {forward.Memory.Facts.Count(f => !f.IsDerived)})");
        Console.WriteLine($"Повторных блокировок: {forward.BlockedRepeats} · Циклических подпорок: {forward.CircularSupports}");
        Console.WriteLine($"Усечений сопоставления: {forward.MatchTruncations} · Лимит: {forward.LimitReached} · Время: {forward.ElapsedMs} мс");

        if (forward.Goal is not null)
        {
            Console.WriteLine(forward.GoalReached
                ? $"Цель {forward.Goal} достигнута на шаге {forward.GoalReachedAtStep}"
                : $"Цель {forward.Goal} НЕ достигнута");
        }

        Console.WriteLine();

        var byLayer = forward.Firings
            .GroupBy(f => f.Layer)
            .OrderBy(g => g.Key);

        foreach (var layer in byLayer)
        {
            Console.WriteLine($"--- слой {layer.Key} ({layer.Count()} срабатываний) ---");

            foreach (var firing in layer.OrderBy(f => f.Step))
            {
                var premises = firing.Premises.Count == 0
                    ? "(нет посылок)"
                    : string.Join(", ", firing.Premises.Select(p => p.Text));

                var conclusions = string.Join(", ", firing.Conclusions.Select(c => c.Text));

                Console.WriteLine($"  #{firing.Step} {firing.Rule.Id}: {premises} => {conclusions}");

                if (verbose && !string.IsNullOrWhiteSpace(firing.Rule.Why))
                    Console.WriteLine($"        почему: {firing.Rule.Why}");
            }

            Console.WriteLine();
        }

        var derived = forward.Memory.Facts.Where(f => f.IsDerived).ToList();
        Console.WriteLine($"--- выведенные факты ({derived.Count}) ---");
        foreach (var fact in derived.OrderBy(f => f.Depth).ThenBy(f => f.Text, StringComparer.Ordinal))
            Console.WriteLine($"  [слой {fact.Depth}] {fact.Text}  ← {fact.Rule?.Id ?? "—"}");

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- tree ---

    /// <summary>
    /// Печатает дерево доказательства факта — ровно то, что рисует панель графа
    /// (узел — факт, дети — посылки сработавшего правила).
    /// </summary>
    private static void PrintTree(ForwardResult forward, string filter)
    {
        var derived = forward.Memory.Facts.Where(f => f.IsDerived).ToList();

        var focus = string.IsNullOrWhiteSpace(filter)
            ? derived.LastOrDefault()
            : derived.FirstOrDefault(f => f.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine(new string('=', 78));
        Console.WriteLine("ДЕРЕВО ДОКАЗАТЕЛЬСТВА (как в панели графа)");
        Console.WriteLine(new string('-', 78));

        if (focus is null)
        {
            Console.WriteLine($"Факт не найден: {filter}");
            Console.WriteLine();
            return;
        }

        var nodes = 0;
        PrintProofNode(focus, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, ref nodes);
        Console.WriteLine();
        Console.WriteLine($"Узлов в дереве: {nodes} (всего выведенных фактов: {derived.Count})");
        Console.WriteLine();
    }

    private static void PrintProofNode(
        Fact fact,
        string indent,
        HashSet<string> path,
        int depth,
        ref int nodes)
    {
        nodes++;

        var kind = fact.IsDerived
            ? $"← {fact.Rule?.Id ?? "—"} · слой {fact.Depth}"
            : "← входной";

        Console.WriteLine($"{indent}• {fact.Text} {kind}");

        if (!fact.IsDerived || depth >= 12) return;

        if (!path.Add(fact.Key))
        {
            Console.WriteLine($"{indent}  (цикл)");
            return;
        }

        foreach (var premise in fact.Premises)
            PrintProofNode(premise, indent + "  ", path, depth + 1, ref nodes);

        path.Remove(fact.Key);
    }

    // ------------------------------------------------------------ backward ---

    private static void PrintBackward(
        KnowledgeBase kb,
        ForwardResult forward,
        string? goalFilter,
        bool verbose,
        bool initialOnly)
    {
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(initialOnly
            ? "ОБРАТНЫЙ ВЫВОД (от исходных фактов)"
            : "ОБРАТНЫЙ ВЫВОД (от полной памяти прямого вывода)");
        Console.WriteLine(new string('-', 78));

        var goals = kb.Goals
            .Where(g => goalFilter is null ||
                        g.Pattern.Name.Contains(goalFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (goals.Count == 0)
        {
            Console.WriteLine("Целей для проверки нет.");
            return;
        }

        var options = new BackwardOptions
        {
            MaxDepth = 14,
            MaxNodes = 30_000,
            UseMemo = true,
            ReuseMemo = true,
            MaxGoalInstances = 4000
        };

        var solutions = BackwardSolver.SolveAll(
            kb,
            initialOnly ? BuildInitialMemory(kb) : forward.Memory,
            goals,
            options);

        foreach (var group in solutions)
        {
            if (group.Results.Count == 0)
            {
                Console.WriteLine($"  {group.Goal} — нет результатов");
                continue;
            }

            // Цель может иметь несколько конкретизаций: печатаем каждую, иначе
            // итог «доказано N из M» расходится с видимыми строками.
            var single = group.Results.Count == 1;
            if (!single)
                Console.WriteLine($"  {group.Goal}");

            foreach (var result in group.Results)
            {
                var status = result.Root.State switch
                {
                    SearchState.Proven => result.CyclePrevented
                        ? "ДОКАЗАНА (цикл предотвращён)"
                        : "ДОКАЗАНА",
                    SearchState.Cyclic => "НЕ ДОКАЗАНА (цикл предотвращён)",
                    SearchState.DepthLimit => "НЕ ДОКАЗАНА (предел глубины)",
                    _ => $"НЕ ДОКАЗАНА ({result.FailureReason})"
                };

                var name = single ? group.Goal.ToString() : result.Goal.ToString();
                var pad = single ? "  " : "    ";

                Console.WriteLine(
                    $"{pad}{name,-52} {status,-32} узлов {result.NodesVisited,4} · циклов {result.CycleHits} · переключений {result.BranchSwitches}");

                if (verbose)
                    Console.WriteLine(Indent(TextExplainer.ExplainBackward(result, includeRejected: true), "      "));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Итог: доказано {solutions.Count(s => s.Proven.Any())} из {solutions.Count} целей.");

        var prevented = solutions
            .SelectMany(s => s.Results)
            .Sum(r => r.CycleHits);

        if (prevented > 0)
        {
            var stuck = solutions
                .SelectMany(s => s.Results)
                .Count(r => !r.Proven && r.Root.State == SearchState.Cyclic);

            var stuckText = stuck == 1 ? "одной цели" : $"{stuck} целей";

            Console.WriteLine(stuck > 0
                ? $"Циклов предотвращено: {prevented} — ветви замкнуты, но у {stuckText} не осталось альтернативных продукций."
                : $"Циклов предотвращено: {prevented} — ветви замкнуты, поиск переигран по другим продукциям.");
        }
    }

    // ------------------------------------------------------------- helpers ---

    private static void PrintUsage()
    {
        Console.WriteLine("ProdModel.Cli — текстовый прогон продукционной модели.");
        Console.WriteLine();
        Console.WriteLine("  --kb <путь>        файл базы знаний (по умолчанию data\\deadlock_kb.txt)");
        Console.WriteLine("  --forward-only     только прямой вывод");
        Console.WriteLine("  --backward-only    только обратный вывод");
        Console.WriteLine("  --goal <подстрока> ограничить обратный вывод целями, содержащими подстроку");
        Console.WriteLine("  --tree <подстрока> напечатать дерево доказательства выведенного факта");
        Console.WriteLine("  --initial          обратный вывод от исходных фактов базы, а не от");
        Console.WriteLine("                     памяти прямого вывода (даёт настоящие деревья)");
        Console.WriteLine("  --verbose          подробный разбор каждого шага");
        Console.WriteLine("  --help             эта справка");
    }

    private static WorkingMemory BuildInitialMemory(KnowledgeBase kb)
    {
        var memory = new WorkingMemory();
        foreach (var fact in kb.Facts)
            memory.AddInput(new Fact(fact.Atom, FactOrigin.Input) { DisplayName = fact.DisplayName });
        return memory;
    }

    private static string Indent(string text, string prefix)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            sb.Append(prefix).AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? FindDefaultKb()
    {
        var candidates = new[]
        {
            Path.Combine("data", "deadlock_kb.txt"),
            Path.Combine("..", "..", "..", "..", "data", "deadlock_kb.txt"),
            Path.Combine(AppContext.BaseDirectory, "data", "deadlock_kb.txt")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
