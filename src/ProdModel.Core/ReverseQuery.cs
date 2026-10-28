
namespace ProdModel.Core;

/// <summary>Оценка героя относительно выбранного предмета.</summary>
public sealed class HeroVerdict
{
    public required string Hero { get; init; }

    /// <summary>Сколько угроз создаёт герой (по профилю).</summary>
    public int TotalThreats { get; set; }

    /// <summary>Сколько из них предмет не закрывает.</summary>
    public int Uncovered { get; set; }

    public double BestCf { get; set; }

    public List<string> UncoveredTags { get; } = new();

    /// <summary>Доказательство первой незакрытой угрозы (для дерева).</summary>
    public BackwardResult? Proof { get; set; }

    public int Covered => TotalThreats - Uncovered;

    /// <summary>Предмет не отвечает ни на одну угрозу героя — герой опасен.</summary>
    public bool Dangerous => Uncovered > 0 && Covered == 0;

    /// <summary>Предмет закрывает все угрозы героя.</summary>
    public bool FullyCountered => Uncovered == 0;

    public string Level => Dangerous ? "опасен" : FullyCountered ? "закрыт" : "частично";

    public string Ratio => $"{Uncovered}/{TotalThreats}";
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
        .Where(h => !h.Dangerous && !h.FullyCountered)
        .OrderByDescending(h => h.Uncovered);

    public IEnumerable<HeroVerdict> FullyCovered => Heroes
        .Where(h => h.FullyCountered)
        .OrderByDescending(h => h.TotalThreats);

    /// <summary>«Пачка» врагов, против которой предмет плох: самые опасные герои.</summary>
    public List<HeroVerdict> Pack(int size = 6) => Dangerous.Concat(PartiallyCovered).Take(size).ToList();

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
            {
                sb.AppendLine($"  • {hero.Hero,-12} все {hero.TotalThreats} угрозы закрыты");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Обратный запрос: по выбранному предмету находит героев, против которых он не работает.
/// Использует обратный вывод: цель uncovered(предмет, герой, угроза) доказывается правилами REV_*,
/// где проверяется наличие профиля героя и отсутствие нужной защиты у предмета.
/// </summary>
public static class ReverseQuery
{
    public static ReverseResult Run(KnowledgeBase kb, string item)
    {
        var kbWithItem = kb.WithItem(item);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var memory = new ForwardChainer().Run(kbWithItem).Memory.InputOnly();
        Console.Error.WriteLine($"[rev] forward {sw.ElapsedMilliseconds} ms, facts={memory.Count}");
        var engine = new BackwardEngine();
        var result = new ReverseResult { Item = item };

        var heroes = kbWithItem.GetSort("hero")?.Values ?? new List<string>();
        var profiles = kbWithItem.FactsFor("hero_tag");

        foreach (var hero in heroes.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            var verdict = new HeroVerdict { Hero = hero };

            var tags = profiles
                .Where(f => f.Atom.Arity == 2 && f.Atom.Args[0].Name.Equals(hero, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Atom.Args[1].Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            verdict.TotalThreats = tags.Count;
            Console.Error.WriteLine($"[rev] hero {hero}: tags={tags.Count} at {sw.ElapsedMilliseconds} ms");

            foreach (var tag in tags)
            {
                var goal = new Predicate("uncovered", Arg.Const(item), Arg.Const(hero), Arg.Const(tag));
                var proof = engine.Solve(kbWithItem, memory, goal);
                if (!proof.Proven) continue;

                verdict.Uncovered++;
                verdict.UncoveredTags.Add(tag);
                if (proof.Cf > verdict.BestCf) verdict.BestCf = proof.Cf;
                verdict.Proof ??= proof;
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
}
