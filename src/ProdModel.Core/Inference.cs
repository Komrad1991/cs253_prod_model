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

    /// <summary>Только положительные посылки (для расчёта cf).</summary>
    public List<Fact> Premises { get; init; } = new();

    /// <summary>Индекс следующего обрабатываемого условия правила.
    /// Не совпадает с Premises.Count, если в правиле есть отрицания.</summary>
    public int ConditionIndex { get; init; }
}

/// <summary>
/// Сопоставление посылок правила с рабочей памятью (соединение по всем условиям).
/// Итеративно, явным стеком — без рекурсии.
///
/// Ограничения по <paramref name="maxMatches"/> и <paramref name="cancellation"/>
/// защищают от комбинаторного взрыва: перебор прерывается, а не «висит».
///
/// Индекс обрабатываемого условия ведётся отдельно (<see cref="MatchResult.ConditionIndex"/>),
/// потому что отрицательный литерал не добавляет факт в посылки, но должен
/// продвигать обход вперёд — иначе матчер зацикливается на нём.
/// </summary>
public static class Matcher
{
    /// <summary>Значение по умолчанию для maxMatches (защита от взрыва).</summary>
    public const int DefaultMaxMatches = 5000;

    public static List<MatchResult> Match(
        IReadOnlyList<Condition> conditions,
        Func<string, IReadOnlyList<Fact>> lookup,
        out bool truncated,
        int maxMatches = DefaultMaxMatches,
        CancellationToken cancellation = default)
    {
        truncated = false;
        var results = new List<MatchResult>();
        if (conditions.Count == 0)
        {
            results.Add(new MatchResult());
            return results;
        }

        var stack = new Stack<MatchResult>();
        stack.Push(new MatchResult { ConditionIndex = 0 });
        var pushed = 1;

        while (stack.Count > 0)
        {
            if (cancellation.IsCancellationRequested) { truncated = true; break; }

            var partial = stack.Pop();
            var index = partial.ConditionIndex;

            if (index >= conditions.Count)
            {
                results.Add(partial);
                if (results.Count >= maxMatches) { truncated = true; break; }
                continue;
            }

            var condition = conditions[index];
            if (condition.Negated)
            {
                if (!AnyMatch(condition.Pattern, partial.Bindings, lookup))
                {
                    if (pushed >= maxMatches) { truncated = true; break; }
                    // посылки не меняются, но индекс продвигается
                    stack.Push(new MatchResult
                    {
                        Bindings = new Dictionary<string, string>(partial.Bindings, StringComparer.OrdinalIgnoreCase),
                        Premises = new List<Fact>(partial.Premises),
                        ConditionIndex = index + 1
                    });
                    pushed++;
                }
                continue;
            }

            foreach (var fact in lookup(condition.Pattern.Name))
            {
                if (cancellation.IsCancellationRequested) { truncated = true; break; }
                if (pushed >= maxMatches) { truncated = true; break; }

                var bindings = new Dictionary<string, string>(partial.Bindings, StringComparer.OrdinalIgnoreCase);
                if (!condition.Pattern.TryMatch(fact.Atom, bindings)) continue;

                var premises = new List<Fact>(partial.Premises) { fact };
                stack.Push(new MatchResult
                {
                    Bindings = bindings,
                    Premises = premises,
                    ConditionIndex = index + 1
                });
                pushed++;
            }
            if (truncated) break;
        }

        return results;
    }

    /// <summary>Проверка отрицания без аллокации словаря на каждый факт.</summary>
    private static bool AnyMatch(Predicate pattern, IReadOnlyDictionary<string, string> bindings, Func<string, IReadOnlyList<Fact>> lookup)
    {
        foreach (var fact in lookup(pattern.Name))
        {
            if (MatchesReadonly(pattern, fact.Atom, bindings)) return true;
        }
        return false;
    }

    private static bool MatchesReadonly(Predicate pattern, Predicate atom, IReadOnlyDictionary<string, string> bindings)
    {
        if (pattern.Arity != atom.Arity) return false;
        if (!string.Equals(pattern.Name, atom.Name, StringComparison.OrdinalIgnoreCase)) return false;

        var args = pattern.Args;
        var atomArgs = atom.Args;
        for (var i = 0; i < args.Count; i++)
        {
            var p = args[i];
            var a = atomArgs[i];
            if (p.IsVariable)
            {
                if (bindings.TryGetValue(p.Name, out var bound))
                {
                    if (!string.Equals(bound, a.Name, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            else if (!string.Equals(p.Name, a.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}



public sealed class ForwardOptions
{
    public int MaxPasses { get; init; } = 200;
    public int MaxFacts { get; init; } = 50_000;
    public int MaxMatches { get; init; } = Matcher.DefaultMaxMatches;
    public double Threshold { get; init; } = Certainty.DefaultThreshold;
    public CancellationToken Cancellation { get; init; }
    public long MaxElapsedMs { get; init; }
}

public sealed class ForwardResult
{
    public required WorkingMemory Memory { get; init; }
    public List<Firing> Firings { get; } = new();
    public int Passes { get; set; }
    public int Layers { get; set; }
    public int BlockedRepeats { get; set; }

    /// <summary>Сколько правил/подстановок пришлось отбросить из-за maxMatches.</summary>
    public int MatchTruncations { get; set; }

    public int CircularSupports { get; set; }
    public bool LimitReached { get; set; }
    public long ElapsedMs { get; set; }

    public int InputCount => Memory.Facts.Count(f => !f.IsDerived);
    public int DerivedCount => Memory.Facts.Count(f => f.IsDerived);

    public IEnumerable<Fact> FactsByLayer(int layer) => Memory.Facts.Where(f => f.Depth == layer);
}

/// <summary>
/// Прямой вывод (data-driven) с semi-naive-подходом.
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

        var rulesByPredicate = new Dictionary<string, HashSet<Rule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in kb.Rules)
        {
            foreach (var cond in rule.Conditions)
            {
                if (!rulesByPredicate.TryGetValue(cond.Pattern.Name, out var set))
                    rulesByPredicate[cond.Pattern.Name] = set = new HashSet<Rule>();
                set.Add(rule);
            }
        }

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in kb.Facts) affected.Add(f.Atom.Name);

        for (var pass = 1; pass <= opt.MaxPasses; pass++)
        {
            if (opt.Cancellation.IsCancellationRequested) break;
            if (opt.MaxElapsedMs > 0 && sw.ElapsedMilliseconds > opt.MaxElapsedMs) break;

            result.Passes = pass;
            var changed = false;
            var nextAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rulesToProcess = new HashSet<Rule>();
            foreach (var pred in affected)
            {
                if (rulesByPredicate.TryGetValue(pred, out var rs)) rulesToProcess.UnionWith(rs);
            }

            foreach (var rule in rulesToProcess)
            {
                if (opt.Cancellation.IsCancellationRequested) break;
                if (opt.MaxElapsedMs > 0 && sw.ElapsedMilliseconds > opt.MaxElapsedMs) break;

                var matches = Matcher.Match(
                    rule.Conditions,
                    memory.For,
                    out var truncated,
                    opt.MaxMatches,
                    opt.Cancellation);
                if (truncated) result.MatchTruncations++;

                foreach (var match in matches)
                {
                    if (opt.Cancellation.IsCancellationRequested) break;

                    var premiseCf = 1.0;
                    foreach (var p in match.Premises) if (p.Cf < premiseCf) premiseCf = p.Cf;

                    var cf = Certainty.RuleConclusion(rule.Cf, premiseCf);
                    if (cf < opt.Threshold) continue;

                    var instanceKey = rule.Id + "|" + Canonical(match.Bindings);
                    if (!fired.Add(instanceKey))
                    {
                        blocked.Add(instanceKey);
                        continue;
                    }

                    var premiseDepth = 0;
                    foreach (var p in match.Premises) if (p.Depth > premiseDepth) premiseDepth = p.Depth;
                    var layer = premiseDepth + 1;

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
                            nextAffected.Add(atom.Name);
                            changed = true;
                        }
                        else
                        {
                            conclusions.Add(existing);
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
                            else if (existing.IsDerived)
                            {
                                var before = existing.Cf;
                                existing.Cf = Certainty.Combine(existing.Cf, cf);
                                existing.Depth = Math.Min(existing.Depth, layer);
                                if (existing.Cf - before > 1e-9)
                                {
                                    changed = true;
                                    nextAffected.Add(existing.Atom.Name);
                                }
                            }
                            else
                            {
                                var before = existing.Cf;
                                existing.Cf = Math.Max(existing.Cf, cf);
                                if (existing.Cf - before > 1e-9)
                                {
                                    changed = true;
                                    nextAffected.Add(existing.Atom.Name);
                                }
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

            affected = nextAffected;
            if (result.LimitReached || !changed || nextAffected.Count == 0) break;
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

    public Fact? Fact { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool Truncated { get; set; }

    public List<string> Rejected { get; } = new();
    public List<RejectedAttempt> RejectedAttempts { get; } = new();

    public bool Proven => State == SearchState.Proven;

    public string BindingsText => Bindings.Count == 0
        ? string.Empty
        : string.Join(", ", Bindings.Select(kv => $"?{kv.Key}={kv.Value}"));

    public string Label => Negated ? "НЕ " + Goal : Goal.ToString();

    public override string ToString() => $"[{Id}] {Label} — {State} cf={Certainty.Format(Cf)}";
}

public sealed class RejectedAttempt
{
    public required Rule Rule { get; init; }
    public required IReadOnlyDictionary<string, string> Bindings { get; init; }
    public List<SearchNode> Children { get; } = new();
    public string Reason { get; init; } = string.Empty;
}

public sealed class BackwardOptions
{
    public int MaxDepth { get; init; } = 20;
    public int MaxNodes { get; init; } = 40_000;
    public bool UseMemo { get; init; } = true;
    public bool ReuseMemo { get; init; }
    public int MaxGoalInstances { get; init; } = 4000;
    public CancellationToken Cancellation { get; init; }
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

    public List<SearchNode> Steps { get; } = new();
    public List<SearchNode> CycleNodes { get; } = new();

    public int Layers { get; set; }

    public bool FailedDueToCycle => !Proven && CycleHits > 0;

    public string FailureReason => Root.State switch
    {
        SearchState.Proven => string.Empty,
        SearchState.Cyclic => CycleHits > 0
            ? $"доказательство упирается в цикл ({CycleHits} шт.)"
            : "доказательство упирается в цикл",
        SearchState.DepthLimit => "превышена предельная глубина",
        SearchState.Failed => Root.Note,
        _ => "поиск не завершён"
    };
}

public sealed class BackwardEngine
{
    private readonly Dictionary<string, double> _memo = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedMemo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SearchNode> _nodes = new();
    private readonly Dictionary<SearchNode, NodeState> _state = new();
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
        if (!_options.ReuseMemo) { _memo.Clear(); _failedMemo.Clear(); }
        _nodes.Clear();
        _state.Clear();
        _aborted = false;

        var root = new SearchNode { Id = 0, Goal = goal, Depth = 0 };
        _nodes.Add(root);
        _state[root] = new NodeState();

        var result = new BackwardResult { Goal = goal, Root = root };

        Run(root, result);

        result.NodesVisited = _nodes.Count;
        result.LimitReached = _aborted;
        result.Layers = root.State == SearchState.Proven ? MaxProvenDepth(root) : 0;
        CollectSteps(root, result.Steps);
        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private void Run(SearchNode root, BackwardResult result)
    {
        var current = root;

        while (current is not null)
        {
            if (_aborted) break;
            if (_options.Cancellation.IsCancellationRequested) { _aborted = true; break; }

            var state = _state[current];

            if (!state.Started)
            {
                state.Started = true;

                if (TryResolveImmediately(current, result))
                {
                    current = current.Parent;
                    continue;
                }

                state.Candidates = Candidates(current.Goal);
                if (state.Candidates.Count == 0)
                {
                    Finalize(current, SearchState.Failed, 0, "нет продукций с таким заключением");
                    current = current.Parent;
                    continue;
                }

                state.RuleIndex = 0;
                if (!OpenRule(current, state))
                {
                    Finalize(current, SearchState.Failed, 0,
                        _aborted ? "поиск прерван по лимиту/отмене"
                                 : "правила небезопасны: переменные не связываются с целью",
                        truncated: _aborted);
                    current = current.Parent;
                    continue;
                }

                if (current.Children.Count == 0)
                {
                    var ruleCf = state.Candidates[state.RuleIndex].Rule.Cf;
                    Finalize(current, SearchState.Proven, Certainty.RuleConclusion(ruleCf, 1.0),
                        string.Empty, memoizable: !current.Negated);
                    current = current.Parent;
                    continue;
                }

                state.ChildIndex = 0;
                current = current.Children[0];
                continue;
            }

            if (state.ChildIndex + 1 < current.Children.Count)
            {
                state.ChildIndex++;
                current = current.Children[state.ChildIndex];
                continue;
            }

            if (current.Children.All(c => c.Proven) && current.Children.All(c => !c.Truncated))
            {
                var premiseCf = Certainty.MinCf(current.Children.Select(c => c.Cf));
                var ruleCf = state.Candidates[state.RuleIndex].Rule.Cf;
                var cf = Certainty.RuleConclusion(ruleCf, premiseCf);
                Finalize(current, SearchState.Proven, cf, string.Empty, memoizable: !current.Negated);
                current = current.Parent;
                continue;
            }

            RecordRejected(current);

            state.RuleIndex++;
            if (OpenRule(current, state))
            {
                if (current.Children.Count == 0)
                {
                    var ruleCf2 = state.Candidates[state.RuleIndex].Rule.Cf;
                    Finalize(current, SearchState.Proven, Certainty.RuleConclusion(ruleCf2, 1.0),
                        string.Empty, memoizable: !current.Negated);
                    current = current.Parent;
                    continue;
                }

                state.ChildIndex = 0;
                current = current.Children[0];
                continue;
            }

            Finalize(current, SearchState.Failed, 0,
                _aborted ? "поиск прерван по лимиту/отмене" : "все продукции испробованы",
                truncated: _aborted);
            current = current.Parent;
        }

        if (_aborted)
        {
            MarkPendingAsFailed();
        }
        else if (root.State == SearchState.Failed && result.CycleHits > 0)
        {
            root.State = SearchState.Cyclic;
            root.Note = $"не доказано; в дереве {result.CycleHits} цикл(ов)";
        }
    }

    private bool TryResolveImmediately(SearchNode node, BackwardResult result)
    {
        if (node.Depth > _options.MaxDepth)
        {
            result.DepthLimited++;
            Finalize(node, SearchState.DepthLimit, 0,
                $"глубина больше {_options.MaxDepth}", truncated: true);
            return true;
        }

        var ancestor = FindAncestor(node);
        if (ancestor is not null)
        {
            result.CycleHits++;
            result.CycleNodes.Add(node);
            Finalize(node, SearchState.Cyclic, 0,
                $"цикл: уже доказываем {ancestor.Goal}", truncated: true);
            return true;
        }

        var fact = _facts.Find(node.Goal);
        if (fact is not null)
        {
            if (node.Negated)
                Finalize(node, SearchState.Failed, 0, "факт присутствует — отрицание ложно");
            else
            {
                node.Fact = fact;
                Finalize(node, SearchState.Proven, fact.Cf, string.Empty, memoizable: true);
            }
            return true;
        }

        if (node.Negated && _kb.RulesFor(node.Goal.Name).Count == 0)
        {
            Finalize(node, SearchState.Proven, 1.0, "факта нет и правил нет — отрицание истинно");
            return true;
        }

        if (_options.UseMemo)
        {
            var key = MemoKey(node);

            if (!node.Negated && _memo.TryGetValue(node.Goal.Key, out var cached))
            {
                result.MemoHits++;
                Finalize(node, SearchState.Proven, cached, "цель доказана ранее");
                return true;
            }

            if (_failedMemo.Contains(key))
            {
                result.MemoHits++;
                Finalize(node, SearchState.Failed, 0, "цель уже опровергнута ранее");
                return true;
            }
        }

        return false;
    }

    private bool OpenRule(SearchNode node, NodeState state)
    {
        while (state.RuleIndex < state.Candidates.Count)
        {
            if (_aborted || _options.Cancellation.IsCancellationRequested) { _aborted = true; return false; }

            var (rule, bindings) = state.Candidates[state.RuleIndex];

            var children = TryBuildChildren(node, rule, bindings);
            if (children is null) { state.RuleIndex++; continue; }

            node.Children.Clear();          // только когда реально строим новый набор
            node.Rule = rule;
            node.Bindings = bindings;
            node.Children.AddRange(children);
            state.Expanded = true;
            return true;
        }
        return false;
    }

    private List<SearchNode>? TryBuildChildren(SearchNode node, Rule rule, Dictionary<string, string> bindings)
    {
        var children = new List<SearchNode>(rule.Conditions.Count);

        foreach (var condition in rule.Conditions)
        {
            if (_options.Cancellation.IsCancellationRequested) { _aborted = true; return null; }

            var atom = condition.Pattern.Substitute(bindings);
            if (!atom.IsGround) return null;

            if (_nodes.Count >= _options.MaxNodes)
            {
                _aborted = true;
                return null;
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
            _state[child] = new NodeState();
            children.Add(child);
        }

        return children;
    }

    private static void RecordRejected(SearchNode node)
    {
        if (node.Rule is null) return;

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

    private void Finalize(SearchNode node, SearchState state, double cf, string note,
                          bool truncated = false, bool memoizable = false)
    {
        if (node.Negated && _state[node].Expanded)
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

        if (_options.UseMemo)
        {
            if (memoizable && state == SearchState.Proven && !node.Negated)
            {
                var key = node.Goal.Key;
                if (!_memo.TryGetValue(key, out var known) || cf > known) _memo[key] = node.Cf;
            }
            else if (!truncated && state == SearchState.Failed)
            {
                _failedMemo.Add(MemoKey(node));
            }
        }
    }

    private static string MemoKey(SearchNode node) => (node.Negated ? "!" : "") + node.Goal.Key;

    private void MarkPendingAsFailed()
    {
        foreach (var node in _nodes)
        {
            if (node.State != SearchState.Pending) continue;
            node.State = SearchState.Failed;
            if (string.IsNullOrEmpty(node.Note)) node.Note = "поиск прерван по лимиту/отмене";
            node.Truncated = true;
        }
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

        return list
            .OrderByDescending(x => x.Rule.Cf)
            .ToList();
    }

    private sealed class NodeState
    {
        public bool Started { get; set; }
        public bool Expanded { get; set; }
        public int RuleIndex { get; set; }
        public int ChildIndex { get; set; }
        public List<(Rule Rule, Dictionary<string, string> Bindings)> Candidates { get; set; } = new();
    }
}


public sealed class GoalSolutions
{
    public required GoalDefinition Goal { get; init; }
    public List<BackwardResult> Results { get; } = new();

    public IEnumerable<BackwardResult> Proven => Results.Where(r => r.Proven);
    public IEnumerable<BackwardResult> FailedDueToCycle => Results.Where(r => r.FailedDueToCycle);
}

public static class BackwardSolver
{
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
        var opt = options ?? new BackwardOptions();

        var batchOptions = new BackwardOptions
        {
            MaxDepth = opt.MaxDepth,
            MaxNodes = opt.MaxNodes,
            UseMemo = opt.UseMemo,
            ReuseMemo = true,
            MaxGoalInstances = opt.MaxGoalInstances,
            Cancellation = opt.Cancellation
        };

        var engine = new BackwardEngine();
        var results = new List<GoalSolutions>();

        foreach (var goal in goals)
        {
            if (opt.Cancellation.IsCancellationRequested) break;

            var holder = new GoalSolutions { Goal = goal };
            foreach (var instance in EnumerateInstances(goal, kb, opt.MaxGoalInstances))
            {
                if (opt.Cancellation.IsCancellationRequested) break;
                holder.Results.Add(engine.Solve(kb, facts, instance, batchOptions));
            }
            results.Add(holder);
        }

        return results;
    }
}