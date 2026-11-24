using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using static ProdModel.Core.BackwardHelpers;

namespace ProdModel.Core;

// ============================================================================
// backward.cs — обратный вывод (goal-driven: от цели к фактам)
//   Solve() строит дерево доказательства «лучший первым»: очередь с приоритетом
//   всегда отдаёт узел с минимальной оценкой f = g + h (best-first search,
//   отсечение по границе стоимости — branch and bound). Рекурсии нет — только
//   явные структуры данных (SearchNode + очередь).
//   Типы данных, опции и BackwardSolver — в backward.utils.cs.
// ============================================================================

/// <summary>
/// Движок обратного вывода. Один экземпляр можно переиспользовать для многих
/// целей: мемоизация доказанных/опровергнутых целей сохраняется между вызовами.
/// </summary>
public sealed class BackwardEngine
{
    // Мемоизация: уже доказанные и уже опровергнутые цели (ключ — имя цели).
    private readonly HashSet<string> _provenMemo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedMemo = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SearchNode> _nodes = new(); // все созданные узлы (счётчики и лимит)

    private BackwardOptions _options = new();  // ограничители текущего поиска
    private WorkingMemory _facts = null!;      // стартовая память: исходные факты
    private KnowledgeBase _kb = null!;         // база правил
    private bool _aborted;                     // поиск прерван (отмена или лимит узлов)
    private long _sequence;                    // порядок постановки в очередь при равной оценке
    private int _branchSwitches;               // сколько раз переключались на соседнюю ветвь
    private SearchNode? _lastExpanded;         // последний раскрытый узел (для подсчёта переключений)

    /// <summary>Доказать одну конкретную цель. Один вызов = одно дерево доказательства.</summary>
    public BackwardResult Solve(
        KnowledgeBase kb,
        WorkingMemory facts,
        Predicate goal,
        BackwardOptions? options = null)
    {
        if (kb is null) throw new ArgumentNullException(nameof(kb));
        if (facts is null) throw new ArgumentNullException(nameof(facts));

        var sw = Stopwatch.StartNew();

        // Мемоизация зависит от базы и стартовой памяти: при их смене
        // результаты прошлого поиска неприменимы.
        if (!ReferenceEquals(_kb, kb) || !ReferenceEquals(_facts, facts))
        {
            _provenMemo.Clear();
            _failedMemo.Clear();
        }

        _kb = kb;
        _facts = facts;
        _options = options ?? new BackwardOptions();

        if (!_options.ReuseMemo)
        {
            _provenMemo.Clear();
            _failedMemo.Clear();
        }

        _nodes.Clear();
        _aborted = false;
        _sequence = 0;
        _branchSwitches = 0;
        _lastExpanded = null;

        var root = new SearchNode { Id = 0, Goal = goal, Depth = 0 };
        _nodes.Add(root);

        var result = new BackwardResult { Goal = goal, Root = root };

        Search(root, result);

        result.NodesVisited = _nodes.Count;
        result.LimitReached = _aborted;
        result.BranchSwitches = _branchSwitches;
        result.Layers = root.Proven ? MaxProvenDepth(root) : 0;
        result.ProofCost = root.Proven ? ProofCostOf(root) : 0.0;

        CollectSteps(root, result.Steps);

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;

        return result;
    }

    /// <summary>
    /// Итеративный обратный вывод: дерево доказательства строится явно (SearchNode),
    /// а обход ведёт очередь с приоритетом — рекурсии нет. Из очереди всегда берётся
    /// узел с минимальной оценкой f = g + h, где g — стоимость уже применённых
    /// продукций, h — нижняя оценка оставшейся работы. Поэтому дорогая ветвь
    /// не доводится до конца: как только в очереди появляется более дешёвая
    /// соседняя ветвь, поиск переключается на неё (best-first search
    /// с отсечением по границе стоимости — branch and bound).
    /// </summary>
    private void Search(SearchNode root, BackwardResult result)
    {
        var frontier = new PriorityQueue<SearchNode, (double Cost, long Order)>();
        Push(frontier, root);

        while (frontier.Count > 0)
        {
            if (_aborted || root.State != SearchState.Pending)
                break;

            if (_options.Cancellation.IsCancellationRequested)
            {
                _aborted = true;
                break;
            }

            var node = frontier.Dequeue();

            // Ветвь могла быть закрыта другим путём, пока узел ждал очереди.
            if (node.State != SearchState.Pending)
                continue;

            var owner = node.Attempt;
            if (owner is not null && (owner.Failed || owner.Done))
                continue;

            TrackBranchSwitch(node);
            result.Expanded++;

            if (node.AwaitingAttempts)
            {
                node.AwaitingAttempts = false;
                StartAttempts(node, result, frontier);
                continue;
            }

            Expand(node, result, frontier);
        }

        if (root.State == SearchState.Pending)
        {
            _aborted = true;
            Finish(root, SearchState.Failed, "поиск прерван или достигнут лимит узлов", truncated: true);
        }
    }

    /// <summary>
    /// Считает переключения между ветвями: если раскрываемый узел не является
    /// продолжением предыдущего, поиск ушёл в соседнюю (более дешёвую) ветвь.
    /// </summary>
    private void TrackBranchSwitch(SearchNode node)
    {
        if (_lastExpanded is not null && !IsDescendantOf(node, _lastExpanded))
            _branchSwitches++;

        _lastExpanded = node;
    }

    /// <summary>Является ли узел продолжением указанного предка (для подсчёта переключений).</summary>
    private static bool IsDescendantOf(SearchNode node, SearchNode ancestor)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>Кладёт узел в очередь с приоритетом: f = g + h, меньше — раньше.</summary>
    private void Push(PriorityQueue<SearchNode, (double Cost, long Order)> frontier, SearchNode node)
        => frontier.Enqueue(node, (node.Cost + Heuristic(node), _sequence++));

    /// <summary>
    /// Нижняя оценка оставшейся работы: известный факт закрывает ветвь бесплатно,
    /// любая другая цель требует хотя бы одной продукции. Оценка не завышает
    /// стоимость, поэтому обход остаётся корректным.
    /// </summary>
    private double Heuristic(SearchNode node)
        => _facts.Find(node.Goal) is not null ? 0.0 : 1.0;

    /// <summary>Цель уже раскрывается выше в этой же ветви? Тогда это цикл.</summary>
    private static bool IsOnCurrentPath(SearchNode node, string key)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (string.Equals(MemoKey(current), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Закрывает узел с данным состоянием и поднимает результат по дереву.</summary>
    private void Settle(
        SearchNode node,
        SearchState state,
        string note,
        BackwardResult result,
        PriorityQueue<SearchNode, (double Cost, long Order)> frontier,
        bool truncated = false,
        bool memoizable = false)
    {
        Finish(node, state, note, truncated, memoizable);
        Complete(node, result, frontier);
    }

    /// <summary>
    /// Раскрытие узла: сначала дешёвые проверки — отмена, готовый факт, глубина,
    /// мемоизация, отрицание, цикл — и только потом перебор продукций.
    /// </summary>
    private void Expand(
        SearchNode node,
        BackwardResult result,
        PriorityQueue<SearchNode, (double Cost, long Order)> frontier)
    {
        if (_options.Cancellation.IsCancellationRequested)
        {
            _aborted = true;
            Settle(node, SearchState.Failed, "поиск отменён", result, frontier, truncated: true);
            return;
        }

        // Проверяем исходные и уже выведенные факты.
        // Факт замыкает ветвь, поэтому предельная глубина к нему не применяется.
        var knownFact = _facts.Find(node.Goal);

        if (knownFact is not null)
        {
            if (node.Negated)
            {
                Settle(node, SearchState.Failed, "факт присутствует — отрицание ложно", result, frontier);
                return;
            }

            node.Fact = knownFact;
            Settle(node, SearchState.Proven, string.Empty, result, frontier, memoizable: true);
            return;
        }

        if (node.Depth > _options.MaxDepth)
        {
            result.DepthLimited++;
            Settle(
                node,
                SearchState.DepthLimit,
                $"превышена глубина поиска: {_options.MaxDepth}",
                result,
                frontier,
                truncated: true);
            return;
        }

        var key = MemoKey(node);

        // Ранее доказанная или опровергнутая цель.
        if (_options.UseMemo)
        {
            if (_provenMemo.Contains(key))
            {
                result.MemoHits++;
                Settle(node, SearchState.Proven, "цель доказана ранее", result, frontier);
                return;
            }

            if (_failedMemo.Contains(key))
            {
                result.MemoHits++;
                Settle(node, SearchState.Failed, "цель ранее не была доказана", result, frontier);
                return;
            }
        }

        var candidates = Candidates(node.Goal);

        // Правил для цели нет — отрицание истинно при отсутствии самого факта.
        if (node.Negated && candidates.Count == 0)
        {
            Settle(
                node,
                SearchState.Proven,
                "факта и правил нет — отрицание истинно",
                result,
                frontier,
                memoizable: true);
            return;
        }

        // Цикл: та же цель уже раскрывается в этой ветви. Ветвь обрывается,
        // а не раскрывается бесконечно; попытка помечается как провалившаяся,
        // поэтому поиск переигрывает цель по следующей продукции.
        if (IsOnCurrentPath(node, key))
        {
            result.CycleHits++;
            result.CycleNodes.Add(node);
            Settle(
                node,
                SearchState.Cyclic,
                "повторное доказательство цели в этой же ветви",
                result,
                frontier,
                truncated: true);
            return;
        }

        if (candidates.Count == 0)
        {
            Settle(node, SearchState.Failed, "нет продукций с таким заключением", result, frontier);
            return;
        }

        node.Candidates.Clear();
        node.Candidates.AddRange(candidates);

        StartAttempts(node, result, frontier);
    }

    private static Dictionary<string, string> CopyBindings(IReadOnlyDictionary<string, string> bindings)
        => new(bindings, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Раскрывает очередную порцию альтернативных продукций цели. Продукции
    /// упорядочены по стоимости, поэтому первыми пробуются самые дешёвые,
    /// а остальные остаются в резерве до провала текущей порции.
    /// </summary>
    private void StartAttempts(
        SearchNode node,
        BackwardResult result,
        PriorityQueue<SearchNode, (double Cost, long Order)> frontier)
    {
        while (true)
        {
            var started = 0;

            while (node.Cursor < node.Candidates.Count && started < _options.AttemptsPerNode)
            {
                if (_aborted)
                    return;

                var candidate = node.Candidates[node.Cursor++];
                var children = TryBuildChildren(node, candidate.Rule, candidate.Bindings, out var unsafeRule);

                if (children is null)
                {
                    if (_aborted)
                        return;

                    if (unsafeRule)
                    {
                        node.Incomplete = true;
                        node.Rejected.Add($"{candidate.Rule.Id} — правило содержит несвязанные переменные");
                    }

                    continue;
                }

                var attempt = new RejectedAttempt
                {
                    Rule = candidate.Rule,
                    Bindings = CopyBindings(candidate.Bindings),
                    Pending = children.Count
                };

                foreach (var child in children)
                {
                    child.Attempt = attempt;
                    child.Cost = node.Cost + RuleCost(candidate.Rule);
                    attempt.Children.Add(child);
                }

                node.Attempts.Add(attempt);
                node.ActiveAttempts++;
                started++;

                // Первая попытка задаёт видимую ветвь дерева, даже если она провалится.
                if (node.Children.Count == 0)
                {
                    node.Rule = candidate.Rule;
                    node.Bindings = CopyBindings(candidate.Bindings);
                    node.Children.AddRange(children);
                }

                if (children.Count == 0)
                {
                    ResolveByEmptyRule(node, attempt, result, frontier);
                    return;
                }

                foreach (var child in children)
                    Push(frontier, child);
            }

            if (node.ActiveAttempts > 0)
                return;

            if (node.Cursor < node.Candidates.Count)
                continue;

            ResolveFailure(node);
            Complete(node, result, frontier);
            return;
        }
    }

    /// <summary>Продукция без посылок доказывает цель сразу.</summary>
    private void ResolveByEmptyRule(
        SearchNode node,
        RejectedAttempt attempt,
        BackwardResult result,
        PriorityQueue<SearchNode, (double Cost, long Order)> frontier)
    {
        attempt.Done = true;
        node.ActiveAttempts--;

        if (node.Negated)
        {
            Settle(node, SearchState.Failed, "отрицание ложно: положительная цель доказана", result, frontier);
            return;
        }

        node.Rule = attempt.Rule;
        node.Bindings = CopyBindings(attempt.Bindings);
        Settle(node, SearchState.Proven, "правило без посылок", result, frontier, memoizable: true);
    }

    /// <summary>
    /// Поднимает результат вверх по дереву: доказанный узел уменьшает счётчик
    /// недоказанных посылок попытки, а проваленный закрывает попытку и заставляет
    /// родителя перейти к следующей продукции.
    /// </summary>
    private void Complete(
        SearchNode node,
        BackwardResult result,
        PriorityQueue<SearchNode, (double Cost, long Order)> frontier)
    {
        var pending = new Stack<SearchNode>();
        pending.Push(node);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var attempt = current.Attempt;
            var parent = current.Parent;

            if (attempt is null || parent is null ||
                parent.State != SearchState.Pending ||
                attempt.Failed || attempt.Done)
            {
                break;
            }

            if (current.Proven)
            {
                if (--attempt.Pending > 0)
                    break;

                attempt.Done = true;

                if (parent.Negated)
                {
                    Finish(parent, SearchState.Failed, "отрицание ложно: положительная цель доказана");
                }
                else
                {
                    parent.Rule = attempt.Rule;
                    parent.Bindings = CopyBindings(attempt.Bindings);
                    parent.Children.Clear();
                    parent.Children.AddRange(attempt.Children);
                    Finish(parent, SearchState.Proven, string.Empty, memoizable: true);
                }

                pending.Push(parent);
                continue;
            }

            attempt.Failed = true;
            parent.ActiveAttempts--;
            parent.Incomplete |= current.Truncated;
            parent.HitDepth |= current.State == SearchState.DepthLimit;
            parent.HitCycle |= current.State == SearchState.Cyclic;

            RecordRejected(parent, attempt, current);

            if (parent.ActiveAttempts > 0)
                break;

            if (parent.Cursor < parent.Candidates.Count)
            {
                parent.AwaitingAttempts = true;
                Push(frontier, parent);
                break;
            }

            ResolveFailure(parent);
            pending.Push(parent);
        }
    }

    /// <summary>Итог перебора: узел закрывается провалом, отрицанием или циклом.</summary>
    private void ResolveFailure(SearchNode node)
    {
        if (_aborted)
        {
            Finish(node, SearchState.Failed, "поиск прерван или достигнут лимит узлов", truncated: true);
            return;
        }

        if (node.Incomplete)
        {
            if (node.HitDepth)
                Finish(node, SearchState.DepthLimit, "одна из ветвей превысила допустимую глубину", truncated: true);
            else if (node.HitCycle)
                Finish(node, SearchState.Cyclic, $"подцель {node.Goal} упирается в цикл", truncated: true);
            else
                Finish(node, SearchState.Failed, "поиск не завершён полностью", truncated: true);

            return;
        }

        if (node.Negated)
            Finish(node, SearchState.Proven, "положительная цель не доказана — отрицание истинно", memoizable: true);
        else
            Finish(node, SearchState.Failed, "все подходящие правила испробованы");
    }

    /// <summary>Фиксирует состояние узла и запоминает результат для мемоизации.</summary>
    private void Finish(
        SearchNode node,
        SearchState state,
        string note,
        bool truncated = false,
        bool memoizable = false)
    {
        node.State = state;
        node.Note = note;
        node.Truncated |= truncated;

        if (!_options.UseMemo || node.Truncated)
            return;

        var key = MemoKey(node);

        if (memoizable && state == SearchState.Proven)
            _provenMemo.Add(key);

        if (state == SearchState.Failed)
            _failedMemo.Add(key);
    }

    /// <summary>
    /// Создаёт узлы-подцели для продукции. null — правило непригодно:
    /// есть несвязанные переменные (unsafeRule) или превышен лимит узлов.
    /// </summary>
    private List<SearchNode>? TryBuildChildren(
        SearchNode parent,
        Rule rule,
        Dictionary<string, string> bindings,
        out bool unsafeRule)
    {
        unsafeRule = false;

        var atoms = new List<(Predicate Atom, bool Negated)>(rule.Conditions.Count);

        // Сначала проверяем все посылки: частично построенное правило в дерево не попадает.
        foreach (var condition in rule.Conditions)
        {
            if (_options.Cancellation.IsCancellationRequested)
            {
                _aborted = true;
                return null;
            }

            var atom = condition.Pattern.Substitute(bindings);

            if (!atom.IsGround)
            {
                unsafeRule = true;
                return null;
            }

            atoms.Add((atom, condition.Negated));
        }

        if (_nodes.Count + atoms.Count > _options.MaxNodes)
        {
            _aborted = true;
            return null;
        }

        var children = new List<SearchNode>(atoms.Count);

        foreach (var item in atoms)
        {
            var child = new SearchNode
            {
                Id = _nodes.Count,
                Goal = item.Atom,
                Depth = parent.Depth + 1,
                Parent = parent,
                Negated = item.Negated
            };

            _nodes.Add(child);
            children.Add(child);
        }

        return children;
    }

    /// <summary>Сохраняет отброшенную попытку: причина отказа и признак «отброшено из-за цикла».</summary>
    private static void RecordRejected(SearchNode node, RejectedAttempt attempt, SearchNode failing)
    {
        attempt.CyclePrevented = failing.State == SearchState.Cyclic || failing.HitCycle;
        attempt.Reason = !string.IsNullOrWhiteSpace(failing.Note) ? failing.Note : "правило не подтвердилось";
        node.RejectedAttempts.Add(attempt);
    }

    /// <summary>
    /// Продукции, способные доказать цель, в порядке возрастания стоимости:
    /// сначала короткие продукции. Это эвристика «сначала самое дешёвое»,
    /// которая сокращает перебор.
    /// </summary>
    private List<(Rule Rule, Dictionary<string, string> Bindings)> Candidates(Predicate goal)
    {
        var result = new List<(Rule Rule, Dictionary<string, string> Bindings)>();

        foreach (var rule in _kb.RulesFor(goal.Name))
        {
            foreach (var conclusion in rule.Conclusions)
            {
                var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!conclusion.TryUnify(goal, bindings))
                    continue;

                result.Add((rule, bindings));
                break;
            }
        }

        // OrderBy устойчива: при равной стоимости сохраняется порядок файла.
        return result
            .OrderBy(c => RuleCost(c.Rule))
            .ThenBy(c => c.Rule.Conditions.Count)
            .ThenBy(c => c.Rule.Id, StringComparer.Ordinal)
            .ToList();
    }
}
