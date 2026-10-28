using System.Globalization;
using ProdModel.Core;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "run" => RunInference(options),
                "build" => RunBuild(options),
                "counter" => Counter(options),
                "reverse" => Reverse(options),
                "stats" => Stats(options),
                "selftest" => SelfTest(options),
                "help" or "--help" or "-h" => Usage(),
                _ => Unknown(command)
            };
        }
        catch (KbParseException ex)
        {
            Console.Error.WriteLine("Ошибка разбора БЗ: " + ex.Message);
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine("Файл не найден: " + ex.Message);
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            map[key] = value;
        }
        return map;
    }

    private static string Required(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Не указан обязательный параметр --{name}.");
        return value;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Продукционная модель: прямой и обратный вывод.

            Команды:
              reverse --kb <файл> --item <предмет>
                  обратный запрос: какие вражеские герои опасны для выбранного предмета
              counter --kb <файл> (--input <файл> | --enemies "Haze,Seven")
                  ответ: какой предмет лучше всего подходит для контрпика состава 1–6 героев
              run --kb <файл> [--input <файл> | --enemies "..."] [--goal "атом"] [--mode both|forward|backward]
                  выполнить вывод по целям из файла (и/или по указанной цели)
              build --kb <файл> [--input <файл> | --enemies "..."]
                  собрать сборку предметов из рекомендаций прямого вывода
              stats --kb <файл>
                  статистика базы знаний
              selftest --kb <файл> [--cycles <файл>] [--input <файл>]
                  самопроверка: объём БЗ, глубина вывода, прямой/обратный вывод, циклы

            Примеры:
              pmcli reverse --kb data/deadlock_kb.txt --item MetalSkin
              pmcli counter --kb data/deadlock_kb.txt --input data/input/3_mixed.txt
              pmcli counter --kb data/deadlock_kb.txt --enemies "Haze,Seven,Abrams"
              pmcli run --kb data/deadlock_kb.txt --input data/input/6_full.txt --mode backward
              pmcli selftest --kb data/deadlock_kb.txt --input data/input/3_mixed.txt --cycles data/demo_cycles.txt
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда «{command}». См. pmcli help.");
        return 2;
    }

    private static void PrintHeader(KnowledgeBase kb)
    {
        Console.WriteLine($"БЗ: {kb.Name}");
        Console.WriteLine($"  файлы: {string.Join(", ", kb.Sources.Select(Path.GetFileName))}");
        Console.WriteLine($"  фактов: {kb.Facts.Count}, правил: {kb.Rules.Count}, целей: {kb.Goals.Count}");
        foreach (var diagnostic in kb.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Take(20))
        {
            Console.WriteLine("  " + diagnostic);
        }
    }

    /// <summary>
    /// Загружает базу знаний и подставляет входные данные — состав противника
    /// из файла (--input) или списка (--enemies). Знания и вход разделены:
    /// в файле БЗ нет ни одного fact enemy(...).
    /// </summary>
    private static (KnowledgeBase Kb, string Composition) LoadWithComposition(
        Dictionary<string, string> options,
        bool requireComposition = false)
    {
        var kb = KbLoader.Load(Required(options, "kb"));
        var enemies = new List<(string Name, double Cf)>();

        if (options.TryGetValue("input", out var inputPath))
        {
            var input = KbLoader.Load(inputPath);
            enemies.AddRange(input.FactsFor("enemy").Select(f => (f.Atom.Args[0].Name, f.Cf)));
        }

        if (options.TryGetValue("enemies", out var list))
        {
            enemies.AddRange(list.Split(new[] { ',', ';', ' ' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => (Name: name, Cf: 0.9)));
        }

        enemies = enemies
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Cf: g.Max(x => x.Cf)))
            .ToList();

        if (enemies.Count > 6)
            throw new ArgumentException($"В составе может быть от 1 до 6 героев, указано {enemies.Count}.");

        if (enemies.Count == 0)
        {
            if (requireComposition)
                throw new ArgumentException("Укажите состав противника: --input <файл> или --enemies \"Haze,Seven\".");
            return (kb, string.Empty);
        }

        var known = kb.GetSort("hero")?.Values ?? new List<string>();
        var unknown = enemies.Where(e => !known.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).Select(e => e.Name).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException("Неизвестные герои: " + string.Join(", ", unknown));

        return (kb.WithEnemies(enemies), string.Join(", ", enemies.Select(e => e.Name)));
    }

    private static int Reverse(Dictionary<string, string> options)
    {
        var kb = KbLoader.Load(Required(options, "kb"));
        var item = Required(options, "item");

        var known = kb.GetSort("item")?.Values ?? new List<string>();
        var canonical = known.FirstOrDefault(x => x.Equals(item, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            throw new ArgumentException($"Неизвестный предмет: {item}. Известные: {string.Join(", ", known)}");

        PrintHeader(kb);
        var result = ReverseQuery.Run(kb, canonical);
        Console.WriteLine();
        Console.Write(result.Explain());

        var first = result.Pack(1).FirstOrDefault();
        if (first?.Proof is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"ОБОСНОВАНИЕ ПО ОБРАТНОМУ ВЫВОДУ: {first.Hero}");
            Console.Write(TextExplainer.ExplainBackward(first.Proof));
        }

        return 0;
    }

    private static int Counter(Dictionary<string, string> options)
    {
        var (kb, composition) = LoadWithComposition(options, requireComposition: true);
        PrintHeader(kb);
        Console.WriteLine($"  состав противника: {composition}");

        var forward = new ForwardChainer().Run(kb);
        var plan = new BuildPlanner().Build(forward, kb);

        var threats = forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("threat", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Cf)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("УГРОЗЫ СОСТАВА:");
        foreach (var threat in threats)
        {
            Console.WriteLine($"  • {threat.Atom.Args[0].Name,-16} cf={Certainty.Format(threat.Cf)}");
        }

        var best = plan.BestCounter;
        Console.WriteLine();

        if (best is null)
        {
            Console.WriteLine("КОНТРПИК НЕ НАЙДЕН: под угрозы состава не выведено ни одного предмета.");
            return 0;
        }

        Console.WriteLine("ЛУЧШИЙ КОНТРПИК");
        Console.WriteLine($"  предмет:      {best.Item}");
        Console.WriteLine($"  закрывает:    {best.Need}");
        Console.WriteLine($"  уверенность:  cf={Certainty.Format(best.Cf)}");
        Console.WriteLine($"  слот/цена:    {best.Slot}, {best.Cost}" + (best.Active ? ", активный" : string.Empty));

        Console.WriteLine();
        Console.WriteLine("ТОП-3 КОНТРПИКА:");
        var place = 1;
        foreach (var item in plan.TopCounters(3))
        {
            Console.WriteLine($"  {place++}. {item.Item,-20} cf={Certainty.Format(item.Cf)}  под {item.Need}");
        }

        var engine = new BackwardEngine();
        var proof = engine.Solve(kb, forward.Memory.InputOnly(),
            new Predicate("pick", Arg.Const(best.Item), Arg.Const(best.Need)));

        if (proof.Proven)
        {
            Console.WriteLine();
            Console.WriteLine("ОБОСНОВАНИЕ (обратный вывод):");
            Console.Write(TextExplainer.ExplainBackward(proof));
        }

        return 0;
    }

    private static int RunInference(Dictionary<string, string> options)
    {
        var (kb, composition) = LoadWithComposition(options);
        var mode = options.TryGetValue("mode", out var m) ? m.ToLowerInvariant() : "both";
        var trace = options.ContainsKey("trace");
        var rejected = options.ContainsKey("rejected");

        PrintHeader(kb);
        if (composition.Length > 0) Console.WriteLine($"  состав противника: {composition}");

        var forward = new ForwardChainer().Run(kb);
        Console.WriteLine();
        Console.WriteLine($"ПРЯМОЙ ВЫВОД: шагов {forward.Firings.Count}, фактов {forward.Memory.Count} (выведено {forward.DerivedCount}), " +
                          $"слоёв {forward.Layers}, проходов {forward.Passes}, заблокировано повторов {forward.BlockedRepeats}, " +
                          $"циклических поддержек {forward.CircularSupports}, " +
                          $"лимит {(forward.LimitReached ? "ДОСТИГНУТ" : "не достигнут")}, время {forward.ElapsedMs} мс");

        if (trace)
        {
            foreach (var firing in forward.Firings)
            {
                Console.WriteLine(TextExplainer.ExplainFiring(firing));
            }
        }

        var goals = new List<GoalDefinition>();
        if (options.TryGetValue("goal", out var goalText))
        {
            goals.Add(ParseGoal(goalText));
        }
        else
        {
            goals.AddRange(kb.Goals);
        }

        if (goals.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Цели не заданы (ни в файле, ни параметром --goal).");
            return 0;
        }

        if (mode is "backward" or "both")
        {
            var facts = forward.Memory.InputOnly();
            var engine = new BackwardEngine();
            Console.WriteLine();
            Console.WriteLine("ОБРАТНЫЙ ВЫВОД:");

            foreach (var goal in goals)
            {
                Console.WriteLine();
                Console.WriteLine($"--- Цель: {goal} ---");
                foreach (var instance in BackwardSolver.EnumerateInstances(goal, kb))
                {
                    var result = engine.Solve(kb, facts, instance);
                    if (!result.Proven) continue;

                    Console.WriteLine(TextExplainer.ExplainBackward(result, rejected));
                }

                var anyProven = BackwardSolver.EnumerateInstances(goal, kb)
                    .Select(i => engine.Solve(kb, facts, i))
                    .Any(r => r.Proven);
                if (!anyProven) Console.WriteLine($"  Цель {goal} не доказана ни при какой подстановке.");
            }
        }

        if (mode is "forward" or "both")
        {
            Console.WriteLine();
            Console.WriteLine("ФАКТЫ ПО СЛОЯМ:");
            for (var layer = 0; layer <= forward.Layers; layer++)
            {
                var facts = forward.FactsByLayer(layer).ToList();
                if (facts.Count == 0) continue;
                Console.WriteLine($"  слой {layer}: {facts.Count}");
                foreach (var fact in facts.Take(40))
                {
                    Console.WriteLine($"    {fact.Text} cf={Certainty.Format(fact.Cf)}" +
                                      (fact.IsDerived ? $" ← {fact.Rule?.Id}" : " (вход)"));
                }
                if (facts.Count > 40) Console.WriteLine($"    ... ещё {facts.Count - 40}");
            }
        }

        return 0;
    }

    private static GoalDefinition ParseGoal(string text)
    {
        // Простейший разбор цели из командной строки, в том числе с сортами: pick(?i:item, ?n:need)
        var open = text.IndexOf('(');
        if (open < 0) return new GoalDefinition { Pattern = new Predicate(text, Array.Empty<Arg>()) };

        var name = text[..open].Trim();
        var inner = text[(open + 1)..].TrimEnd(')');
        var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var args = new List<Arg>();
        var sorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            if (part.StartsWith('?'))
            {
                var token = part[1..];
                var colon = token.IndexOf(':');
                if (colon > 0)
                {
                    var variable = token[..colon];
                    sorts[variable] = token[(colon + 1)..];
                    args.Add(Arg.Var(variable));
                }
                else
                {
                    args.Add(Arg.Var(token));
                }
            }
            else
            {
                args.Add(Arg.Const(part));
            }
        }

        var goal = new GoalDefinition { Pattern = new Predicate(name, args) };
        foreach (var kv in sorts) goal.VariableSorts[kv.Key] = kv.Value;
        return goal;
    }

    private static int RunBuild(Dictionary<string, string> options)
    {
        var (kb, composition) = LoadWithComposition(options);
        PrintHeader(kb);
        if (composition.Length > 0) Console.WriteLine($"  состав противника: {composition}");

        var forward = new ForwardChainer().Run(kb);
        Console.WriteLine();
        Console.WriteLine($"ПРЯМОЙ ВЫВОД: шагов {forward.Firings.Count}, фактов {forward.Memory.Count} (выведено {forward.DerivedCount}), " +
                          $"слоёв {forward.Layers}, время {forward.ElapsedMs} мс");

        var threats = forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("threat", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Cf)
            .ToList();
        var needs = forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("need", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Cf)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("УГРОЗЫ СОСТАВА:");
        foreach (var threat in threats) Console.WriteLine($"  • {threat.Atom.Args[0].Name,-16} cf={Certainty.Format(threat.Cf)}");

        Console.WriteLine();
        Console.WriteLine("ПОТРЕБНОСТИ СБОРКИ:");
        foreach (var need in needs) Console.WriteLine($"  • {need.Atom.Args[0].Name,-16} cf={Certainty.Format(need.Cf)}");

        var plan = new BuildPlanner().Build(forward, kb);
        Console.WriteLine();
        Console.WriteLine(plan.Explain());

        var covered = plan.Chosen.Select(c => c.Need).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncovered = needs.Select(n => n.Atom.Args[0].Name).Where(n => !covered.Contains(n)).ToList();
        if (uncovered.Count > 0)
        {
            Console.WriteLine("Потребности без ответа в сборке: " + string.Join(", ", uncovered));
        }

        var warnings = forward.Memory.Facts
            .Where(f => f.Atom.Name.Equals("warning", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("ПРЕДУПРЕЖДЕНИЯ:");
            foreach (var warning in warnings) Console.WriteLine($"  ⚠ {warning.Atom.Args[0].Name} cf={Certainty.Format(warning.Cf)}");
        }

        return 0;
    }

    private static int Stats(Dictionary<string, string> options)
    {
        var kb = KbLoader.Load(Required(options, "kb"));
        PrintHeader(kb);

        Console.WriteLine();
        Console.WriteLine("Предикаты (факты / правила в заключении):");
        foreach (var predicate in kb.Predicates())
        {
            var facts = kb.FactsFor(predicate).Count;
            var rules = kb.RulesFor(predicate).Count;
            Console.WriteLine($"  {predicate,-24} фактов {facts,4}   правил {rules,4}");
        }

        Console.WriteLine();
        Console.WriteLine("Сорта:");
        foreach (var sort in kb.Sorts.Values)
        {
            Console.WriteLine($"  {sort.Name,-10} {sort.Values.Count,3}: {string.Join(", ", sort.Values.Take(12))}{(sort.Values.Count > 12 ? ", ..." : string.Empty)}");
        }

        var thresholds = kb.Rules.Select(r => r.Cf).ToList();
        if (thresholds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"cf правил: min {Certainty.Format(thresholds.Min())}, max {Certainty.Format(thresholds.Max())}, средний {Certainty.Format(thresholds.Average())}");
        }

        return 0;
    }

    private static int SelfTest(Dictionary<string, string> options)
    {
        var failures = new List<string>();
        var (kb, composition) = LoadWithComposition(options, requireComposition: true);
        PrintHeader(kb);
        Console.WriteLine($"  состав противника: {composition}");

        Check(kb.Facts.Count >= 50, $"фактов {kb.Facts.Count} (нужно ≥ 50)", failures);
        Check(kb.Rules.Count >= 120, $"правил {kb.Rules.Count} (нужно ≥ 120)", failures);
        Check(kb.Goals.Count > 0, "в файле нет целей", failures);

        var forward = new ForwardChainer().Run(kb);
        Check(!forward.LimitReached, "прямой вывод упёрся в лимит фактов", failures);
        Check(forward.Layers >= 4, $"глубина прямого вывода {forward.Layers} (нужно ≥ 4)", failures);
        Console.WriteLine($"  прямой вывод: фактов {forward.Memory.Count}, выведено {forward.DerivedCount}, слоёв {forward.Layers}, шагов {forward.Firings.Count}, {forward.ElapsedMs} мс");

        var facts = forward.Memory.InputOnly();
        var engine = new BackwardEngine();
        var provenAny = 0;
        var bestDepth = 0;

        foreach (var goal in kb.Goals)
        {
            foreach (var instance in BackwardSolver.EnumerateInstances(goal, kb))
            {
                var result = engine.Solve(kb, facts, instance);
                if (!result.Proven) continue;

                provenAny++;
                bestDepth = Math.Max(bestDepth, result.Layers);

                var inForward = forward.Memory.Find(instance);
                Check(inForward is not null, $"обратный вывод доказал {instance}, а прямой не вывел", failures);
            }
        }

        Check(provenAny > 0, "обратный вывод не доказал ни одной цели", failures);
        Check(bestDepth >= 4, $"глубина обратного вывода {bestDepth} (нужно ≥ 4)", failures);
        Console.WriteLine($"  обратный вывод: доказано целей {provenAny}, максимальная глубина {bestDepth}");

        if (options.TryGetValue("cycles", out var cyclesPath))
        {
            Console.WriteLine();
            Console.WriteLine("ПРОВЕРКА ЦИКЛОВ: " + Path.GetFileName(cyclesPath));
            var cyclesKb = KbLoader.Load(cyclesPath);
            var cyclesForward = new ForwardChainer().Run(cyclesKb);
            Check(!cyclesForward.LimitReached, "прямой вывод на циклической БЗ упёрся в лимит", failures);
            Console.WriteLine($"  прямой вывод: шагов {cyclesForward.Firings.Count}, фактов {cyclesForward.Memory.Count}, заблокировано повторов {cyclesForward.BlockedRepeats}");

            foreach (var goal in cyclesKb.Goals)
            {
                foreach (var instance in BackwardSolver.EnumerateInstances(goal, cyclesKb))
                {
                    var result = engine.Solve(cyclesKb, cyclesForward.Memory.InputOnly(), instance);
                    Console.WriteLine($"  цель {instance}: {(result.Proven ? "доказана" : "не доказана")}, cf={Certainty.Format(result.Cf)}, " +
                                      $"узлов {result.NodesVisited}, циклов {result.CycleHits}");
                    Check(result.Proven, $"циклическая цель {instance} не доказана", failures);
                }
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("САМОПРОВЕРКА ПРОЙДЕНА.");
            return 0;
        }

        Console.WriteLine("САМОПРОВЕРКА НЕ ПРОЙДЕНА:");
        foreach (var failure in failures) Console.WriteLine("  ✘ " + failure);
        return 1;
    }

    private static void Check(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
