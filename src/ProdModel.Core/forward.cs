using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ProdModel.Core;

// ============================================================================
// forward.cs — прямой вывод (data-driven: от фактов к заключениям)
//   Run() создаёт Session и запускает Execute(): проходы по правилам, пока
//   рабочая память не перестанет пополняться (неподвижная точка).
//   Типы данных, Matcher и опции — в forward.utils.cs.
// ============================================================================

/// <summary>
/// Прямой вывод: правила применяются к рабочей памяти, пока она не перестанет
/// пополняться (неподвижная точка). Состояние одного запуска живёт в Session,
/// поэтому каждый шаг алгоритма — отдельный метод без длинных списков параметров.
/// </summary>
public sealed class ForwardChainer
{
    /// <summary>Запуск вывода на готовой базе знаний. Один вызов = один прогон.</summary>
    public ForwardResult Run(KnowledgeBase kb, ForwardOptions? options = null)
    {
        if (kb is null)
            throw new ArgumentNullException(nameof(kb));

        return new Session(kb, options ?? new ForwardOptions()).Execute();
    }

    /// <summary>Состояние одного запуска: память, индексы правил, счётчики шагов.</summary>
    private sealed class Session
    {
        private readonly KnowledgeBase _kb;
        private readonly ForwardOptions _opt;
        private readonly ForwardResult _result;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        // Экземпляры правил: уже сработавшие и повторно заблокированные.
        private readonly HashSet<string> _fired = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

        // Правила по предикатам посылок: после появления факта достаточно
        // проверить правила, у которых есть посылка с этим предикатом.
        private readonly Dictionary<string, List<Rule>> _rulesByPredicate = new(StringComparer.OrdinalIgnoreCase);

        // Предикаты, факты которых появились или улучшились на последнем проходе.
        private HashSet<string> _affected = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _nextAffected = new(StringComparer.OrdinalIgnoreCase);

        private bool _changed;
        private bool _goalReached;
        private int _step;

        public Session(KnowledgeBase kb, ForwardOptions opt)
        {
            _kb = kb;
            _opt = opt;
            _result = new ForwardResult { Memory = new WorkingMemory(), Goal = opt.Goal };
        }

        public ForwardResult Execute()
        {
            // Входные факты переносим в рабочую память вместе с их названиями.
            foreach (var fact in _kb.Facts)
                _result.Memory.AddInput(new Fact(fact.Atom, FactOrigin.Input) { DisplayName = fact.DisplayName });

            // Цель могла оказаться среди исходных фактов.
            if (_opt.Goal is not null && ForwardHelpers.FindGoalMatch(_result.Memory, _opt.Goal) is { } initial)
            {
                _result.ReachedGoal = initial;
                _result.GoalReachedAtStep = 0;
                return Done();
            }

            if (_opt.MaxPasses <= 0 || _opt.MaxFacts <= 0 || _opt.MaxMatches <= 0)
            {
                _result.LimitReached = true;
                return Done();
            }

            IndexRules();

            // На первом проходе проверяются все правила — иначе не сработают
            // правила без посылок и правила с отрицательными условиями.
            var firstPass = true;

            for (var pass = 1; pass <= _opt.MaxPasses; pass++)
            {
                if (LimitHit())
                    break;

                _result.Passes = pass;

                var rules = firstPass ? new HashSet<Rule>(_kb.Rules) : RulesForAffected();
                firstPass = false;

                if (!ProcessRules(rules))
                    break;

                _affected = _nextAffected;

                if (_result.LimitReached || _goalReached)
                    break;

                // Неподвижная точка: новых фактов нет и слои не улучшились.
                if (!_changed || _affected.Count == 0)
                    break;

                if (pass == _opt.MaxPasses)
                    _result.LimitReached = true;
            }

            _result.BlockedRepeats = _blocked.Count;
            return Done();
        }

        /// <summary>
        /// Проход по правилам в порядке файла — он определяет нумерацию шагов.
        /// Возвращает false, если проход прерван лимитом или достигнутой целью.
        /// </summary>
        private bool ProcessRules(HashSet<Rule> rules)
        {
            _changed = false;
            _nextAffected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in _kb.Rules)
            {
                if (!rules.Contains(rule))
                    continue;

                if (LimitHit() || !ProcessRule(rule))
                    return false;
            }

            return true;
        }

        private bool ProcessRule(Rule rule)
        {
            var matches = Matcher.Match(
                rule.Conditions,
                _result.Memory.For,
                out var truncated,
                _opt.MaxMatches,
                _opt.Cancellation);

            if (truncated)
            {
                _result.MatchTruncations++;
                _result.LimitReached = true;
            }

            foreach (var match in matches)
            {
                if (_opt.Cancellation.IsCancellationRequested)
                {
                    _result.LimitReached = true;
                    return false;
                }

                // Один и тот же экземпляр правила не должен срабатывать повторно.
                var instanceKey = rule.Id + "|" + ForwardHelpers.Canonical(match.Bindings);
                if (!_fired.Add(instanceKey))
                {
                    _blocked.Add(instanceKey);
                    continue;
                }

                if (!Fire(rule, match) || _goalReached)
                    return false;
            }

            return !_result.LimitReached;
        }

        /// <summary>
        /// Применяет один экземпляр правила: добавляет заключения в память и
        /// регистрирует срабатывание. Возвращает false, если сработал лимит фактов.
        /// </summary>
        private bool Fire(Rule rule, MatchResult match)
        {
            var memory = _result.Memory;
            var premiseDepth = match.Premises.Count == 0 ? 0 : match.Premises.Max(p => p.Depth);
            var layer = premiseDepth + 1;
            var conclusions = new List<Fact>();

            foreach (var head in rule.Conclusions)
            {
                var atom = head.Substitute(match.Bindings);

                // Заключение с несвязанными переменными нельзя добавить в память.
                if (!atom.IsGround)
                    continue;

                var existing = memory.Find(atom);

                if (existing is null)
                {
                    if (memory.Count >= _opt.MaxFacts)
                    {
                        _result.LimitReached = true;
                        break;
                    }

                    var created = memory.AddDerived(atom, layer, rule, match.Premises, _step + 1);
                    created.DisplayName = _kb.GetFactName(atom.Name);

                    conclusions.Add(created);
                    _nextAffected.Add(atom.Name);
                    _changed = true;
                    continue;
                }

                conclusions.Add(existing);

                var circular = ForwardHelpers.DependsOn(atom, match.Premises);
                existing.Derivations.Add(new Derivation
                {
                    Rule = rule,
                    Step = _step + 1,
                    Layer = layer,
                    Premises = match.Premises,
                    Circular = circular
                });

                if (circular)
                {
                    _result.CircularSupports++;
                }
                else if (existing.IsDerived && layer < existing.Depth)
                {
                    // Факт получен более коротким путём — обновляем его слой.
                    existing.Depth = layer;
                    _changed = true;
                    _nextAffected.Add(existing.Atom.Name);
                }
            }

            if (_result.LimitReached)
                return false;

            if (conclusions.Count == 0)
                return true;

            _step++;
            _result.Firings.Add(new Firing
            {
                Step = _step,
                Rule = rule,
                Bindings = new Dictionary<string, string>(match.Bindings, StringComparer.OrdinalIgnoreCase),
                Premises = match.Premises,
                Conclusions = conclusions,
                Layer = layer
            });

            if (_opt.Goal is null || ForwardHelpers.FindGoalMatch(memory, _opt.Goal) is not { } reached)
                return true;

            // Запрошенная цель получена — прямой вывод останавливается.
            _result.ReachedGoal = reached;
            _result.GoalReachedAtStep = _step;
            _goalReached = true;
            return true;
        }

        /// <summary>Проверка отмены и лимита времени; выставляет LimitReached.</summary>
        private bool LimitHit()
        {
            if (!_opt.Cancellation.IsCancellationRequested &&
                (_opt.MaxElapsedMs <= 0 || _sw.ElapsedMilliseconds < _opt.MaxElapsedMs))
                return false;

            _result.LimitReached = true;
            return true;
        }

        private void IndexRules()
        {
            foreach (var rule in _kb.Rules)
            {
                foreach (var condition in rule.Conditions)
                {
                    var predicate = condition.Pattern.Name;

                    if (!_rulesByPredicate.TryGetValue(predicate, out var list))
                        _rulesByPredicate[predicate] = list = new List<Rule>();

                    if (!list.Contains(rule))
                        list.Add(rule);
                }
            }
        }

        private HashSet<Rule> RulesForAffected()
        {
            var rules = new HashSet<Rule>();

            foreach (var predicate in _affected)
            {
                if (!_rulesByPredicate.TryGetValue(predicate, out var related))
                    continue;

                foreach (var rule in related)
                    rules.Add(rule);
            }

            return rules;
        }

        private ForwardResult Done()
        {
            _result.Layers = _result.Memory.MaxDerivedDepth();
            _sw.Stop();
            _result.ElapsedMs = _sw.ElapsedMilliseconds;
            return _result;
        }
    }
}
