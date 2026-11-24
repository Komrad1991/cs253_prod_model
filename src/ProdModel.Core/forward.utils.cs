using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ProdModel.Core;

// ============================================================================
// forward.utils.cs — вспомогательные части прямого вывода
//   Firing         — одно срабатывание правила (шаг журнала вывода)
//   MatchResult    — набор фактов, подошедший под посылки правила
//   Matcher        — перебор посылок и связывание переменных
//   ForwardOptions — ограничители одного запуска
//   ForwardResult  — рабочая память + журнал + счётчики
//   ForwardHelpers — мелкие помощники (цель, ключ экземпляра, циклы)
// Сам алгоритм прямого вывода — в forward.cs.
// ============================================================================

/// <summary>
/// Одно срабатывание продукции: какие посылки совпали, что выведено, на каком слое.
/// Из этих записей строится журнал вывода и все объяснения.
/// </summary>
public sealed class Firing
{
    public required int Step { get; init; }                          // номер шага вывода: 1, 2, 3…
    public required Rule Rule { get; init; }                         // сработавшая продукция
    public required IReadOnlyDictionary<string, string> Bindings { get; init; } // ?переменная = значение
    public required IReadOnlyList<Fact> Premises { get; init; }      // совпавшие факты-посылки
    public required IReadOnlyList<Fact> Conclusions { get; init; }   // что добавлено в память
    public required int Layer { get; init; }                         // слой вывода = max(слой посылок) + 1

    /// <summary>Привязки в виде «?x=a, ?y=b» — для журнала и объяснений.</summary>
    public string BindingsText => Bindings.Count == 0
        ? "—"
        : string.Join(", ", Bindings.Select(kv => $"?{kv.Key}={kv.Value}"));

    /// <summary>Короткая строка для списка срабатываний: «#12 R7 → факт».</summary>
    public override string ToString()
        => $"#{Step} {Rule.Id} → " + string.Join(", ", Conclusions.Select(c => c.Text));
}

// ============================================================================
// Matching
// ============================================================================

/// <summary>
/// Один вариант сопоставления: набор фактов, удовлетворяющий всем посылкам правила,
/// и привязки переменных, полученные при этом переборе.
/// </summary>
public sealed class MatchResult
{
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase); // ?x = a
    public List<Fact> Premises { get; init; } = new(); // найденные факты-посылки (в порядке условий)
    public int ConditionIndex { get; init; }           // сколько посылок проверено (все = совпадение)
}

/// <summary>
/// Сопоставление посылок правила с рабочей памятью: перебор с возвратом.
/// Возвращает все наборы фактов, при которых правило применимо.
/// </summary>
public static class Matcher
{
    /// <summary>Предел совпадений на одно правило — защита от комбинаторного взрыва.</summary>
    public const int DefaultMaxMatches = 5000;

    /// <summary>
    /// Перебирает все наборы фактов, удовлетворяющие посылкам правила.
    /// Отрицательная посылка выполнена, если подходящего факта не существует.
    /// </summary>
    public static List<MatchResult> Match(
        IReadOnlyList<Condition> conditions,
        Func<string, IReadOnlyList<Fact>> lookup,
        out bool truncated,
        int maxMatches = DefaultMaxMatches,
        CancellationToken cancellation = default)
    {
        var results = new List<MatchResult>();

        if (maxMatches <= 0)
        {
            truncated = true;
            return results;
        }

        if (conditions.Count == 0)
        {
            truncated = false;
            results.Add(new MatchResult());
            return results;
        }

        var wasTruncated = false;
        Search(0, NewBindings(), new List<Fact>());
        truncated = wasTruncated;
        return results;

        // Перебор посылок слева направо: связываем переменные и копим найденные факты.
        void Search(int index, Dictionary<string, string> bindings, List<Fact> premises)
        {
            if (wasTruncated)
                return;

            if (cancellation.IsCancellationRequested)
            {
                wasTruncated = true;
                return;
            }

            // Все условия выполнены — найдено совпадение.
            if (index >= conditions.Count)
            {
                if (results.Count >= maxMatches)
                {
                    wasTruncated = true;
                    return;
                }

                results.Add(new MatchResult
                {
                    Bindings = NewBindings(bindings),
                    Premises = new List<Fact>(premises),
                    ConditionIndex = index
                });
                return;
            }

            var condition = conditions[index];

            // Отрицательное условие выполнено, если подходящего факта нет.
            if (condition.Negated)
            {
                if (!AnyMatch(condition.Pattern, bindings, lookup))
                    Search(index + 1, NewBindings(bindings), new List<Fact>(premises));
                return;
            }

            foreach (var fact in lookup(condition.Pattern.Name))
            {
                if (cancellation.IsCancellationRequested)
                {
                    wasTruncated = true;
                    return;
                }

                var nextBindings = NewBindings(bindings);
                if (!condition.Pattern.TryMatch(fact.Atom, nextBindings))
                    continue;

                Search(index + 1, nextBindings, new List<Fact>(premises) { fact });

                if (wasTruncated)
                    return;
            }
        }
    }

    private static Dictionary<string, string> NewBindings()
        => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> NewBindings(IReadOnlyDictionary<string, string> bindings)
        => new(bindings, StringComparer.OrdinalIgnoreCase);

    private static bool AnyMatch(
        Predicate pattern,
        IReadOnlyDictionary<string, string> bindings,
        Func<string, IReadOnlyList<Fact>> lookup)
        => lookup(pattern.Name).Any(fact => MatchesReadonly(pattern, fact.Atom, bindings));

    /// <summary>
    /// Проверка «есть ли подходящий факт» без создания привязок:
    /// нужна только для отрицательных посылок.
    /// </summary>
    private static bool MatchesReadonly(
        Predicate pattern,
        Predicate atom,
        IReadOnlyDictionary<string, string> bindings)
    {
        if (pattern.Arity != atom.Arity ||
            !string.Equals(pattern.Name, atom.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        for (var i = 0; i < pattern.Args.Count; i++)
        {
            var patternArg = pattern.Args[i];
            var valueArg = atom.Args[i];

            if (valueArg.IsVariable)
                return false;

            if (!patternArg.IsVariable)
            {
                if (!string.Equals(patternArg.Name, valueArg.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }

            // Несвязанная переменная — ошибка БЗ, её ловит валидатор.
            if (!bindings.TryGetValue(patternArg.Name, out var bound) ||
                !string.Equals(bound, valueArg.Name, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}

// ============================================================================
// Options and result
// ============================================================================

/// <summary>Ограничители одного запуска прямого вывода — защита от бесконечного перебора.</summary>
public sealed class ForwardOptions
{
    public int MaxPasses { get; init; } = 10000;   // максимум проходов по базе правил
    public int MaxFacts { get; init; } = 50_000;   // максимум фактов в рабочей памяти
    public int MaxMatches { get; init; } = Matcher.DefaultMaxMatches; // совпадений на одно правило
    public CancellationToken Cancellation { get; init; }              // отмена (кнопка «Стоп» в UI)
    public long MaxElapsedMs { get; init; }                           // лимит времени, 0 — без лимита
    public Predicate? Goal { get; init; }                             // цель: как только выведена, вывод останавливается
}

/// <summary>Итог прогона: рабочая память, журнал срабатываний и счётчики для объяснений.</summary>
public sealed class ForwardResult
{
    public required WorkingMemory Memory { get; init; }   // накопленные факты: исходные + выведенные
    public List<Firing> Firings { get; } = new();         // журнал: один элемент — один шаг вывода
    public Predicate? Goal { get; init; }                 // запрошенная цель, если её задавали

    public int Passes { get; set; }            // сколько проходов по правилам выполнено
    public int Layers { get; set; }            // глубина вывода в слоях (по ТЗ нужно ≥ 4)
    public int BlockedRepeats { get; set; }    // повторы: правило снова дало тот же экземпляр
    public int MatchTruncations { get; set; }  // сколько раз сработал предел совпадений
    public int CircularSupports { get; set; }  // циклические подпорки: факт подтверждает сам себя
    public bool LimitReached { get; set; }     // сработал хотя бы один лимит
    public long ElapsedMs { get; set; }        // время прогона, мс
    public Fact? ReachedGoal { get; set; }     // найденный факт цели
    public int GoalReachedAtStep { get; set; } = -1; // номер шага получения цели (-1 — не получена)

    public bool GoalReached => ReachedGoal is not null;              // цель достигнута?
    public int InputCount => Memory.Facts.Count(f => !f.IsDerived);  // исходных фактов
    public int DerivedCount => Memory.Facts.Count(f => f.IsDerived); // выведенных фактов
    public IEnumerable<Fact> FactsByLayer(int layer) => Memory.Facts.Where(f => f.Depth == layer); // один слой
}

// ============================================================================
// Small helpers
// ============================================================================

/// <summary>Мелкие помощники прямого вывода: поиск цели, ключ экземпляра, проверка циклов.</summary>
internal static class ForwardHelpers
{
    /// <summary>Есть ли в памяти факт, подходящий под цель (цель может быть с переменными).</summary>
    internal static Fact? FindGoalMatch(WorkingMemory memory, Predicate goal)
    {
        if (goal.IsGround)
            return memory.Find(goal);

        foreach (var fact in memory.For(goal.Name))
        {
            if (fact.Atom.Arity != goal.Arity)
                continue;

            var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (goal.TryMatch(fact.Atom, bindings))
                return fact;
        }

        return null;
    }

    /// <summary>Ключ экземпляра правила: привязки в устойчивом порядке (чтобы не срабатывать дважды).</summary>
    internal static string Canonical(IReadOnlyDictionary<string, string> bindings)
        => bindings.Count == 0
            ? string.Empty
            : string.Join(
                ";",
                bindings
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value.ToLowerInvariant()));

    /// <summary>
    /// Циклическая подпорка: выводимый факт уже встречается среди посылок,
    /// то есть правило подтверждает само себя. Такие факты считаются отдельно.
    /// </summary>
    internal static bool DependsOn(Predicate atom, IReadOnlyList<Fact> premises)
    {
        if (premises.Count == 0)
            return false;

        var key = atom.Key;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Fact>();

        foreach (var premise in premises)
            stack.Push(premise);

        while (stack.Count > 0)
        {
            var fact = stack.Pop();

            if (!visited.Add(fact.Key))
                continue;

            if (string.Equals(fact.Key, key, StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var parent in fact.Premises)
                stack.Push(parent);
        }

        return false;
    }
}
