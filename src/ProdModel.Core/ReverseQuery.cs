namespace ProdModel.Core;

/// <summary>Оценка героя относительно выбранного предмета.</summary>
public sealed class HeroVerdict
{
    public required string Hero { get; init; }
    public int TotalThreats { get; set; }
    public int Uncovered { get; set; }
    public double BestCf { get; set; }

    public List<string> UncoveredTags { get; } = new();
    public List<string> CoveredTags { get; } = new();

    public BackwardResult? Proof { get; set; }

    public int Covered => TotalThreats - Uncovered;
    public bool NoThreats => TotalThreats == 0;
    public bool Dangerous => !NoThreats && Uncovered > 0 && Covered == 0;
    public bool FullyCountered => !NoThreats && Uncovered == 0;
    public string Level => NoThreats ? "нет данных" : Dangerous ? "опасен" : FullyCountered ? "закрыт" : "частично";
    public string Ratio => $"{Uncovered}/{TotalThreats}";
    public string TargetLabel => NoThreats
        ? "нет профиля"
        : FullyCountered ? $"полный контр ({Covered}/{TotalThreats})"
                         : $"частично ({Covered}/{TotalThreats})";
}

/// <summary>Результат обратного запроса: выбранный предмет и оценка всех вражеских героев.</summary>
public sealed class ReverseResult
{
    public required string Item { get; init; }
    public List<HeroVerdict> Heroes { get; } = new();

    public IEnumerable<HeroVerdict> Dangerous => Heroes
        .Where(h => h.Dangerous)
        .OrderByDescending(h => h.Uncovered)
        .ThenByDescending(h => h.BestCf);

    public IEnumerable<HeroVerdict> PartiallyCovered => Heroes
        .Where(h => !h.NoThreats && !h.Dangerous && !h.FullyCountered)
        .OrderByDescending(h => h.Uncovered);

    public IEnumerable<HeroVerdict> FullyCovered => Heroes
        .Where(h => h.FullyCountered)
        .OrderByDescending(h => h.TotalThreats);

    public List<HeroVerdict> Pack(int size = 6) => Dangerous.Concat(PartiallyCovered).Take(size).ToList();

    public IEnumerable<HeroVerdict> BestTargets() => Heroes
        .Where(h => h.Covered > 0)
        .OrderByDescending(h => h.Covered)
        .ThenByDescending(h => h.FullyCountered)
        .ThenBy(h => h.Hero, StringComparer.OrdinalIgnoreCase);

    public string Explain()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ОБРАТНЫЙ ЗАПРОС: предмет {Item}");
        sb.AppendLine($"  героев в базе: {Heroes.Count}, опасных для предмета: {Dangerous.Count()}, " +
                      $"полностью закрытых: {FullyCovered.Count()}");
        sb.AppendLine();
        sb.AppendLine("ОПАСНАЯ ПАЧКА (предмет не отвечает на эти угрозы):");

        var place = 1;
        foreach (var hero in Pack())
        {
            sb.AppendLine($"  {place++}. {hero.Hero,-12} {hero.Ratio} угроз не закрыто · cf={Certainty.Format(hero.BestCf)} · " +
                          string.Join(", ", hero.UncoveredTags));
        }

        var covered = FullyCovered.Take(8).ToList();
        if (covered.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ПРЕДМЕТ ПОЛНОСТЬЮ ЗАКРЫВАЕТ:");
            foreach (var hero in covered)
                sb.AppendLine($"  • {hero.Hero,-12} все {hero.TotalThreats} угрозы закрыты");
        }

        return sb.ToString();
    }

    public string ExplainBestTargets(int top = 15)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ПРЕДМЕТ: {Item}");
        sb.AppendLine($"  героев в базе: {Heroes.Count}, полностью закрыто: {FullyCovered.Count()}, " +
                      $"опасных (ни одна угроза не закрыта): {Dangerous.Count()}");
        sb.AppendLine();

        sb.AppendLine($"ЛУЧШИЕ ЦЕЛИ (топ-{top}):");
        var place = 1;
        var any = false;
        foreach (var hero in BestTargets().Take(top))
        {
            any = true;
            sb.AppendLine($"  {place++,2}. {hero.Hero,-14} {hero.TargetLabel,-26}" +
                          (hero.CoveredTags.Count > 0
                              ? $"  закрыто: {string.Join(", ", hero.CoveredTags)}"
                              : string.Empty));
        }
        if (!any) sb.AppendLine("  — предмет не закрывает ни одной угрозы ни у одного героя");

        var dangerous = Dangerous.Take(8).ToList();
        if (dangerous.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("ПРОТИВ КОГО ПЛОХ (ни одна угроза не закрыта):");
            foreach (var hero in dangerous)
                sb.AppendLine($"  • {hero.Hero,-14} {hero.Ratio} угроз · " + string.Join(", ", hero.UncoveredTags));
        }

        return sb.ToString();
    }
}

public sealed class ReverseQueryOptions
{
    /// <summary>Предел глубины обратного поиска на один тег.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Предел числа узлов на один тег (защита от фризов на «не доказывается»).</summary>
    public int MaxNodes { get; init; } = 1_500;

    public CancellationToken Cancellation { get; init; }

    /// <summary>Прогресс в формате «сделано/всего · герой · тег».</summary>
    public IProgress<string>? Progress { get; init; }
}

/// <summary>
/// Обратный запрос: по выбранному предмету находит героев, против которых он не работает.
/// Использует обратный вывод: uncovered(предмет, герой, тег) доказывается правилами REV_*,
/// где проверяется наличие профиля героя и отсутствие нужной защиты у предмета.
/// </summary>
public static class ReverseQuery
{
    public static ReverseResult Run(KnowledgeBase kb, string item, ReverseQueryOptions? options = null)
    {
        var opt = options ?? new ReverseQueryOptions();

        var kbWithItem = kb.WithItem(item);
        var memory = BuildInputMemory(kbWithItem);

        // один движок на весь батч: memo шарится между вызовами
        var engine = new BackwardEngine();
        var backwardOptions = new BackwardOptions
        {
            MaxDepth = opt.MaxDepth,
            MaxNodes = opt.MaxNodes,
            UseMemo = true,
            ReuseMemo = true,
            Cancellation = opt.Cancellation
        };

        var result = new ReverseResult { Item = item };
        var heroes = kbWithItem.GetSort("hero")?.Values ?? new List<string>();

        // агрегируем hero_tag за один проход
        var tagMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in heroes) tagMap[h] = new List<string>();
        foreach (var f in kbWithItem.FactsFor("hero_tag"))
        {
            if (f.Atom.Arity != 2) continue;
            var hero = f.Atom.Args[0].Name;
            var tag = f.Atom.Args[1].Name;
            if (tagMap.TryGetValue(hero, out var list) && !list.Contains(tag, StringComparer.OrdinalIgnoreCase))
                list.Add(tag);
        }

        var total = 0;
        foreach (var h in heroes) total += tagMap[h].Count;
        var processed = 0;

        foreach (var hero in heroes.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            if (opt.Cancellation.IsCancellationRequested) return result;

            var verdict = new HeroVerdict { Hero = hero };
            var tags = tagMap[hero];
            verdict.TotalThreats = tags.Count;

            foreach (var tag in tags)
            {
                if (opt.Cancellation.IsCancellationRequested) return result;

                var goal = new Predicate("uncovered", Arg.Const(item), Arg.Const(hero), Arg.Const(tag));
                var proof = engine.Solve(kbWithItem, memory, goal, backwardOptions);

                if (proof.Proven)
                {
                    verdict.Uncovered++;
                    verdict.UncoveredTags.Add(tag);
                    if (proof.Cf > verdict.BestCf) verdict.BestCf = proof.Cf;
                    verdict.Proof ??= proof;
                }
                else
                {
                    verdict.CoveredTags.Add(tag);
                }

                processed++;
                opt.Progress?.Report($"{processed}/{total} · {hero} · {tag}");
            }

            result.Heroes.Add(verdict);
        }

        result.Heroes.Sort((a, b) =>
        {
            var byDanger = b.Dangerous.CompareTo(a.Dangerous);
            if (byDanger != 0) return byDanger;
            var byUncovered = b.Uncovered.CompareTo(a.Uncovered);
            if (byUncovered != 0) return byUncovered;
            return string.Compare(a.Hero, b.Hero, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    internal static WorkingMemory BuildInputMemory(KnowledgeBase kbWithFacts)
    {
        var memory = new WorkingMemory();
        foreach (var f in kbWithFacts.Facts)
            memory.AddInput(new Fact(f.Atom, f.Cf, FactOrigin.Input));
        return memory;
    }
}