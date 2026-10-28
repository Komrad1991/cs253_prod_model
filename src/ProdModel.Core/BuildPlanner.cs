
namespace ProdModel.Core;



public sealed class BuildItem
{
    public required string Item { get; init; }
    public required string Need { get; init; }
    public string Slot { get; init; } = string.Empty;
    public double Cf { get; init; }
    public int Cost { get; init; }
    public bool Active { get; init; }

    /// <summary>Почему предмет не попал в сборку (пусто, если попал).</summary>
    public string? Reason { get; set; }

    public override string ToString() => $"{Item} [{Slot}] cf={Certainty.Format(Cf)} — под {Need}";
}

/// <summary>
/// Сборка предметов: из выведенных рекомендаций (pick) выбираются лучшие
/// по уверенности с учётом лимитов слотов (4 оружия / 4 живучести / 4 духа)
/// и ограничения на число активных предметов.
/// </summary>
public sealed class BuildPlan
{
    public const int SlotsPerCategory = 4;
    public const int MaxActives = 3;

    public List<BuildItem> Chosen { get; } = new();
    public List<BuildItem> Overflow { get; } = new();
    public List<BuildItem> Duplicates { get; } = new();

    /// <summary>Второй контр-предмет — ответ на предмет, который купит противник.</summary>
    public List<BuildItem> SecondWave { get; } = new();

    public int ActiveCount => Chosen.Count(i => i.Active);
    public bool ActivesOverLimit => ActiveCount > MaxActives;
    public double TotalCost => Chosen.Sum(i => i.Cost);

    public IEnumerable<BuildItem> ForSlot(string slot) => Chosen.Where(i => i.Slot == slot).OrderByDescending(i => i.Cf);

    /// <summary>Лучший контр-предмет: самый уверенный выбор, закрывающий угрозу (anti_*).</summary>
    public BuildItem? BestCounter => Chosen
        .Where(i => i.Need.StartsWith("anti_", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(i => i.Cf)
        .FirstOrDefault();

    public IEnumerable<BuildItem> TopCounters(int count = 3) => Chosen
        .Where(i => i.Need.StartsWith("anti_", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(i => i.Cf)
        .Take(count);

    public string Explain()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("СБОРКА (агрегация рекомендаций, лимиты слотов 4/4/4, активов не более " + MaxActives + "):");
        foreach (var slot in new[] { "weapon", "vitality", "spirit" })
        {
            var items = ForSlot(slot).ToList();
            sb.AppendLine($"  {slot,-9} ({items.Count}/{SlotsPerCategory}):");
            if (items.Count == 0) sb.AppendLine("      —");
            foreach (var item in items)
            {
                sb.AppendLine($"      • {item.Item,-20} cf={Certainty.Format(item.Cf)}  под {item.Need,-16} цена {item.Cost}" +
                              (item.Active ? "  [активный]" : string.Empty));
            }
        }

        sb.AppendLine($"  Активных предметов: {ActiveCount} из {MaxActives}" + (ActivesOverLimit ? "  ⚠ ПРЕВЫШЕН ЛИМИТ" : string.Empty));
        sb.AppendLine($"  Суммарная цена: {TotalCost:0}");

        if (Overflow.Count > 0)
        {
            sb.AppendLine("  Не влезли в сборку:");
            foreach (var item in Overflow.Take(12))
            {
                sb.AppendLine($"      · {item.Item,-20} cf={Certainty.Format(item.Cf)} ({item.Slot}) — {item.Reason}");
            }
        }

        if (SecondWave.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ВТОРОЙ КОНТР-ПРЕДМЕТ (ответ на предмет, который возьмёт враг):");
            foreach (var item in SecondWave)
            {
                sb.AppendLine($"      • {item.Item,-20} cf={Certainty.Format(item.Cf)}  под {item.Need,-16} цена {item.Cost}" +
                              (item.Active ? "  [активный]" : string.Empty) +
                              (item.Reason is null ? string.Empty : $"  ({item.Reason})"));
            }
        }

        return sb.ToString();
    }
}

public sealed class BuildPlanner
{
    public BuildPlan Build(ForwardResult result, KnowledgeBase kb)
    {
        var plan = new BuildPlan();
        var memory = result.Memory;

        var picks = memory.Facts
            .Where(f => f.Atom.Name.Equals("pick", StringComparison.OrdinalIgnoreCase) && f.Atom.Arity == 2)
            .Select(f => new
            {
                Item = f.Atom.Args[0].Name,
                Need = f.Atom.Args[1].Name,
                f.Cf
            })
            .ToList();

        // по предмету оставляем самое уверенное обоснование
        var best = picks
            .GroupBy(p => p.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Cf).First())
            .OrderByDescending(x => x.Cf)
            .ThenBy(x => x.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var slotCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in best)
        {
            var slot = SecondArg(memory, "item_slot", pick.Item) ?? GuessSlot(kb, pick.Item);
            var costRaw = SecondArg(memory, "item_cost", pick.Item);
            var cost = costRaw is not null && int.TryParse(costRaw, out var c) ? c : 0;
            var active = memory.For("item_active")
                .Any(f => f.Atom.Arity == 1 && f.Atom.Args[0].Name.Equals(pick.Item, StringComparison.OrdinalIgnoreCase));

            var buildItem = new BuildItem
            {
                Item = pick.Item,
                Need = pick.Need,
                Slot = slot,
                Cf = pick.Cf,
                Cost = cost,
                Active = active
            };

            if (plan.Chosen.Any(x => x.Item.Equals(pick.Item, StringComparison.OrdinalIgnoreCase)))
            {
                plan.Duplicates.Add(buildItem);
                continue;
            }

            slotCounts.TryGetValue(slot, out var used);
            if (used >= BuildPlan.SlotsPerCategory)
            {
                buildItem.Reason = "нет свободного слота";
                plan.Overflow.Add(buildItem);
                continue;
            }

            if (active && plan.ActiveCount >= BuildPlan.MaxActives)
            {
                buildItem.Reason = "лимит активных предметов";
                plan.Overflow.Add(buildItem);
                continue;
            }

            slotCounts[slot] = used + 1;
            plan.Chosen.Add(buildItem);
        }

        // второй контр-предмет: то, что выведено как ответ на предмет противника
        var second = memory.Facts
            .Where(f => f.Atom.Name.Equals("second_pick", StringComparison.OrdinalIgnoreCase) && f.Atom.Arity == 2)
            .GroupBy(f => f.Atom.Args[0].Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.Cf).First())
            .OrderByDescending(f => f.Cf);

        foreach (var fact in second)
        {
            var item = fact.Atom.Args[0].Name;

            var costRaw = SecondArg(memory, "item_cost", item);
            var alreadyChosen = plan.Chosen.Any(x => x.Item.Equals(item, StringComparison.OrdinalIgnoreCase));

            plan.SecondWave.Add(new BuildItem
            {
                Item = item,
                Need = fact.Atom.Args[1].Name,
                Slot = "2-й контр",
                Cf = fact.Cf,
                Cost = costRaw is not null && int.TryParse(costRaw, out var c2) ? c2 : 0,
                Active = memory.For("item_active")
                    .Any(f => f.Atom.Arity == 1 && f.Atom.Args[0].Name.Equals(item, StringComparison.OrdinalIgnoreCase)),
                Reason = alreadyChosen ? "уже в основной сборке" : null
            });
        }

        return plan;
    }

    private static string? SecondArg(WorkingMemory memory, string predicate, string first)
    {
        foreach (var fact in memory.For(predicate))
        {
            if (fact.Atom.Arity == 2 && fact.Atom.Args[0].Name.Equals(first, StringComparison.OrdinalIgnoreCase))
                return fact.Atom.Args[1].Name;
        }
        return null;
    }

    private static string GuessSlot(KnowledgeBase kb, string item)
    {
        foreach (var fact in kb.FactsFor("item_slot"))
        {
            if (fact.Atom.Arity == 2 && fact.Atom.Args[0].Name.Equals(item, StringComparison.OrdinalIgnoreCase))
                return fact.Atom.Args[1].Name;
        }
        return "?";
    }
}
