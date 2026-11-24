using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ProdModel.Core;

// ============================================================================
// backward.utils.cs — вспомогательные части обратного вывода
//   SearchState     — состояния узла дерева доказательства
//   SearchNode      — узел дерева (цель + продукции + потомки)
//   RejectedAttempt — отброшенная попытка применить продукцию
//   BackwardOptions — ограничители поиска
//   BackwardResult  — доказательство + счётчики
//   GoalSolutions   — результаты по одной цели (все её экземпляры)
//   BackwardSolver  — обвязка: перебор экземпляров цели и запуск движка
//   BackwardHelpers — мелкие функции над деревом (стоимость, обход)
// Сам алгоритм обратного вывода — в backward.cs.
// ============================================================================

/// <summary>Состояние узла дерева: что с этой подцелью в итоге произошло.</summary>
public enum SearchState
{
    Pending,    // ещё не раскрыт — ждёт в очереди с приоритетом
    Proven,     // доказан: факт есть в памяти или сработала продукция
    Failed,     // не доказан: все продукции испробованы
    Cyclic,     // оборван из-за цикла (цель повторно доказывает сама себя)
    DepthLimit  // превышена предельная глубина
}

/// <summary>
/// Узел дерева обратного вывода: одна подцель. Дети — подцели той продукции,
/// которой цель раскрывается. Дерево строится явно (без рекурсии).
/// </summary>
public sealed class SearchNode
{
    public int Id { get; init; }                                 // порядковый номер узла
    public required Predicate Goal { get; init; }                // что доказываем
    public int Depth { get; init; }                              // слой вывода (корень = 0)
    public SearchNode? Parent { get; init; }                     // родитель (null у корня)
    public bool Negated { get; init; }                           // цель с отрицанием (НЕ …)
    public Rule? Rule { get; set; }                              // какой продукцией доказан
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase); // привязки этой продукции
    public List<SearchNode> Children { get; } = new();           // подцели выбранной продукции
    public SearchState State { get; set; } = SearchState.Pending; // текущее состояние
    public Fact? Fact { get; set; }                              // найденный факт (если цель уже есть в памяти)

    /// <summary>Накопленная стоимость пути от корня (сумма RuleCost применённых продукций).</summary>
    public double Cost { get; set; }

    public string Note { get; set; } = string.Empty;             // пояснение для объяснения/тултипа
    public bool Truncated { get; set; }                          // ветвь оборвана, а не опровергнута
    public List<string> Rejected { get; } = new();               // короткие причины отказов
    public List<RejectedAttempt> RejectedAttempts { get; } = new(); // отказы с продукцией и привязками

    // --- состояние перебора альтернативных продукций ---
    internal RejectedAttempt? Attempt { get; set; }              // попытка, в которую входит узел
    internal List<(Rule Rule, Dictionary<string, string> Bindings)> Candidates { get; } = new(); // все продукции для цели
    internal List<RejectedAttempt> Attempts { get; } = new();    // уже запущенные попытки
    internal int Cursor { get; set; }                            // сколько продукций уже запущено
    internal int ActiveAttempts { get; set; }                    // сколько попыток ещё живо

    /// <summary>Узел ждёт раскрытия следующей порции альтернатив через очередь.</summary>
    internal bool AwaitingAttempts { get; set; }

    internal bool Incomplete { get; set; }   // есть оборванные ветви (глубина/цикл)
    internal bool HitDepth { get; set; }     // хоть одна ветвь упёрлась в глубину
    internal bool HitCycle { get; set; }     // хоть одна ветвь упёрлась в цикл

    public bool Proven => State == SearchState.Proven; // удобная проверка для UI и обходов

    /// <summary>Привязки в виде «?x=a, ?y=b» — для подписи узла.</summary>
    public string BindingsText => Bindings.Count == 0
        ? string.Empty
        : string.Join(", ", Bindings.Select(kv => $"?{kv.Key}={kv.Value}"));

    public string Label => Negated ? "НЕ " + Goal : Goal.ToString(); // подпись узла на графе

    public override string ToString() => $"[{Id}] {Label} — {State}";
}

/// <summary>
/// Попытка доказать цель конкретной продукцией. Если хоть одна подцель не доказана —
/// попытка отбрасывается, а поиск берёт следующую продукцию (перебор вариантов).
/// </summary>
public sealed class RejectedAttempt
{
    public required Rule Rule { get; init; }                              // пробованная продукция
    public required IReadOnlyDictionary<string, string> Bindings { get; init; } // её привязки
    public List<SearchNode> Children { get; } = new();                   // подцели этой продукции
    public string Reason { get; set; } = string.Empty;                   // почему отброшена

    /// <summary>
    /// Попытка отброшена из-за цикла: ветвь замкнута, а поиск переигран
    /// по следующей продукции. Такие ветви показываются на графе отдельно.
    /// </summary>
    public bool CyclePrevented { get; set; }

    internal int Pending { get; set; }   // сколько подцелей ещё не доказано
    internal bool Failed { get; set; }   // попытка провалилась
    internal bool Done { get; set; }     // попытка удалась (все подцели доказаны)
}

/// <summary>Ограничители обратного поиска — чтобы перебор не ушёл в бесконечность.</summary>
public sealed class BackwardOptions
{
    public int MaxDepth { get; init; } = 14;           // предельная глубина дерева (слоёв вывода)
    public int MaxNodes { get; init; } = 30_000;       // предел числа узлов дерева
    public bool UseMemo { get; init; } = true;         // запоминать доказанные/опровергнутые цели
    public bool ReuseMemo { get; init; }               // переиспользовать память между целями
    public int MaxGoalInstances { get; init; } = 4000; // сколько экземпляров цели перебирать

    /// <summary>
    /// Сколько альтернативных продукций одной цели раскрываются одновременно.
    /// Больше единицы — поиск может уйти в соседнюю (более дешёвую) ветвь,
    /// не доводя текущую до конца.
    /// </summary>
    public int AttemptsPerNode { get; init; } = 4;

    public CancellationToken Cancellation { get; init; } // отмена из UI
}

/// <summary>Результат одного обратного вывода: дерево доказательства и счётчики для объяснения.</summary>
public sealed class BackwardResult
{
    public required Predicate Goal { get; init; }         // что доказывали
    public required SearchNode Root { get; init; }        // корень дерева доказательства
    public List<SearchNode> Steps { get; } = new();       // узлы доказательства в порядке вывода
    public List<SearchNode> CycleNodes { get; } = new();  // узлы, где сработала защита от цикла

    public int NodesVisited { get; set; }   // сколько узлов создано всего

    /// <summary>Сколько узлов поиск действительно раскрыл (очередь с приоритетом).</summary>
    public int Expanded { get; set; }

    public int CycleHits { get; set; }      // сколько раз встретился цикл
    public int MemoHits { get; set; }       // сколько раз сработала мемоизация
    public int DepthLimited { get; set; }   // сколько узлов обрезано по глубине
    public bool LimitReached { get; set; }  // сработал лимит узлов или отмена
    public long ElapsedMs { get; set; }     // время поиска, мс
    public int Layers { get; set; }         // глубина доказательства в слоях

    /// <summary>Стоимость найденного доказательства.</summary>
    public double ProofCost { get; set; }

    /// <summary>Сколько раз поиск переключался на соседнюю ветвь.</summary>
    public int BranchSwitches { get; set; }

    public bool Proven => Root.State == SearchState.Proven; // цель доказана

    public bool FailedDueToCycle => !Proven && CycleHits > 0; // не доказали, потому что цикл

    /// <summary>Цикл встретился, но был предотвращён: ветвь отброшена.</summary>
    public bool CyclePrevented => CycleHits > 0;

    /// <summary>Готовая фраза «почему не доказано» — для UI и CLI.</summary>
    public string FailureReason => Root.State switch
    {
        SearchState.Proven => string.Empty,
        SearchState.Cyclic => "цикл предотвращён, но других продукций для цели нет",
        SearchState.DepthLimit => "превышена предельная глубина",
        SearchState.Failed => Root.Note,
        _ => "поиск не завершён"
    };
}

// ============================================================================
// Goal solutions
// ============================================================================

/// <summary>Результаты по одной цели: по одному BackwardResult на каждый экземпляр цели.</summary>
public sealed class GoalSolutions
{
    public required GoalDefinition Goal { get; init; } // объявление цели из файла базы

    public List<BackwardResult> Results { get; } = new();

    public IEnumerable<BackwardResult> Proven => Results.Where(r => r.Proven);                     // успешные

    public IEnumerable<BackwardResult> FailedDueToCycle => Results.Where(r => r.FailedDueToCycle);  // из-за циклов
}

/// <summary>
/// Обвязка над движком: разворачивает цель в конкретные экземпляры (подстановки
/// переменных) и запускает обратный вывод для каждого. Один движок на все цели —
/// поэтому мемоизация переиспользуется и повторный поиск идёт быстрее.
/// </summary>
public static class BackwardSolver
{
    /// <summary>Все экземпляры цели (до cap штук): переменные заменяются константами сорта.</summary>
    public static List<Predicate> EnumerateInstances(
        GoalDefinition goal,
        KnowledgeBase kb,
        int cap = 4000)
    {
        if (cap <= 0)
            return new List<Predicate>();

        if (!goal.HasVariables)
            return new List<Predicate> { goal.Pattern };

        var variables = goal.Pattern.Args
            .Where(a => a.IsVariable)
            .Select(a => a.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var domains = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            IReadOnlyList<string> values;

            if (goal.VariableSorts.TryGetValue(variable, out var sortName))
                values = kb.GetSort(sortName)?.Values ?? new List<string>();
            else
                values = kb.AllConstants().ToList();

            if (values.Count == 0)
                return new List<Predicate>();

            domains[variable] = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var indexes = new int[variables.Count];
        var result = new List<Predicate>();

        // «Одометр»: перебираем комбинации значений переменных, пока не наберём cap.
        while (result.Count < cap)
        {
            var assignment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < variables.Count; i++)
                assignment[variables[i]] = domains[variables[i]][indexes[i]];

            var args = new Arg[goal.Pattern.Arity];

            for (var i = 0; i < args.Length; i++)
            {
                var arg = goal.Pattern.Args[i];
                args[i] = arg.IsVariable ? Arg.Const(assignment[arg.Name]) : arg;
            }

            result.Add(new Predicate(goal.Pattern.Name, args));

            var carry = true;

            for (var i = variables.Count - 1; i >= 0 && carry; i--)
            {
                indexes[i]++;

                if (indexes[i] < domains[variables[i]].Count)
                    carry = false;
                else
                    indexes[i] = 0;
            }

            if (carry)
                break;
        }

        return result;
    }

    /// <summary>Обратный вывод по всем целям базы (используется CLI, UI и отчётом).</summary>
    public static List<GoalSolutions> SolveAll(
        KnowledgeBase kb,
        WorkingMemory facts,
        IEnumerable<GoalDefinition> goals,
        BackwardOptions? options = null)
    {
        var opt = options ?? new BackwardOptions();

        // Один движок на все цели, поэтому мемоизация переиспользуется (ReuseMemo).
        var batchOptions = new BackwardOptions
        {
            MaxDepth = opt.MaxDepth,
            MaxNodes = opt.MaxNodes,
            UseMemo = opt.UseMemo,
            ReuseMemo = true,
            MaxGoalInstances = opt.MaxGoalInstances,
            AttemptsPerNode = opt.AttemptsPerNode,
            Cancellation = opt.Cancellation
        };

        var engine = new BackwardEngine();
        var results = new List<GoalSolutions>();

        foreach (var goal in goals)
        {
            if (opt.Cancellation.IsCancellationRequested)
                break;

            var holder = new GoalSolutions { Goal = goal };

            foreach (var instance in EnumerateInstances(goal, kb, opt.MaxGoalInstances))
            {
                if (opt.Cancellation.IsCancellationRequested)
                    break;

                holder.Results.Add(engine.Solve(kb, facts, instance, batchOptions));
            }

            results.Add(holder);
        }

        return results;
    }
}

// ============================================================================
// Small helpers
// ============================================================================

/// <summary>
/// Функции над деревом доказательства, не зависящие от состояния поиска:
/// стоимость продукции, ключ мемоизации, обход доказанной части дерева.
/// </summary>
internal static class BackwardHelpers
{
    /// <summary>
    /// Стоимость продукции: единица за шаг вывода плюс надбавка за каждую посылку
    /// (посылка — отдельная подцель), поэтому короткие продукции пробуются раньше.
    /// </summary>
    internal static double RuleCost(Rule rule) => 1.0 + 0.1 * rule.Conditions.Count;

    /// <summary>Ключ мемоизации: имя цели с учётом отрицания.</summary>
    internal static string MemoKey(SearchNode node)
        => (node.Negated ? "!" : string.Empty) + node.Goal.Key;

    /// <summary>Копия привязок — словарь с нечувствительностью к регистру.</summary>
    internal static Dictionary<string, string> CopyBindings(IReadOnlyDictionary<string, string> bindings)
        => new(bindings, StringComparer.OrdinalIgnoreCase);

    /// <summary>Обход доказанной части дерева в произвольном порядке.</summary>
    internal static IEnumerable<SearchNode> ProvenSubtree(SearchNode root)
    {
        var stack = new Stack<SearchNode>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (!node.Proven)
                continue;

            yield return node;

            foreach (var child in node.Children)
                stack.Push(child);
        }
    }

    /// <summary>
    /// Стоимость доказательства: сумма стоимостей продукций, реально вошедших
    /// в доказанное поддерево. Отброшенные ветви в неё не попадают.
    /// </summary>
    internal static double ProofCostOf(SearchNode root)
    {
        var total = 0.0;

        foreach (var node in ProvenSubtree(root))
        {
            if (node.Rule is not null)
                total += RuleCost(node.Rule);
        }

        return total;
    }

    /// <summary>Глубина доказательства в слоях (0, если не доказано).</summary>
    internal static int MaxProvenDepth(SearchNode root)
    {
        var max = 0;

        foreach (var node in ProvenSubtree(root))
        {
            if (node.Depth > max)
                max = node.Depth;
        }

        return max;
    }

    /// <summary>Собирает доказанные узлы в порядке «сначала посылки, потом заключение».</summary>
    internal static void CollectSteps(SearchNode root, List<SearchNode> steps)
    {
        var stack = new Stack<(SearchNode Node, bool Processed)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, processed) = stack.Pop();

            if (processed)
            {
                if (node.Proven)
                    steps.Add(node);

                continue;
            }

            stack.Push((node, true));

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                if (node.Children[i].Proven)
                    stack.Push((node.Children[i], false));
            }
        }
    }
}