using System.Globalization;
using System.Text;

namespace ProdModel.Core;



/// <summary>
/// Аргумент предиката: константа (Haze, anti_bullet) либо логическая переменная (?x).
/// </summary>
public readonly record struct Arg(string Name, bool IsVariable)
{
    public static Arg Const(string name) => new(name, false);
    public static Arg Var(string name) => new(name, true);

    public override string ToString() => IsVariable ? "?" + Name : Name;
}

/// <summary>
/// Атом (предикат с аргументами). Используется и как шаблон правила,
/// и как конкретный факт (в факте все аргументы — константы).
/// </summary>
public sealed class Predicate : IEquatable<Predicate>
{
    private readonly Arg[] _args;

    public string Name { get; }
    public IReadOnlyList<Arg> Args => _args;
    public int Arity => _args.Length;
    public bool IsGround
    {
        get
        {
            foreach (var a in _args)
            {
                if (a.IsVariable) return false;
            }
            return true;
        }
    }

    public Predicate(string name, params Arg[] args) : this(name, (IReadOnlyList<Arg>)args)
    {
    }

    public Predicate(string name, IReadOnlyList<Arg> args)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Имя предиката не может быть пустым.", nameof(name));

        Name = name.Trim();
        _args = args.ToArray();
    }

    /// <summary>Канонический ключ атома (без учёта регистра) — для индексов и сравнения.</summary>
    public string Key
    {
        get
        {
            var sb = new StringBuilder(Name.Length + 8);
            sb.Append(Name.ToLowerInvariant());
            if (_args.Length > 0)
            {
                sb.Append('(');
                for (var i = 0; i < _args.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_args[i].IsVariable ? "?" : string.Empty);
                    sb.Append(_args[i].Name.ToLowerInvariant());
                }
                sb.Append(')');
            }
            return sb.ToString();
        }
    }

    public IEnumerable<string> Variables
    {
        get
        {
            foreach (var a in _args)
            {
                if (a.IsVariable) yield return a.Name;
            }
        }
    }

    public IEnumerable<string> Constants
    {
        get
        {
            foreach (var a in _args)
            {
                if (!a.IsVariable) yield return a.Name;
            }
        }
    }

    /// <summary>Подстановка значений переменных. Невозможно — оставляет переменную как есть.</summary>
    public Predicate Substitute(IReadOnlyDictionary<string, string> bindings)
    {
        if (_args.Length == 0) return this;

        var changed = false;
        var result = new Arg[_args.Length];
        for (var i = 0; i < _args.Length; i++)
        {
            var a = _args[i];
            if (a.IsVariable && bindings.TryGetValue(a.Name, out var value))
            {
                result[i] = Arg.Const(value);
                changed = true;
            }
            else
            {
                result[i] = a;
            }
        }

        return changed ? new Predicate(Name, result) : this;
    }

    /// <summary>
    /// Сопоставление шаблона (this) с конкретным атомом. Связывает переменные шаблона.
    /// Атом обязан быть конкретным (без переменных).
    /// </summary>
    public bool TryMatch(Predicate atom, IDictionary<string, string> bindings)
    {
        if (_args.Length != atom._args.Length) return false;
        if (!string.Equals(Name, atom.Name, StringComparison.OrdinalIgnoreCase)) return false;

        for (var i = 0; i < _args.Length; i++)
        {
            var pattern = _args[i];
            var value = atom._args[i];

            if (pattern.IsVariable)
            {
                if (bindings.TryGetValue(pattern.Name, out var bound))
                {
                    if (!string.Equals(bound, value.Name, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else
                {
                    bindings[pattern.Name] = value.Name;
                }
            }
            else if (!string.Equals(pattern.Name, value.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Унификация двух атомов (переменные связываются значениями из второго).</summary>
    public bool TryUnify(Predicate other, IDictionary<string, string> bindings)
    {
        if (_args.Length != other._args.Length) return false;
        if (!string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)) return false;

        for (var i = 0; i < _args.Length; i++)
        {
            var left = _args[i];
            var right = other._args[i];

            if (left.IsVariable)
            {
                if (bindings.TryGetValue(left.Name, out var bound))
                {
                    if (!string.Equals(bound, right.Name, StringComparison.OrdinalIgnoreCase)) return false;
                }
                else
                {
                    bindings[left.Name] = right.Name;
                }
            }
            else if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(Predicate? other) => other is not null && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is Predicate p && Equals(p);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Key);

    public override string ToString()
    {
        if (_args.Length == 0) return Name;
        return $"{Name}({string.Join(", ", _args)})";
    }
}

/// <summary>Условие правила: атом или его отрицание (закрытый мир / negation as failure).</summary>
public sealed class Condition
{
    public Predicate Pattern { get; }
    public bool Negated { get; }

    public Condition(Predicate pattern, bool negated = false)
    {
        Pattern = pattern;
        Negated = negated;
    }

    public override string ToString() => (Negated ? "НЕ " : string.Empty) + Pattern;
}


public enum FactOrigin
{
    /// <summary>Факт задан во входном файле (данные задачи).</summary>
    Input,

    /// <summary>Факт получен продукцией.</summary>
    Derived
}

/// <summary>Одно из объяснений (обоснований) факта: правило, посылки, коэффициент.</summary>
public sealed class Derivation
{
    public required Rule Rule { get; init; }
    public required double Cf { get; init; }
    public required double RuleCf { get; init; }
    public required int Step { get; init; }
    public required int Layer { get; init; }
    public required IReadOnlyList<Fact> Premises { get; init; }

    /// <summary>Обоснование опирается на сам факт (циклическая поддержка) — уверенность не повышается.</summary>
    public bool Circular { get; init; }
}

/// <summary>Факт рабочей памяти: атом + коэффициент уверенности + история вывода.</summary>
public sealed class Fact
{
    public Fact(Predicate atom, double cf, FactOrigin origin)
    {
        Atom = atom;
        Cf = cf;
        Origin = origin;
    }

    public Predicate Atom { get; }
    public string Key => Atom.Key;
    public string Text => Atom.ToString();

    /// <summary>Коэффициент уверенности, вещественное число в [0, 1].</summary>
    public double Cf { get; set; }

    public FactOrigin Origin { get; }
    public bool IsDerived => Origin == FactOrigin.Derived;

    /// <summary>Слой вывода: 0 — входной факт, 1 — получен правилом из входных, и т.д.</summary>
    public int Depth { get; set; }

    /// <summary>Порядковый номер появления в рабочей памяти.</summary>
    public int Order { get; set; }

    /// <summary>Правило и посылки лучшего (по коэффициенту) обоснования.</summary>
    public Rule? Rule { get; set; }
    public double RuleCf { get; set; }

    /// <summary>Коэффициент лучшего обоснования (для выбора объяснения).</summary>
    public double BestCf { get; set; }

    public List<Fact> Premises { get; } = new();

    /// <summary>Все известные обоснования факта (факт мог выводиться несколько раз).</summary>
    public List<Derivation> Derivations { get; } = new();

    public string? Note { get; set; }

    public override string ToString() => $"{Text} [cf={Certainty.Format(Cf)}]";
}


/// <summary>
/// Продукция (правило) вида: ЕСЛИ c1 И c2 ... ТО q, с коэффициентом уверенности.
/// </summary>
public sealed class Rule
{
    public required string Id { get; init; }

    /// <summary>Коэффициент уверенности самого правила, [0, 1].</summary>
    public double Cf { get; init; } = 1.0;

    public List<Condition> Conditions { get; } = new();

    /// <summary>Заключение (обычно одно).</summary>
    public List<Predicate> Conclusions { get; } = new();

    /// <summary>Текстовое пояснение «почему правило сформулировано именно так».</summary>
    public string Why { get; init; } = string.Empty;

    public int Line { get; init; }
    public string? SourceFile { get; init; }

    public string ConditionText => Conditions.Count == 0
        ? "—"
        : string.Join(" И ", Conditions.Select(c => c.ToString()));

    public string ConclusionText => string.Join(", ", Conclusions.Select(c => c.ToString()));

    /// <summary>Переменные тела правила.</summary>
    public HashSet<string> BodyVariables()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Conditions)
        {
            foreach (var v in c.Pattern.Variables) set.Add(v);
        }
        return set;
    }

    /// <summary>Переменные заключения.</summary>
    public HashSet<string> HeadVariables()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Conclusions)
        {
            foreach (var v in p.Variables) set.Add(v);
        }
        return set;
    }

    public override string ToString() => $"{Id}: {ConditionText} → {ConclusionText} [cf={Certainty.Format(Cf)}]";
}



/// <summary>
/// Работа с коэффициентами уверенности (MYCIN-подобная модель).
///
/// cf факта/правила — вещественное число в [0, 1]:
///   0    — уверенности нет,
///   0.5  — «скорее да»,
///   1    — достоверно.
///
/// Свёртка правила: cf(заключение) = cf(правило) * min(cf посылок).
/// Накопление свидетельств: cf = cf1 + cf2 * (1 - cf1).
/// </summary>
public static class Certainty
{
    public const double Min = 0.0;
    public const double Max = 1.0;

    /// <summary>Порог, ниже которого выведенный факт не попадает в рабочую память.</summary>
    public const double DefaultThreshold = 0.15;

    public static double Clamp(double value)
    {
        if (double.IsNaN(value)) return Min;
        if (value < Min) return Min;
        if (value > Max) return Max;
        return value;
    }

    public static double RuleConclusion(double ruleCf, double premiseCf) => Clamp(ruleCf * premiseCf);

    public static double Combine(double a, double b) => Clamp(a + b * (1.0 - a));

    public static double MinCf(IEnumerable<double> values)
    {
        var min = 1.0;
        foreach (var v in values)
        {
            if (v < min) min = v;
        }
        return min;
    }

    public static string Format(double cf) => cf.ToString("0.00", CultureInfo.InvariantCulture);

    public static string FormatPercent(double cf) => (cf * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
}


public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }

    public override string ToString()
    {
        var where = File is null ? string.Empty : $"{Path.GetFileName(File)}:{Line}: ";
        var tag = Severity switch
        {
            DiagnosticSeverity.Error => "ОШИБКА",
            DiagnosticSeverity.Warning => "предупреждение",
            _ => "инфо"
        };
        return $"{where}{tag}: {Message}";
    }
}

/// <summary>Цель вывода: атом, возможно с переменными, размеченными сортами (?i:item).</summary>
public sealed class GoalDefinition
{
    public required Predicate Pattern { get; init; }

    /// <summary>Сорт для переменных цели: имя переменной → сорт.</summary>
    public Dictionary<string, string> VariableSorts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Label { get; init; }
    public int Line { get; init; }
    public string? SourceFile { get; init; }

    public bool HasVariables => Pattern.Args.Any(a => a.IsVariable);

    public override string ToString() => Pattern.ToString();
}

/// <summary>Объявление сорта (домена) — используется для перебора целей с переменными.</summary>
public sealed class SortDefinition
{
    public required string Name { get; init; }
    public List<string> Values { get; } = new();
}

/// <summary>База знаний: факты, правила, цели, объявления сортов.</summary>
public sealed class KnowledgeBase
{
    private readonly Dictionary<string, List<Fact>> _factsByPredicate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Rule>> _rulesByConclusion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortDefinition> _sorts = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; set; } = "База знаний";
    public string? Description { get; set; }
    public List<Fact> Facts { get; } = new();
    public List<Rule> Rules { get; } = new();
    public List<GoalDefinition> Goals { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<string> Sources { get; } = new();

    public IReadOnlyDictionary<string, SortDefinition> Sorts => _sorts;

    public IReadOnlyList<Fact> FactsFor(string predicate)
        => _factsByPredicate.TryGetValue(predicate, out var list) ? list : Array.Empty<Fact>();

    public IReadOnlyList<Rule> RulesFor(string conclusionPredicate)
        => _rulesByConclusion.TryGetValue(conclusionPredicate, out var list) ? list : Array.Empty<Rule>();

    public bool HasFact(string predicate) => _factsByPredicate.ContainsKey(predicate);

    public void AddFact(Fact fact)
    {
        fact.Order = Facts.Count;
        Facts.Add(fact);
        if (!_factsByPredicate.TryGetValue(fact.Atom.Name, out var list))
        {
            list = new List<Fact>();
            _factsByPredicate[fact.Atom.Name] = list;
        }
        list.Add(fact);
    }

    public void AddRule(Rule rule)
    {
        Rules.Add(rule);
        foreach (var conclusion in rule.Conclusions)
        {
            if (!_rulesByConclusion.TryGetValue(conclusion.Name, out var list))
            {
                list = new List<Rule>();
                _rulesByConclusion[conclusion.Name] = list;
            }
            if (!list.Contains(rule)) list.Add(rule);
        }
    }

    public void AddSort(string name, IEnumerable<string> values)
    {
        if (!_sorts.TryGetValue(name, out var sort))
        {
            sort = new SortDefinition { Name = name };
            _sorts[name] = sort;
        }
        foreach (var v in values)
        {
            if (!sort.Values.Contains(v, StringComparer.OrdinalIgnoreCase)) sort.Values.Add(v);
        }
    }

    public SortDefinition? GetSort(string name) => _sorts.TryGetValue(name, out var s) ? s : null;

    /// <summary>Все константы, встречающиеся в базе (факты + правила).</summary>
    public HashSet<string> AllConstants()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Facts)
            foreach (var c in f.Atom.Constants) set.Add(c);
        foreach (var s in _sorts.Values)
            foreach (var v in s.Values) set.Add(v);
        foreach (var r in Rules)
        {
            foreach (var c in r.Conditions)
                foreach (var v in c.Pattern.Constants) set.Add(v);
            foreach (var p in r.Conclusions)
                foreach (var v in p.Constants) set.Add(v);
        }
        return set;
    }

    /// <summary>Предикаты, которые встречаются в базе (для отчётов и проверок).</summary>
    public IEnumerable<string> Predicates()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Facts) set.Add(f.Atom.Name);
        foreach (var r in Rules)
        {
            foreach (var c in r.Conditions) set.Add(c.Pattern.Name);
            foreach (var p in r.Conclusions) set.Add(p.Name);
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }

    public void ResetRuntimeFacts()
    {
        // База знаний неизменяема; рабочая память создаётся движком заново.
    }

    /// <summary>
    /// Копия базы (правила, знания, цели) с добавленными входными фактами.
    /// Используется, чтобы отделить знания от входных данных: состав врагов
    /// подставляется в одну и ту же базу без правки файла знаний.
    /// </summary>
    public KnowledgeBase Clone(IEnumerable<Fact>? extraFacts = null)
    {
        var clone = new KnowledgeBase
        {
            Name = Name,
            Description = Description
        };

        foreach (var sort in Sorts.Values) clone.AddSort(sort.Name, sort.Values);
        foreach (var fact in Facts) clone.AddFact(fact);
        foreach (var rule in Rules) clone.AddRule(rule);
        foreach (var goal in Goals) clone.Goals.Add(goal);
        foreach (var source in Sources) clone.Sources.Add(source);
        foreach (var diagnostic in Diagnostics) clone.Diagnostics.Add(diagnostic);

        if (extraFacts is not null)
        {
            foreach (var fact in extraFacts) clone.AddFact(fact);
        }

        return clone;
    }

    /// <summary>База с входным набором вражеских героев (1–6).</summary>
    public KnowledgeBase WithEnemies(IEnumerable<string> heroes, double cf = 0.9)
        => WithEnemies(heroes.Select(h => (Hero: h, Cf: cf)));

    /// <summary>База с входным набором вражеских героев и их коэффициентами уверенности.</summary>
    public KnowledgeBase WithEnemies(IEnumerable<(string Hero, double Cf)> heroes)
    {
        var facts = new List<Fact>();

        foreach (var (hero, cf) in heroes)
        {
            if (string.IsNullOrWhiteSpace(hero)) continue;

            facts.Add(new Fact(
                new Predicate("enemy", Arg.Const(hero.Trim())),
                Certainty.Clamp(cf),
                FactOrigin.Input));
        }

        return Clone(facts);
    }

    /// <summary>База с входным фактом «выбранный предмет» — для обратного запроса «предмет → враги».</summary>
    public KnowledgeBase WithItem(string item, double cf = 1.0)
        => Clone(new[]
        {
            new Fact(new Predicate("our_item", Arg.Const(item)), Certainty.Clamp(cf), FactOrigin.Input)
        });
}


/// <summary>
/// Рабочая память: множество фактов, индекс по предикату и по каноническому ключу.
/// Порядок фактов детерминирован (порядок появления), чтобы вывод был воспроизводим.
/// </summary>
public sealed class WorkingMemory
{
    private readonly Dictionary<string, Fact> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Fact>> _byPredicate = new(StringComparer.OrdinalIgnoreCase);

    public List<Fact> Facts { get; } = new();

    public int Count => Facts.Count;

    public void AddInput(Fact fact)
    {
        fact.Depth = 0;
        Add(fact);
    }

    public Fact Add(Fact fact)
    {
        if (_byKey.TryGetValue(fact.Key, out var existing)) return existing;

        fact.Order = Facts.Count;
        Facts.Add(fact);
        _byKey[fact.Key] = fact;
        if (!_byPredicate.TryGetValue(fact.Atom.Name, out var list))
        {
            list = new List<Fact>();
            _byPredicate[fact.Atom.Name] = list;
        }
        list.Add(fact);
        return fact;
    }

    public Fact AddDerived(Predicate atom, double cf, int depth, Rule rule, IReadOnlyList<Fact> premises, int step)
    {
        var fact = new Fact(atom, cf, FactOrigin.Derived)
        {
            Depth = depth,
            Rule = rule,
            RuleCf = rule.Cf,
            BestCf = cf
        };
        fact.Premises.AddRange(premises);
        fact.Derivations.Add(new Derivation
        {
            Rule = rule,
            Cf = cf,
            RuleCf = rule.Cf,
            Step = step,
            Layer = depth,
            Premises = premises
        });
        return Add(fact);
    }

    public Fact? Find(Predicate atom) => _byKey.TryGetValue(atom.Key, out var f) ? f : null;

    public bool Contains(Predicate atom) => _byKey.ContainsKey(atom.Key);

    public IReadOnlyList<Fact> For(string predicate)
        => _byPredicate.TryGetValue(predicate, out var list) ? list : Array.Empty<Fact>();

    public IEnumerable<Fact> DerivedFacts()
    {
        foreach (var f in Facts)
        {
            if (f.IsDerived) yield return f;
        }
    }

    public int MaxDerivedDepth()
    {
        var max = 0;
        foreach (var f in Facts)
        {
            if (f.IsDerived && f.Depth > max) max = f.Depth;
        }
        return max;
    }

    /// <summary>Копия только входных фактов (для «чистого» обратного вывода).</summary>
    public WorkingMemory InputOnly()
    {
        var wm = new WorkingMemory();
        foreach (var f in Facts)
        {
            if (!f.IsDerived) wm.AddInput(new Fact(f.Atom, f.Cf, FactOrigin.Input));
        }
        return wm;
    }
}
