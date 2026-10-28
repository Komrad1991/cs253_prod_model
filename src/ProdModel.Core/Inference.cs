using System.Diagnostics;

namespace ProdModel.Core;


/// <summary>Одно успешное применение правила: посылки, подстановка, заключение, коэффициент.</summary>
public sealed class Firing
{
    public required int Step { get; init; }
    public required Rule Rule { get; init; }
    public required IReadOnlyDictionary<string, string> Bindings { get; init; }
    public required IReadOnlyList<Fact> Premises { get; init; }
    public required IReadOnlyList<Fact> Conclusions { get; init; }

    /// <summary>Коэффициент вывода: cf(правило) * min(cf посылок).</summary>
    public required double Cf { get; init; }

    /// <summary>min(cf посылок) на момент срабатывания (посылки могли усилиться позже).</summary>
    public required double PremiseCf { get; init; }

    public required double RuleCf { get; init; }

    /// <summary>Слой вывода (глубина цепочки).</summary>
    public required int Layer { get; init; }

    public string BindingsText => Bindings.Count == 0
        ? "—"
        : string.Join(", ", Bindings.Select(kv => $"?{kv.Key}={kv.Value}"));

    public override string ToString() => $"#{Step} {Rule.Id} → {string.Join(", ", Conclusions.Select(c => c.Text))} [cf={Certainty.Format(Cf)}]";
}


public sealed class MatchResult
{
    public Dictionary<string, string> Bindings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Fact> Premises { get; init; } = new();
}

/// <summary>
/// Сопоставление посылок правила с рабочей памятью (соединение по всем условиям).
/// Реализовано итеративно, явным стеком — без рекурсии.
/// </summary>
public static class Matcher
{
    public static List<MatchResult> Match(IReadOnlyList<Condition> conditions, Func<string, IReadOnlyList<Fact>> lookup)
    {
        var results = new List<MatchResult>();
        if (conditions.Count == 0)
        {
            results.Add(new MatchResult());
            return results;
        }

        var stack = new Stack<MatchResult>();
        stack.Push(new MatchResult());

        while (stack.Count > 0)
        {
            var partial = stack.Pop();
            var index = partial.Premises.Count;
            if (index == conditions.Count)
            {
                results.Add(partial);
                continue;
            }

            var condition = conditions[index];
            if (condition.Negated)
            {
                if (!AnyMatch(condition.Pattern, partial.Bindings, lookup))
                {
                    stack.Push(new MatchResult
                    {
                        Bindings = new Dictionary<string, string>(partial.Bindings, StringComparer.OrdinalIgnoreCase),
                        Premises = new List<Fact>(partial.Premises)
                    });
                }
                continue;
            }

            foreach (var fact in lookup(condition.Pattern.Name))
            {
                var bindings = new Dictionary<string, string>(partial.Bindings, StringComparer.OrdinalIgnoreCase);
                if (!condition.Pattern.TryMatch(fact.Atom, bindings)) continue;

                var premises = new List<Fact>(partial.Premises) { fact };
                stack.Push(new MatchResult { Bindings = bindings, Premises = premises });
            }
        }

        return results;
    }

    private static bool AnyMatch(Predicate pattern, IReadOnlyDictionary<string, string> bindings, Func<string, IReadOnlyList<Fact>> lookup)
    {
        foreach (var fact in lookup(pattern.Name))
        {
            var probe = new Dictionary<string, string>(bindings, StringComparer.OrdinalIgnoreCase);
            if (pattern.TryMatch(fact.Atom, probe)) return true;
        }
        return false;
    }
}



public sealed class ForwardOptions
{
    /// <summary>Защита от зацикливания на уровне проходов.</summary>
    public int MaxPasses { get; init; } = 200;

    /// <summary>Защита от комбинаторного взрыва.</summary>
    public int MaxFacts { get; init; } = 50_000;

    /// <summary>Порог уверенности, ниже которого факт не записывается.</summary>
    public double Threshold { get; init; } = Certainty.DefaultThreshold;
}

public sealed class ForwardResult
{
    public required WorkingMemory Memory { get; init; }
    public List<Firing> Firings { get; } = new();
    public int Passes { get; set; }
    public int Layers { get; set; }
    public int BlockedRepeats { get; set; }

    /// <summary>Сколько раз обоснование опиралось на сам выводимый факт (циклическая поддержка).</summary>
    public int CircularSupports { get; set; }

    public bool LimitReached { get; set; }
    public long ElapsedMs { get; set; }

    public int InputCount => Memory.Facts.Count(f => !f.IsDerived);
    public int DerivedCount => Memory.Facts.Count(f => f.IsDerived);

    public IEnumerable<Fact> FactsByLayer(int layer) => Memory.Facts.Where(f => f.Depth == layer);
}

/// <summary>
/// Прямой вывод (data-driven): от фактов к заключениям, проход за проходом,
/// пока не будет достигнута неподвижная точка.
///
/// Защита от циклов:
///  * каждая конкретная подстановка правила (правило + привязка переменных) срабатывает не более одного раза;
///  * число проходов ограничено;
///  * размер рабочей памяти ограничен.
/// </summary>
public sealed class ForwardChainer
{
    public ForwardResult Run(KnowledgeBase kb, ForwardOptions? options = null)
    {
        var opt = options ?? new ForwardOptions();
        var sw = Stopwatch.StartNew();

        var memory = new WorkingMemory();
        foreach (var fact in kb.Facts)
        {
            memory.AddInput(new Fact(fact.Atom, fact.Cf, FactOrigin.Input));
        }

        var result = new ForwardResult { Memory = memory };

        var fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var step = 0;

        for (var pass = 1; pass <= opt.MaxPasses; pass++)
        {
            result.Passes = pass;
            var changed = false;

            foreach (var rule in kb.Rules)
            {
                var matches = Matcher.Match(rule.Conditions, memory.For);
                foreach (var match in matches)
                {
                    var instanceKey = rule.Id + "|" + Canonical(match.Bindings);
                    if (!fired.Add(instanceKey))
                    {
                        blocked.Add(instanceKey);
                        continue;
                    }

                    var premiseCf = Certainty.MinCf(match.Premises.Select(p => p.Cf));
                    var premiseDepth = match.Premises.Count == 0 ? 0 : match.Premises.Max(p => p.Depth);
                    var cf = Certainty.RuleConclusion(rule.Cf, premiseCf);
                    var layer = premiseDepth + 1;

                    if (cf < opt.Threshold) continue;

                    var conclusions = new List<Fact>();
                    foreach (var head in rule.Conclusions)
                    {
                        var atom = head.Substitute(match.Bindings);
                        if (!atom.IsGround) continue;

                        var existing = memory.Find(atom);
                        if (existing is null)
                        {
                            var created = memory.AddDerived(atom, cf, layer, rule, match.Premises, step + 1);
                            conclusions.Add(created);
                            changed = true;
                        }
                        else
                        {
                            conclusions.Add(existing);

                            // Циклическая поддержка: факт опирается на самого себя.
                            // Такую цепочку не учитываем в уверенности, иначе цикл
                            // искусственно «подтверждал» бы сам себя.
                            var circular = DependsOn(atom, match.Premises);

                            existing.Derivations.Add(new Derivation
                            {
                                Rule = rule,
                                Cf = cf,
                                RuleCf = rule.Cf,
                                Step = step + 1,
                                Layer = layer,
                                Premises = match.Premises,
                                Circular = circular
                            });

                            if (circular)
                            {
                                result.CircularSupports++;
                            }
                            else
                            {
                                if (existing.IsDerived)
                                {
                                    var before = existing.Cf;
                                    existing.Cf = Certainty.Combine(existing.Cf, cf);
                                    existing.Depth = Math.Min(existing.Depth, layer);
                                    if (existing.Cf - before > 1e-9) changed = true;
                                }
                                else
                                {
                                    // входной факт подтверждён правилом — уверенность не понижаем
                                    existing.Cf = Math.Max(existing.Cf, cf);
                                }

                                if (cf > existing.BestCf + 1e-9)
                                {
                                    existing.BestCf = cf;
                                    existing.Rule = rule;
                                    existing.RuleCf = rule.Cf;
                                    existing.Premises.Clear();
                                    existing.Premises.AddRange(match.Premises);
                                }
                            }
                        }
                    }

                    if (conclusions.Count == 0) continue;

                    step++;
                    result.Firings.Add(new Firing
                    {
                        Step = step,
                        Rule = rule,
                        Bindings = match.Bindings,
                        Premises = match.Premises,
                        Conclusions = conclusions,
                        Cf = cf,
                        PremiseCf = premiseCf,
                        RuleCf = rule.Cf,
                        Layer = layer
                    });

                    if (memory.Count > opt.MaxFacts)
                    {
                        result.LimitReached = true;
                        break;
                    }
                }

                if (result.LimitReached) break;
            }

            if (result.LimitReached || !changed) break;
        }

        result.BlockedRepeats = blocked.Count;
        result.Layers = memory.MaxDerivedDepth();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    internal static string Canonical(IReadOnlyDictionary<string, string> bindings)
    {
        if (bindings.Count == 0) return string.Empty;
        return string.Join(";", bindings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value.ToLowerInvariant()));
    }

    /// <summary>Проверяет, опирается ли вывод атома на сам этот атом (идя по посылкам).</summary>
    private static bool DependsOn(Predicate atom, IReadOnlyList<Fact> premises)
    {
        if (premises.Count == 0) return false;

        var key = atom.Key;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Fact>();

        foreach (var premise in premises) stack.Push(premise);

        while (stack.Count > 0)
        {
            var fact = stack.Pop();
            if (!visited.Add(fact.Key)) continue;
            if (string.Equals(fact.Key, key, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var parent in fact.Premises) stack.Push(parent);
        }

        return false;
    }
}



public enum SearchState
{
    Pending,
    Proven,
    Failed,
    Cyclic,
    DepthLimit
}

/// <summary>Узел дерева/графа поиска обратного вывода.</summary>
public sealed class SearchNode
{
    public int Id { get; init; }
    public required Predicate Goal { get; init; }
    public int Depth { get; init; }
    public SearchNode? Parent { get; init; }
    public bool Negated { get; init; }

    public Rule? Rule { get; set; }
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SearchNode> Children { get; } = new();

    public SearchState State { get; set; } = SearchState.Pending;
    public double Cf { get; set; }

    /// <summary>Если цель доказана фактом рабочей памяти.</summary>
    public Fact? Fact { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>Ветвь оборвана ограничением (глубина/лимит узлов), а не логикой.</summary>
    public bool Truncated { get; set; }

    public List<string> Rejected { get; } = new();

    /// <summary>Отвергнутые попытки применить правило — с сохранением поддерева (для визуализации).</summary>
    public List<RejectedAttempt> RejectedAttempts { get; } = new();

    public bool Proven => State == SearchState.Proven;

    public string BindingsText => Bindings.Count == 0
        ? string.Empty
        : string.Join(", ", Bindings.Select(kv => $"?{kv.Key}={kv.Value}"));

    public string Label => Negated ? "НЕ " + Goal : Goal.ToString();

    public override string ToString() => $"[{Id}] {Label} — {State} cf={Certainty.Format(Cf)}";
}

/// <summary>Попытка применить правило, которая не подтвердилась: правило, подстановка, поддерево и причина.</summary>
public sealed class RejectedAttempt
{
    public required Rule Rule { get; init; }
    public required IReadOnlyDictionary<string, string> Bindings { get; init; }
    public List<SearchNode> Children { get; } = new();
    public string Reason { get; init; } = string.Empty;
}

public sealed class BackwardOptions
{
    /// <summary>Максимальная глубина цепочки (защита от зацикливания и бесконечного поиска).</summary>
    public int MaxDepth { get; init; } = 14;

    /// <summary>Максимальное число узлов дерева поиска.</summary>
    public int MaxNodes { get; init; } = 30_000;

    /// <summary>Использовать кэш доказанных подцелей.</summary>
    public bool UseMemo { get; init; } = true;
}

public sealed class BackwardResult
{
    public required Predicate Goal { get; init; }
    public required SearchNode Root { get; init; }

    public bool Proven => Root.State == SearchState.Proven;
    public double Cf => Root.Cf;

    public int NodesVisited { get; set; }
    public int CycleHits { get; set; }
    public int MemoHits { get; set; }
    public int DepthLimited { get; set; }
    public bool LimitReached { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>Узлы успешного доказательства в «нормальном порядке»: посылки раньше заключения.</summary>
    public List<SearchNode> Steps { get; } = new();

    public int Layers { get; set; }

    public string FailureReason => Root.State switch
    {
        SearchState.Proven => string.Empty,
        SearchState.Cyclic => "доказательство упирается в цикл",
        SearchState.DepthLimit => "превышена предельная глубина",
        SearchState.Failed => Root.Note,
        _ => "поиск не завершён"
    };
}

/// <summary>
/// Обратный вывод (goal-driven) над деревом поиска, реализованный итеративно
/// (явный стек кадров, без рекурсии).
///
/// Схема: цель → правила, в заключении которых есть эта цель (унификация) →
/// подцели из посылок → в глубину, с возвратом при неудаче.
/// Каждая подцель обязана быть конкретной (все переменные связаны унификацией
/// с заключением) — это гарантируется проверкой безопасности правил.
/// </summary>
public sealed class BackwardEngine
{
    private readonly Dictionary<string, double> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SearchNode> _nodes = new();
    private BackwardOptions _options = new();
    private WorkingMemory _facts = null!;
    private KnowledgeBase _kb = null!;
    private bool _aborted;

    public BackwardResult Solve(KnowledgeBase kb, WorkingMemory facts, Predicate goal, BackwardOptions? options = null)
    {
        var sw = Stopwatch.StartNew();
        _kb = kb;
        _facts = facts;
        _options = options ?? new BackwardOptions();
        _memo.Clear();
        _nodes.Clear();
        _aborted = false;

        var root = new SearchNode { Id = 0, Goal = goal, Depth = 0 };
        _nodes.Add(root);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(root));

        var result = new BackwardResult { Goal = goal, Root = root };

        while (stack.Count > 0)
        {
            if (_aborted)
            {
                foreach (var pending in stack)
                {
                    if (pending.Node.State == SearchState.Pending)
                    {
                        pending.Node.State = SearchState.Failed;
                        pending.Node.Note = "поиск прерван по лимиту узлов";
                        pending.Node.Truncated = true;
                    }
                }
                stack.Clear();
                break;
            }

            var frame = stack.Peek();
            var node = frame.Node;

            if (!frame.Initialized)
            {
                frame.Initialized = true;

                if (node.Depth > _options.MaxDepth)
                {
                    result.DepthLimited++;
                    Complete(stack, frame, SearchState.DepthLimit, 0, $"глубина больше {_options.MaxDepth}", truncated: true);
                    continue;
                }

                var ancestor = FindAncestor(node);
                if (ancestor is not null)
                {
                    result.CycleHits++;
                    Complete(stack, frame, SearchState.Cyclic, 0, $"цикл: уже доказываем {ancestor.Goal}", truncated: true);
                    continue;
                }

                var fact = _facts.Find(node.Goal);
                if (fact is not null)
                {
                    if (node.Negated)
                    {
                        Complete(stack, frame, SearchState.Failed, 0, "факт присутствует — отрицание ложно");
                    }
                    else
                    {
                        node.Fact = fact;
                        Complete(stack, frame, SearchState.Proven, fact.Cf, string.Empty, truncated: false, memoizable: true);
                    }
                    continue;
                }

                if (node.Negated && _kb.RulesFor(node.Goal.Name).Count == 0)
                {
                    Complete(stack, frame, SearchState.Proven, 1.0, "факта нет и правил нет — отрицание истинно");
                    continue;
                }

                if (!node.Negated && _options.UseMemo && _memo.TryGetValue(node.Goal.Key, out var cached))
                {
                    result.MemoHits++;
                    Complete(stack, frame, SearchState.Proven, cached, "цель доказана ранее", truncated: false);
                    continue;
                }

                frame.Candidates = Candidates(node.Goal);
                if (frame.Candidates.Count == 0)
                {
                    Complete(stack, frame, SearchState.Failed, 0, "нет продукций с таким заключением");
                    continue;
                }

                frame.RuleIndex = 0;
                if (!OpenRule(stack, frame))
                {
                    Complete(stack, frame, SearchState.Failed, 0, "правила небезопасны: переменные не связываются с целью");
                    continue;
                }

                continue;
            }

            if (frame.ChildIndex < node.Children.Count)
            {
                var child = node.Children[frame.ChildIndex];
                if (child.State == SearchState.Pending)
                {
                    stack.Push(new Frame(child));
                    continue;
                }
                frame.ChildIndex++;
                continue;
            }

            var allProven = true;
            foreach (var child in node.Children)
            {
                if (!child.Proven)
                {
                    allProven = false;
                    break;
                }
            }

            if (allProven && node.Rule is not null && node.Children.All(c => !c.Truncated))
            {
                var premiseCf = Certainty.MinCf(node.Children.Select(c => c.Cf));
                var cf = Certainty.RuleConclusion(node.Rule.Cf, premiseCf);
                Complete(stack, frame, SearchState.Proven, cf, string.Empty, truncated: false, memoizable: !node.Negated);
                continue;
            }

            // правило не сработало — пробуем следующее
            if (node.Rule is not null)
            {
                var failing = node.Children.FirstOrDefault(c => !c.Proven);
                var reason = failing is not null && failing.Note.Length > 0
                    ? failing.Note
                    : "правило не подтвердилось";

                var attempt = new RejectedAttempt
                {
                    Rule = node.Rule,
                    Bindings = new Dictionary<string, string>(node.Bindings, StringComparer.OrdinalIgnoreCase),
                    Reason = reason
                };
                attempt.Children.AddRange(node.Children);
                node.RejectedAttempts.Add(attempt);

                node.Rejected.Add($"{node.Rule.Id} — {reason}");
            }

            frame.RuleIndex++;
            if (frame.RuleIndex < frame.Candidates.Count && OpenRule(stack, frame)) continue;

            Complete(stack, frame, SearchState.Failed, 0, "все продукции испробованы");
        }

        result.NodesVisited = _nodes.Count;
        result.LimitReached = _aborted;
        result.Layers = root.State == SearchState.Proven ? MaxProvenDepth(root) : 0;
        CollectSteps(root, result.Steps);
        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static int MaxProvenDepth(SearchNode node)
    {
        var max = 0;
        var stack = new Stack<SearchNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.Proven) continue;
            if (n.Depth > max) max = n.Depth;
            foreach (var c in n.Children) stack.Push(c);
        }
        return max;
    }

    private static void CollectSteps(SearchNode root, List<SearchNode> steps)
    {
        // Пост-обход (посылки → заключение), только успешная ветвь.
        var stack = new Stack<(SearchNode Node, bool Processed)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (node, processed) = stack.Pop();
            if (processed)
            {
                if (node.Proven) steps.Add(node);
                continue;
            }

            stack.Push((node, true));
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                if (node.Children[i].Proven) stack.Push((node.Children[i], false));
            }
        }
    }

    private SearchNode? FindAncestor(SearchNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (string.Equals(current.Goal.Key, node.Goal.Key, StringComparison.OrdinalIgnoreCase)) return current;
            current = current.Parent;
        }
        return null;
    }

    private static bool UnifyHead(Rule rule, Predicate goal, out Dictionary<string, string> bindings, out Predicate? head)
    {
        bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        head = null;

        foreach (var conclusion in rule.Conclusions)
        {
            if (!string.Equals(conclusion.Name, goal.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (conclusion.Arity != goal.Arity) continue;

            var probe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (conclusion.TryUnify(goal, probe))
            {
                bindings = probe;
                head = conclusion;
                return true;
            }
        }

        return false;
    }

    private List<(Rule Rule, Dictionary<string, string> Bindings)> Candidates(Predicate goal)
    {
        var list = new List<(Rule Rule, Dictionary<string, string> Bindings)>();
        foreach (var rule in _kb.RulesFor(goal.Name))
        {
            if (UnifyHead(rule, goal, out var bindings, out _)) list.Add((rule, bindings));
        }

        // Сильные правила пробуем первыми: так доказательство получает более
        // объяснимое обоснование (сначала специализированные контр-предметы,
        // потом общие правила).
        return list
            .OrderByDescending(x => x.Rule.Cf)
            .ToList();
    }

    private bool OpenRule(Stack<Frame> stack, Frame frame)
    {
        var node = frame.Node;
        var (rule, bindings) = frame.Candidates[frame.RuleIndex];
        node.Rule = rule;
        node.Bindings = bindings;
        node.Children.Clear();
        frame.ChildIndex = 0;

        var children = new List<SearchNode>(rule.Conditions.Count);
        foreach (var condition in rule.Conditions)
        {
            var atom = condition.Pattern.Substitute(bindings);
            if (!atom.IsGround)
            {
                node.Children.Clear();
                node.Note = $"правило {rule.Id} небезопасно: подцель {atom} содержит переменные";
                return false;
            }

            if (_nodes.Count >= _options.MaxNodes)
            {
                _aborted = true;
                node.Children.Clear();
                return false;
            }

            var child = new SearchNode
            {
                Id = _nodes.Count,
                Goal = atom,
                Depth = node.Depth + 1,
                Parent = node,
                Negated = condition.Negated
            };
            _nodes.Add(child);
            children.Add(child);
        }

        node.Children.AddRange(children);
        frame.Expanded = true;
        return true;
    }

    private void Complete(
        Stack<Frame> stack,
        Frame frame,
        SearchState state,
        double cf,
        string note,
        bool truncated = false,
        bool memoizable = false)
    {
        var node = frame.Node;

        // Отрицание: результат раскрытия правил инвертируется.
        if (node.Negated && frame.Expanded)
        {
            if (state == SearchState.Proven)
            {
                state = SearchState.Failed;
                note = "отрицание ложно: " + note;
                cf = 0;
            }
            else if (state == SearchState.Failed && !truncated)
            {
                state = SearchState.Proven;
                note = "не выводится → отрицание истинно";
                cf = 1.0;
            }
        }

        node.State = state;
        node.Cf = Certainty.Clamp(cf);
        node.Note = note;
        node.Truncated |= truncated;

        if (memoizable && state == SearchState.Proven && !node.Negated)
        {
            var key = node.Goal.Key;
            if (!_memo.TryGetValue(key, out var known) || cf > known) _memo[key] = node.Cf;
        }

        stack.Pop();
        if (stack.Count > 0) stack.Peek().ChildIndex++;
    }

    private sealed class Frame
    {
        public Frame(SearchNode node) => Node = node;

        public SearchNode Node { get; }
        public List<(Rule Rule, Dictionary<string, string> Bindings)> Candidates { get; set; } = new();
        public int RuleIndex { get; set; }
        public int ChildIndex { get; set; }
        public bool Initialized { get; set; }
        public bool Expanded { get; set; }
    }
}


public sealed class GoalSolutions
{
    public required GoalDefinition Goal { get; init; }
    public List<BackwardResult> Results { get; } = new();

    public IEnumerable<BackwardResult> Proven => Results.Where(r => r.Proven);
}

/// <summary>Обёртка над обратным выводом: перебор целей с переменными и запуск по всем целям файла.</summary>
public static class BackwardSolver
{
    /// <summary>
    /// Разворачивает цель с переменными в набор конкретных целей по объявленным сортам.
    /// </summary>
    public static List<Predicate> EnumerateInstances(GoalDefinition goal, KnowledgeBase kb, int cap = 4000)
    {
        if (!goal.HasVariables) return new List<Predicate> { goal.Pattern };

        var args = goal.Pattern.Args.ToArray();
        var domains = new List<List<Arg>?>(args.Length);

        foreach (var arg in args)
        {
            if (!arg.IsVariable)
            {
                domains.Add(null);
                continue;
            }

            IReadOnlyList<string> values;
            if (goal.VariableSorts.TryGetValue(arg.Name, out var sortName))
            {
                var sort = kb.GetSort(sortName);
                values = sort?.Values ?? (IReadOnlyList<string>)Array.Empty<string>();
            }
            else
            {
                values = kb.AllConstants().ToList();
            }

            if (values.Count == 0) return new List<Predicate>();
            domains.Add(values.Select(Arg.Const).ToList());
        }

        var result = new List<Predicate>();
        var indexes = new int[args.Length];
        var variablePositions = domains.Select((d, i) => (d, i)).Where(x => x.d is not null).Select(x => x.i).ToList();

        while (result.Count < cap)
        {
            var atoms = new Arg[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                atoms[i] = domains[i] is null ? args[i] : domains[i]![indexes[i]];
            }
            result.Add(new Predicate(goal.Pattern.Name, atoms));

            // следующий индекс (декартово произведение)
            var carry = true;
            for (var k = 0; k < variablePositions.Count && carry; k++)
            {
                var pos = variablePositions[variablePositions.Count - 1 - k];
                indexes[pos]++;
                if (indexes[pos] < domains[pos]!.Count)
                {
                    carry = false;
                }
                else
                {
                    indexes[pos] = 0;
                }
            }

            if (carry) break;
        }

        return result;
    }

    public static List<GoalSolutions> SolveAll(
        KnowledgeBase kb,
        WorkingMemory facts,
        IEnumerable<GoalDefinition> goals,
        BackwardOptions? options = null)
    {
        var engine = new BackwardEngine();
        var results = new List<GoalSolutions>();

        foreach (var goal in goals)
        {
            var holder = new GoalSolutions { Goal = goal };
            foreach (var instance in EnumerateInstances(goal, kb))
            {
                holder.Results.Add(engine.Solve(kb, facts, instance, options));
            }
            results.Add(holder);
        }

        return results;
    }
}
