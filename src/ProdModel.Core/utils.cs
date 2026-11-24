using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ProdModel.Core;

// ============================================================================
// Model
// ============================================================================

public readonly record struct Arg(string Name, bool IsVariable)
{
    public static Arg Const(string name) => new(name, false);
    public static Arg Var(string name) => new(name, true);

    public override string ToString()
        => IsVariable ? "?" + Name : Name;
}

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
            foreach (var arg in _args)
                if (arg.IsVariable)
                    return false;

            return true;
        }
    }

    public Predicate(string name, params Arg[] args)
        : this(name, (IReadOnlyList<Arg>)args)
    {
    }

    public Predicate(string name, IReadOnlyList<Arg> args)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Имя предиката не может быть пустым.",
                nameof(name));

        Name = name.Trim();
        _args = args?.ToArray() ?? Array.Empty<Arg>();

        foreach (var arg in _args)
        {
            if (string.IsNullOrWhiteSpace(arg.Name))
                throw new ArgumentException(
                    "Аргумент предиката не может быть пустым.",
                    nameof(args));
        }
    }

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
                    if (i > 0)
                        sb.Append(',');

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
            foreach (var arg in _args)
                if (arg.IsVariable)
                    yield return arg.Name;
        }
    }

    public IEnumerable<string> Constants
    {
        get
        {
            foreach (var arg in _args)
                if (!arg.IsVariable)
                    yield return arg.Name;
        }
    }

    public Predicate Substitute(
        IReadOnlyDictionary<string, string> bindings)
    {
        if (_args.Length == 0 || bindings.Count == 0)
            return this;

        var result = new Arg[_args.Length];
        var changed = false;

        for (var i = 0; i < _args.Length; i++)
        {
            var original = _args[i];

            if (!original.IsVariable)
            {
                result[i] = original;
                continue;
            }

            var resolved = Resolve(
                original,
                bindings,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            result[i] = resolved;

            if (resolved != original)
                changed = true;
        }

        return changed ? new Predicate(Name, result) : this;
    }

    private static Arg Resolve(
        Arg arg,
        IReadOnlyDictionary<string, string> bindings,
        HashSet<string> visited)
    {
        if (!arg.IsVariable)
            return arg;

        if (!visited.Add(arg.Name))
            return arg;

        if (!bindings.TryGetValue(arg.Name, out var value))
            return arg;

        if (string.IsNullOrWhiteSpace(value))
            return arg;

        if (value.StartsWith("?", StringComparison.Ordinal))
        {
            var next = value[1..];

            if (next.Length == 0)
                return arg;

            return Resolve(
                Arg.Var(next),
                bindings,
                visited);
        }

        return Arg.Const(value);
    }

    public bool TryMatch(
        Predicate atom,
        IDictionary<string, string> bindings)
    {
        if (atom is null ||
            bindings is null ||
            _args.Length != atom._args.Length ||
            !string.Equals(
                Name,
                atom.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var temporary = new Dictionary<string, string>(
            bindings,
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _args.Length; i++)
        {
            var pattern = _args[i];
            var value = atom._args[i];

            if (value.IsVariable)
                return false;

            if (pattern.IsVariable)
            {
                if (temporary.TryGetValue(
                        pattern.Name,
                        out var bound))
                {
                    if (!string.Equals(
                            bound,
                            value.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else
                {
                    temporary[pattern.Name] = value.Name;
                }
            }
            else if (!string.Equals(
                         pattern.Name,
                         value.Name,
                         StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        CopyBindings(temporary, bindings);
        return true;
    }

    public bool TryUnify(
        Predicate other,
        IDictionary<string, string> bindings)
    {
        if (other is null ||
            bindings is null ||
            _args.Length != other._args.Length ||
            !string.Equals(
                Name,
                other.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var temporary = new Dictionary<string, string>(
            bindings,
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _args.Length; i++)
        {
            var left = Resolve(
                _args[i],
                temporary,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var right = Resolve(
                other._args[i],
                temporary,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (!UnifyArguments(left, right, temporary))
                return false;
        }

        CopyBindings(temporary, bindings);
        return true;
    }

    private static bool UnifyArguments(
        Arg left,
        Arg right,
        Dictionary<string, string> bindings)
    {
        if (!left.IsVariable && !right.IsVariable)
        {
            return string.Equals(
                left.Name,
                right.Name,
                StringComparison.OrdinalIgnoreCase);
        }

        if (left.IsVariable && right.IsVariable)
        {
            if (string.Equals(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            bindings[left.Name] = "?" + right.Name;
            return true;
        }

        if (left.IsVariable)
        {
            bindings[left.Name] = right.Name;
            return true;
        }

        bindings[right.Name] = left.Name;
        return true;
    }

    private static void CopyBindings(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> destination)
    {
        destination.Clear();

        foreach (var pair in source)
            destination[pair.Key] = pair.Value;
    }

    public bool Equals(Predicate? other)
    {
        return other is not null &&
               string.Equals(
                   Key,
                   other.Key,
                   StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
        => obj is Predicate predicate && Equals(predicate);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Key);

    public override string ToString()
    {
        return _args.Length == 0
            ? Name
            : $"{Name}({string.Join(", ", _args)})";
    }
}

public sealed class Condition
{
    public Predicate Pattern { get; }
    public bool Negated { get; }

    public Condition(Predicate pattern, bool negated = false)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Negated = negated;
    }

    public override string ToString()
        => (Negated ? "НЕ " : string.Empty) + Pattern;
}

// ============================================================================
// Facts
// ============================================================================

public enum FactOrigin
{
    Input,
    Derived
}

public sealed class Derivation
{
    public required Rule Rule { get; init; }
    public required int Step { get; init; }
    public required int Layer { get; init; }
    public required IReadOnlyList<Fact> Premises { get; init; }

    public bool Circular { get; init; }
}

public sealed class Fact
{
    public Fact(Predicate atom, FactOrigin origin)
    {
        Atom = atom ?? throw new ArgumentNullException(nameof(atom));
        Origin = origin;
    }

    public Predicate Atom { get; }

    public string Key => Atom.Key;

    public string? DisplayName { get; set; }

    public string Text
    {
        get
        {
            return string.IsNullOrWhiteSpace(DisplayName)
                ? Atom.ToString()
                : $"{Atom} - {DisplayName}";
        }
    }

    public FactOrigin Origin { get; }

    public bool IsDerived => Origin == FactOrigin.Derived;

    public int Depth { get; set; }

    public int Order { get; set; }

    public Rule? Rule { get; set; }

    public List<Fact> Premises { get; } = new();

    public List<Derivation> Derivations { get; } = new();

    public string? Note { get; set; }

    public override string ToString() => Text;
}

// ============================================================================
// Rules
// ============================================================================

public sealed class Rule
{
    public required string Id { get; init; }

    public List<Condition> Conditions { get; } = new();

    public List<Predicate> Conclusions { get; } = new();

    public string Why { get; init; } = string.Empty;

    public int Line { get; init; }

    public string? SourceFile { get; init; }

    public string ConditionText
    {
        get
        {
            return Conditions.Count == 0
                ? "—"
                : string.Join(" И ", Conditions.Select(c => c.ToString()));
        }
    }

    public string ConclusionText
        => string.Join(", ", Conclusions.Select(c => c.ToString()));

    public HashSet<string> BodyVariables()
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var condition in Conditions)
        {
            foreach (var variable in condition.Pattern.Variables)
                result.Add(variable);
        }

        return result;
    }

    public HashSet<string> HeadVariables()
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var conclusion in Conclusions)
        {
            foreach (var variable in conclusion.Variables)
                result.Add(variable);
        }

        return result;
    }

    public override string ToString()
        => $"{Id}: {ConditionText} → {ConclusionText}";
}

// ============================================================================
// Diagnostics
// ============================================================================

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
        var where = File is null
            ? string.Empty
            : $"{Path.GetFileName(File)}:{Line}: ";

        var tag = Severity switch
        {
            DiagnosticSeverity.Error => "ОШИБКА",
            DiagnosticSeverity.Warning => "предупреждение",
            _ => "инфо"
        };

        return $"{where}{tag}: {Message}";
    }
}

// ============================================================================
// Goals and sorts
// ============================================================================

public sealed class GoalDefinition
{
    public required Predicate Pattern { get; init; }

    public Dictionary<string, string> VariableSorts { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    public int Line { get; init; }

    public string? SourceFile { get; init; }

    public bool HasVariables
        => Pattern.Args.Any(arg => arg.IsVariable);

    public override string ToString()
        => Pattern.ToString();
}

public sealed class SortDefinition
{
    public required string Name { get; init; }

    public List<string> Values { get; } = new();
}

// ============================================================================
// Knowledge base
// ============================================================================

public sealed class KnowledgeBase
{
    private readonly Dictionary<string, List<Fact>> _factsByPredicate
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<Rule>> _rulesByConclusion
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, SortDefinition> _sorts
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _factNames
        = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; set; } = "База знаний";

    public string? Description { get; set; }

    public List<Fact> Facts { get; } = new();

    public List<Rule> Rules { get; } = new();

    public List<GoalDefinition> Goals { get; } = new();

    public List<Diagnostic> Diagnostics { get; } = new();

    public List<string> Sources { get; } = new();

    public IReadOnlyDictionary<string, SortDefinition> Sorts => _sorts;

    public IReadOnlyDictionary<string, string> FactNames => _factNames;

    public IReadOnlyList<Fact> FactsFor(string predicate)
    {
        return _factsByPredicate.TryGetValue(predicate, out var list)
            ? list
            : Array.Empty<Fact>();
    }

    public IReadOnlyList<Rule> RulesFor(string conclusionPredicate)
    {
        return _rulesByConclusion.TryGetValue(conclusionPredicate, out var list)
            ? list
            : Array.Empty<Rule>();
    }

    public void SetFactName(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException(
                "Идентификатор факта не может быть пустым.",
                nameof(id));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Название факта не может быть пустым.",
                nameof(name));

        _factNames[id.Trim()] = name.Trim();

        foreach (var fact in Facts)
        {
            if (string.Equals(
                    fact.Atom.Name,
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                fact.DisplayName = name.Trim();
            }
        }
    }

    public string? GetFactName(string id)
    {
        return _factNames.TryGetValue(id, out var name)
            ? name
            : null;
    }

    public void AddFact(Fact fact)
    {
        fact.Order = Facts.Count;
        fact.DisplayName ??= GetFactName(fact.Atom.Name);

        Facts.Add(fact);

        if (!_factsByPredicate.TryGetValue(
                fact.Atom.Name,
                out var list))
        {
            _factsByPredicate[fact.Atom.Name] = list = new List<Fact>();
        }

        list.Add(fact);
    }

    public void AddRule(Rule rule)
    {
        Rules.Add(rule);

        foreach (var conclusion in rule.Conclusions)
        {
            if (!_rulesByConclusion.TryGetValue(
                    conclusion.Name,
                    out var list))
            {
                _rulesByConclusion[conclusion.Name] = list =
                    new List<Rule>();
            }

            if (!list.Contains(rule))
                list.Add(rule);
        }
    }

    public void AddSort(string name, IEnumerable<string> values)
    {
        if (!_sorts.TryGetValue(name, out var sort))
        {
            _sorts[name] = sort = new SortDefinition
            {
                Name = name
            };
        }

        foreach (var value in values)
        {
            if (!sort.Values.Contains(
                    value,
                    StringComparer.OrdinalIgnoreCase))
            {
                sort.Values.Add(value);
            }
        }
    }

    public SortDefinition? GetSort(string name)
    {
        return _sorts.TryGetValue(name, out var sort)
            ? sort
            : null;
    }

    public HashSet<string> AllConstants()
    {
        var result = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var fact in Facts)
        {
            foreach (var value in fact.Atom.Constants)
                result.Add(value);
        }

        foreach (var sort in _sorts.Values)
        {
            foreach (var value in sort.Values)
                result.Add(value);
        }

        foreach (var rule in Rules)
        {
            foreach (var condition in rule.Conditions)
            {
                foreach (var value in condition.Pattern.Constants)
                    result.Add(value);
            }

            foreach (var conclusion in rule.Conclusions)
            {
                foreach (var value in conclusion.Constants)
                    result.Add(value);
            }
        }

        return result;
    }
}

// ============================================================================
// Working memory
// ============================================================================

public sealed class WorkingMemory
{
    private readonly Dictionary<string, Fact> _byKey
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<Fact>> _byPredicate
        = new(StringComparer.OrdinalIgnoreCase);

    public List<Fact> Facts { get; } = new();

    public int Count => Facts.Count;

    public void AddInput(Fact fact)
    {
        fact.Depth = 0;
        Add(fact);
    }

    public Fact Add(Fact fact)
    {
        if (_byKey.TryGetValue(fact.Key, out var existing))
        {
            if (fact.IsDerived)
                existing.Derivations.AddRange(fact.Derivations);

            return existing;
        }

        fact.Order = Facts.Count;
        Facts.Add(fact);
        _byKey[fact.Key] = fact;

        if (!_byPredicate.TryGetValue(
                fact.Atom.Name,
                out var list))
        {
            _byPredicate[fact.Atom.Name] = list = new List<Fact>();
        }

        list.Add(fact);
        return fact;
    }

    public Fact AddDerived(
        Predicate atom,
        int depth,
        Rule rule,
        IReadOnlyList<Fact> premises,
        int step)
    {
        var fact = new Fact(atom, FactOrigin.Derived)
        {
            Depth = depth,
            Rule = rule
        };

        fact.Premises.AddRange(premises);

        fact.Derivations.Add(new Derivation
        {
            Rule = rule,
            Step = step,
            Layer = depth,
            Premises = premises
        });

        return Add(fact);
    }

    public Fact? Find(Predicate atom)
    {
        return _byKey.TryGetValue(atom.Key, out var fact)
            ? fact
            : null;
    }

    public IReadOnlyList<Fact> For(string predicate)
    {
        return _byPredicate.TryGetValue(predicate, out var list)
            ? list
            : Array.Empty<Fact>();
    }

    public int MaxDerivedDepth()
    {
        var max = 0;

        foreach (var fact in Facts)
        {
            if (fact.IsDerived && fact.Depth > max)
                max = fact.Depth;
        }

        return max;
    }
}

// ============================================================================
// Parser
//
// Supported format:
//
// ИМЯ: Название базы
//
// ФАКТЫ:
// f1
// f2 - Человек
//
// ПРАВИЛА:
// f1, f2 -> f3
// f2 -> f4
//
// ЦЕЛИ:
// f3
// ============================================================================

public sealed class KbParseException : Exception
{
    public string FilePath { get; }

    public int Line { get; }

    public KbParseException(
        string filePath,
        int line,
        string message)
        : base($"{Path.GetFileName(filePath)}:{line}: {message}")
    {
        FilePath = filePath;
        Line = line;
    }
}

public static class KbLoader
{
    public static KnowledgeBase Load(params string[] paths)
        => Load((IEnumerable<string>)paths);

    public static KnowledgeBase Load(IEnumerable<string> paths)
    {
        var kb = new KnowledgeBase();
        var loader = new Loader(kb);

        var any = false;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            loader.LoadFile(Path.GetFullPath(path));
            any = true;
        }

        if (!any)
        {
            throw new InvalidOperationException(
                "Не указан ни один файл базы знаний.");
        }

        KbValidator.Validate(kb);
        return kb;
    }
}

internal sealed class Loader
{
    private enum Section
    {
        None,
        Facts,
        Rules,
        Goals
    }

    private readonly KnowledgeBase _kb;

    private readonly HashSet<string> _loaded
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _active
        = new(StringComparer.OrdinalIgnoreCase);

    private int _ruleCounter;

    public Loader(KnowledgeBase kb)
    {
        _kb = kb;
    }

    public void LoadFile(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Файл базы знаний не найден: {fullPath}",
                fullPath);
        }

        if (_active.Contains(fullPath))
        {
            throw new KbParseException(
                fullPath,
                0,
                "Обнаружено циклическое включение файла.");
        }

        if (_loaded.Contains(fullPath))
        {
            _kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Файл уже загружен: {Path.GetFileName(fullPath)}",
                File = fullPath
            });

            return;
        }

        _active.Add(fullPath);
        _kb.Sources.Add(fullPath);

        try
        {
            ParseFile(fullPath);
            _loaded.Add(fullPath);
        }
        finally
        {
            _active.Remove(fullPath);
        }
    }

    private void ParseFile(string fullPath)
    {
        var section = Section.None;
        var lines = File.ReadAllLines(fullPath);

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;

            var (body, comment) = SplitComment(lines[i]);
            body = body.Trim();

            if (body.Length == 0)
                continue;

            if (TryParseHeader(body, out var newSection))
            {
                section = newSection;
                continue;
            }

            if (TryParseMetadata(body, fullPath, lineNumber))
                continue;

            if (TryParseInclude(body, fullPath, lineNumber))
                continue;

            switch (section)
            {
                case Section.Facts:
                    ParseFact(body, fullPath, lineNumber);
                    break;

                case Section.Rules:
                    ParseRule(body, comment, fullPath, lineNumber);
                    break;

                case Section.Goals:
                    ParseGoal(body, fullPath, lineNumber);
                    break;

                default:
                    throw new KbParseException(
                        fullPath,
                        lineNumber,
                        "Сначала укажите раздел ФАКТЫ:, ПРАВИЛА: или ЦЕЛИ:.");
            }
        }
    }

    private static bool TryParseHeader(
        string line,
        out Section section)
    {
        var header = line.Trim().TrimEnd(':').Trim();

        if (header.Equals(
                "ФАКТЫ",
                StringComparison.OrdinalIgnoreCase) ||
            header.Equals(
                "FACTS",
                StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Facts;
            return true;
        }

        if (header.Equals(
                "ПРАВИЛА",
                StringComparison.OrdinalIgnoreCase) ||
            header.Equals(
                "RULES",
                StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Rules;
            return true;
        }

        if (header.Equals(
                "ЦЕЛИ",
                StringComparison.OrdinalIgnoreCase) ||
            header.Equals(
                "GOALS",
                StringComparison.OrdinalIgnoreCase))
        {
            section = Section.Goals;
            return true;
        }

        section = Section.None;
        return false;
    }

    private bool TryParseMetadata(
        string line,
        string file,
        int lineNumber)
    {
        var colon = line.IndexOf(':');

        if (colon < 0)
            return false;

        var key = line[..colon].Trim();
        var value = Unquote(line[(colon + 1)..].Trim());

        if (key.Equals(
                "ИМЯ",
                StringComparison.OrdinalIgnoreCase) ||
            key.Equals(
                "NAME",
                StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length == 0)
                throw new KbParseException(
                    file, lineNumber, "Не указано имя базы знаний.");

            _kb.Name = value;
            return true;
        }

        if (key.Equals(
                "ОПИСАНИЕ",
                StringComparison.OrdinalIgnoreCase) ||
            key.Equals(
                "DESCRIPTION",
                StringComparison.OrdinalIgnoreCase))
        {
            _kb.Description = value;
            return true;
        }

        if (key.Equals(
                "ЦЕЛЬ",
                StringComparison.OrdinalIgnoreCase) ||
            key.Equals(
                "GOAL",
                StringComparison.OrdinalIgnoreCase))
        {
            ParseGoal(value, file, lineNumber);
            return true;
        }

        return false;
    }

    private bool TryParseInclude(
        string line,
        string file,
        int lineNumber)
    {
        const string prefix = "ВКЛЮЧИТЬ:";

        if (!line.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var target = Unquote(line[prefix.Length..].Trim());

        if (target.Length == 0)
            throw new KbParseException(
                file, lineNumber, "Не указан файл для включения.");

        var fullPath = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(file) ?? ".",
                    target));

        LoadFile(fullPath);
        return true;
    }

    private void ParseFact(
        string line,
        string file,
        int lineNumber)
    {
        var body = line;

        var separator = body.IndexOf(
            " - ",
            StringComparison.Ordinal);

        string id;
        string? displayName = null;

        if (separator >= 0)
        {
            id = body[..separator].Trim();
            displayName = Unquote(body[(separator + 3)..].Trim());

            if (displayName.Length == 0)
            {
                throw new KbParseException(
                    file,
                    lineNumber,
                    "Не указано название после дефиса.");
            }
        }
        else
        {
            id = body.Trim();
        }

        ValidateIdentifier(id, file, lineNumber);

        if (_kb.Facts.Any(f =>
                string.Equals(
                    f.Atom.Name,
                    id,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new KbParseException(
                file,
                lineNumber,
                $"Факт {id} уже объявлен.");
        }

        if (displayName is not null)
            _kb.SetFactName(id, displayName);

        _kb.AddFact(new Fact(
            new Predicate(id),
            FactOrigin.Input)
        {
            DisplayName = displayName
        });
    }

    private void ParseRule(
        string line,
        string comment,
        string file,
        int lineNumber)
    {
        var arrow = line.IndexOf(
            "->",
            StringComparison.Ordinal);

        if (arrow < 0)
        {
            throw new KbParseException(
                file,
                lineNumber,
                "В правиле отсутствует стрелка ->.");
        }

        if (line.IndexOf(
                "->",
                arrow + 2,
                StringComparison.Ordinal) >= 0)
        {
            throw new KbParseException(
                file,
                lineNumber,
                "В правиле должна быть только одна стрелка ->.");
        }

        var left = line[..arrow].Trim();
        var right = line[(arrow + 2)..].Trim();

        if (left.Length == 0)
        {
            throw new KbParseException(
                file,
                lineNumber,
                "Не указаны посылки правила.");
        }

        if (right.Length == 0)
        {
            throw new KbParseException(
                file,
                lineNumber,
                "Не указано заключение правила.");
        }

        var rule = new Rule
        {
            Id = $"R{++_ruleCounter}",
            Why = comment,
            Line = lineNumber,
            SourceFile = file
        };

        foreach (var part in SplitTopLevel(left, ',', file, lineNumber))
        {
            var conditionText = part.Trim();
            var negated = false;

            if (conditionText.StartsWith(
                    "not ",
                    StringComparison.OrdinalIgnoreCase))
            {
                negated = true;
                conditionText = conditionText[4..].Trim();
            }
            else if (conditionText.StartsWith(
                         "не ",
                         StringComparison.OrdinalIgnoreCase))
            {
                negated = true;
                conditionText = conditionText[3..].Trim();
            }

            ValidateIdentifier(
                conditionText,
                file,
                lineNumber);

            rule.Conditions.Add(new Condition(
                new Predicate(conditionText),
                negated));
        }

        foreach (var part in SplitTopLevel(right, ',', file, lineNumber))
        {
            var id = part.Trim();

            ValidateIdentifier(id, file, lineNumber);
            rule.Conclusions.Add(new Predicate(id));
        }

        if (rule.Conclusions.Count == 0)
        {
            throw new KbParseException(
                file,
                lineNumber,
                "У правила нет заключений.");
        }

        _kb.AddRule(rule);
    }

    private void ParseGoal(
        string line,
        string file,
        int lineNumber)
    {
        var id = line.Trim();

        ValidateIdentifier(id, file, lineNumber);

        if (_kb.Goals.Any(g =>
                string.Equals(
                    g.Pattern.Name,
                    id,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _kb.Goals.Add(new GoalDefinition
        {
            Pattern = new Predicate(id),
            Line = lineNumber,
            SourceFile = file
        });
    }

    private static void ValidateIdentifier(
        string id,
        string file,
        int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new KbParseException(
                file,
                lineNumber,
                "Пустой идентификатор.");
        }

        if (!(char.IsLetter(id[0]) || id[0] == '_'))
        {
            throw new KbParseException(
                file,
                lineNumber,
                $"Некорректный идентификатор «{id}».");
        }

        for (var i = 1; i < id.Length; i++)
        {
            var c = id[i];

            if (!(char.IsLetterOrDigit(c) ||
                  c == '_' ||
                  c == '-'))
            {
                throw new KbParseException(
                    file,
                    lineNumber,
                    $"Некорректный символ в идентификаторе «{id}».");
            }
        }
    }

    private static List<string> SplitTopLevel(
        string text,
        char separator,
        string file,
        int lineNumber)
    {
        var result = new List<string>();
        var depth = 0;
        var inQuotes = false;
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
                continue;

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0 && c == separator)
            {
                var part = text[start..i].Trim();

                if (part.Length == 0)
                    throw new KbParseException(
                        file,
                        lineNumber,
                        "Обнаружен пустой элемент списка.");

                result.Add(part);
                start = i + 1;
            }
        }

        var tail = text[start..].Trim();

        if (tail.Length == 0)
        {
            if (result.Count > 0)
                throw new KbParseException(
                    file,
                    lineNumber,
                    "Обнаружен пустой элемент списка.");
        }
        else
        {
            result.Add(tail);
        }

        return result;
    }

    private static (string Body, string Comment) SplitComment(
        string line)
    {
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
                continue;

            if (c == '#' ||
                (c == '/' &&
                 i + 1 < line.Length &&
                 line[i + 1] == '/'))
            {
                var offset = c == '#' ? 1 : 2;

                return (
                    line[..i].TrimEnd(),
                    line[(i + offset)..].Trim());
            }
        }

        return (line.TrimEnd(), string.Empty);
    }

    private static string Unquote(string text)
    {
        text = text.Trim();

        if (text.Length >= 2 &&
            text[0] == '"' &&
            text[^1] == '"')
        {
            return text[1..^1].Replace("\\\"", "\"");
        }

        return text;
    }
}

// ============================================================================
// Validation
// ============================================================================

public static class KbValidator
{
    public static void Validate(KnowledgeBase kb)
    {
        if (kb.Facts.Count == 0)
        {
            kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = "В базе знаний нет исходных фактов."
            });
        }

        if (kb.Rules.Count == 0)
        {
            kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = "В базе знаний нет продукционных правил."
            });
        }

        var factKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var fact in kb.Facts)
        {
            if (!factKeys.Add(fact.Key))
            {
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Повторяющийся факт {fact.Text}."
                });
            }

            if (!fact.Atom.IsGround)
            {
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Исходный факт {fact.Text} содержит переменные."
                });
            }
        }

        var ruleIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var rule in kb.Rules)
        {
            if (!ruleIds.Add(rule.Id))
            {
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Повторяющийся ID правила {rule.Id}.",
                    File = rule.SourceFile,
                    Line = rule.Line
                });
            }

            if (rule.Conclusions.Count == 0)
            {
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = $"У правила {rule.Id} нет заключения.",
                    File = rule.SourceFile,
                    Line = rule.Line
                });
            }

            if (rule.Conditions.Count == 0)
            {
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Правило {rule.Id} не имеет посылок.",
                    File = rule.SourceFile,
                    Line = rule.Line
                });
            }

            var bound = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var condition in rule.Conditions)
            {
                if (condition.Negated)
                {
                    foreach (var variable in condition.Pattern.Variables)
                    {
                        if (!bound.Contains(variable))
                        {
                            kb.Diagnostics.Add(new Diagnostic
                            {
                                Severity = DiagnosticSeverity.Error,
                                Message =
                                    $"В правиле {rule.Id} переменная " +
                                    $"?{variable} в отрицании не связана.",
                                File = rule.SourceFile,
                                Line = rule.Line
                            });
                        }
                    }
                }
                else
                {
                    foreach (var variable in condition.Pattern.Variables)
                        bound.Add(variable);
                }
            }

            // Небезопасно то заключение, чья переменная не связана положительными
            // посылками: такую цель нельзя конкретизировать ни вперёд, ни назад.
            foreach (var variable in rule.HeadVariables())
            {
                if (!bound.Contains(variable))
                {
                    kb.Diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Message =
                            $"Правило {rule.Id} небезопасно: переменная " +
                            $"?{variable} в заключении не связана посылками.",
                        File = rule.SourceFile,
                        Line = rule.Line
                    });
                }
            }
        }

        foreach (var goal in kb.Goals)
        {
            foreach (var pair in goal.VariableSorts)
            {
                if (kb.GetSort(pair.Value) is null)
                {
                    kb.Diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Message =
                            $"Для переменной ?{pair.Key} цели {goal} " +
                            $"не найден сорт «{pair.Value}».",
                        File = goal.SourceFile,
                        Line = goal.Line
                    });
                }
            }
        }

        kb.Diagnostics.Add(new Diagnostic
        {
            Severity = DiagnosticSeverity.Info,
            Message =
                $"Загружено: фактов {kb.Facts.Count}, " +
                $"правил {kb.Rules.Count}, целей {kb.Goals.Count}."
        });
    }
}

// ============================================================================
// Explanation
// ============================================================================

public static class TextExplainer
{
    public static string ExplainFiring(Firing firing)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $"Шаг {firing.Step}, слой {firing.Layer}: правило {firing.Rule.Id}");

        if (!string.IsNullOrWhiteSpace(firing.Rule.Why))
            sb.AppendLine($"  Смысл: {firing.Rule.Why}");

        sb.AppendLine("  Посылки:");

        if (firing.Premises.Count == 0)
            sb.AppendLine("    • (посылок нет)");

        foreach (var premise in firing.Premises)
        {
            sb.AppendLine(
                $"    • {premise.Text}" +
                (premise.IsDerived
                    ? $" (слой {premise.Depth})"
                    : " (входной)"));
        }

        sb.AppendLine("  Вывод:");

        foreach (var conclusion in firing.Conclusions)
            sb.AppendLine($"    • {conclusion.Text}");

        if (firing.Bindings.Count > 0)
            sb.AppendLine($"  Подстановка: {firing.BindingsText}");

        return sb.ToString();
    }

    public static string ExplainFact(
        Fact fact,
        int maxNodes = 300)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Факт: {fact.Text}");

        sb.AppendLine(
            fact.IsDerived
                ? $"Источник: правило {fact.Rule?.Id ?? "—"}, слой {fact.Depth}"
                : "Источник: входной факт");

        if (fact.Derivations.Count > 1)
        {
            sb.AppendLine(
                $"Количество обоснований: {fact.Derivations.Count}");
        }

        var stack = new Stack<(Fact Fact, int Level)>();
        stack.Push((fact, 0));

        var visited = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        var nodes = 0;

        while (stack.Count > 0 && nodes++ < maxNodes)
        {
            var (current, level) = stack.Pop();

            if (level > 0)
            {
                var kind = current.IsDerived
                    ? $"← {current.Rule?.Id}"
                    : "← вход";

                sb.AppendLine(
                    new string(' ', level * 2) +
                    $"• {current.Text} {kind}");
            }

            if (!visited.Add(current.Key))
                continue;

            for (var i = current.Premises.Count - 1; i >= 0; i--)
                stack.Push((current.Premises[i], level + 1));
        }

        return sb.ToString();
    }

    public static string ExplainBackward(
        BackwardResult result,
        bool includeRejected = false)
    {
        var sb = new StringBuilder();

        var status = result.Proven
            ? result.CyclePrevented
                ? $"ЦЕЛЬ ДОКАЗАНА (цикл предотвращён, попаданий: {result.CycleHits})"
                : "ЦЕЛЬ ДОКАЗАНА"
            : result.FailedDueToCycle
                ? $"ЦЕЛЬ НЕ ДОКАЗАНА — цикл предотвращён, но других продукций нет (попаданий: {result.CycleHits})"
                : $"ЦЕЛЬ НЕ ДОКАЗАНА ({result.FailureReason})";

        sb.AppendLine($"Цель: {result.Goal} → {status}");

        sb.AppendLine(
            $"Узлов: {result.NodesVisited}, " +
            $"слоёв: {result.Layers}, " +
            $"циклов: {result.CycleHits}, " +
            $"кэш-хитов: {result.MemoHits}, " +
            $"время: {result.ElapsedMs} мс");

        if (result.Proven)
        {
            sb.AppendLine(
                $"Стоимость доказательства: {result.ProofCost:0.00}");
        }

        sb.AppendLine(
            $"Переключений между ветвями: {result.BranchSwitches}");

        if (result.CycleNodes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(result.Proven
                ? "ПРЕДОТВРАЩЁННЫЕ ЦИКЛЫ (ветвь отброшена, цель доказана заново):"
                : "ОБНАРУЖЕННЫЕ ЦИКЛЫ:");

            foreach (var node in result.CycleNodes)
            {
                sb.AppendLine(
                    $"  ⟲ {node.Label} — {node.Note}");
                sb.AppendLine(result.Proven
                    ? "    ветвь замкнута, цель переиграна по следующей продукции"
                    : "    ветвь замкнута, других продукций у цели нет — ответ не получен");
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            result.Proven
                ? "Дерево доказательства:"
                : "Дерево поиска:");

        RenderTree(sb, result.Root, includeRejected);

        if (result.Proven)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Нормальный порядок (посылки раньше заключений):");

            var number = 1;

            foreach (var node in result.Steps)
            {
                var via = node.Fact is not null
                    ? "факт"
                    : node.Rule is null
                        ? "—"
                        : $"правило {node.Rule.Id}";

                var why = node.Fact is null && !string.IsNullOrWhiteSpace(node.Rule?.Why)
                    ? $" — {node.Rule!.Why}"
                    : string.Empty;

                sb.AppendLine(
                    $"  {number++,2}. {node.Label} — {via}{why}");
            }
        }

        return sb.ToString();
    }

    private static void RenderTree(
        StringBuilder sb,
        SearchNode root,
        bool includeRejected)
    {
        var stack = new Stack<(SearchNode Node, int Level)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (node, level) = stack.Pop();
            var indent = new string(' ', level * 2);

            var mark = node.State switch
            {
                SearchState.Proven => "✔",
                SearchState.Failed => "✘",
                SearchState.Cyclic => "⟲ ЦИКЛ предотвращён",
                SearchState.DepthLimit => "… предел",
                _ => "?"
            };

            var via = node.Fact is not null
                ? "факт из базы"
                : node.Rule is null
                    ? "—"
                    : $"правило {node.Rule.Id}";

            var bindings = node.Bindings.Count > 0
                ? " [" + node.BindingsText + "]"
                : string.Empty;

            sb.AppendLine(
                $"{indent}{mark} {node.Label} ← {via}{bindings}" +
                (string.IsNullOrEmpty(node.Note)
                    ? string.Empty
                    : $" // {node.Note}"));

            if (includeRejected)
            {
                foreach (var rejected in node.Rejected)
                {
                    sb.AppendLine(
                        $"{indent}   ✘ отклонено: {rejected}");
                }

                foreach (var attempt in node.RejectedAttempts)
                {
                    sb.AppendLine(
                        $"{indent}   {(attempt.CyclePrevented ? "⟲ цикл предотвращён" : "✘ отклонено")}: " +
                        $"{attempt.Rule.Id} — {attempt.Reason}");
                }
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
                stack.Push((node.Children[i], level + 1));
        }
    }
}

// ============================================================================
// Pipeline
// ============================================================================

public sealed class PipelineOptions
{
    public ForwardOptions Forward { get; init; } = new();

    public BackwardOptions Backward { get; init; } = new();

    public TimeSpan TotalTimeout { get; init; }
        = TimeSpan.FromSeconds(30);

    public bool RunBackward { get; init; } = true;

    public IProgress<string>? Progress { get; init; }

    public CancellationToken Cancellation { get; init; }
}

public sealed class PipelineResult
{
    public required ForwardResult Forward { get; init; }

    public List<GoalSolutions> Backward { get; } = new();

    public bool TimedOut { get; set; }

    public long ElapsedMs { get; set; }

    public List<string> Log { get; } = new();

    public int CycleHitsTotal
        => Backward
            .SelectMany(g => g.Results)
            .Sum(r => r.CycleHits);

    public int FailedDueToCycleCount
        => Backward
            .SelectMany(g => g.Results)
            .Count(r => r.FailedDueToCycle);
}

public static class PipelineRunner
{
    public static PipelineResult Run(
        KnowledgeBase kb,
        PipelineOptions? options = null)
    {
        var opt = options ?? new PipelineOptions();
        var sw = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource();

        if (opt.TotalTimeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(opt.TotalTimeout);

        using var cts =
            CancellationTokenSource.CreateLinkedTokenSource(
                opt.Cancellation,
                timeoutCts.Token);

        var token = cts.Token;

        var forwardOptions = new ForwardOptions
        {
            MaxPasses = opt.Forward.MaxPasses,
            MaxFacts = opt.Forward.MaxFacts,
            MaxMatches = opt.Forward.MaxMatches,
            MaxElapsedMs = opt.Forward.MaxElapsedMs,
            Goal = opt.Forward.Goal,
            Cancellation = token
        };

        opt.Progress?.Report("прямой вывод…");

        var forward = new ForwardChainer().Run(
            kb,
            forwardOptions);

        var result = new PipelineResult
        {
            Forward = forward
        };

        result.Log.Add(
            $"forward: pass={forward.Passes}, " +
            $"derived={forward.DerivedCount}, " +
            $"truncated={forward.MatchTruncations}, " +
            $"circular={forward.CircularSupports}, " +
            $"{forward.ElapsedMs} ms");

        if (token.IsCancellationRequested)
        {
            result.TimedOut = timeoutCts.IsCancellationRequested;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        if (opt.RunBackward && kb.Goals.Count > 0)
        {
            opt.Progress?.Report("обратный вывод…");

            var backwardOptions = new BackwardOptions
            {
                MaxDepth = opt.Backward.MaxDepth,
                MaxNodes = opt.Backward.MaxNodes,
                UseMemo = opt.Backward.UseMemo,
                ReuseMemo = true,
                MaxGoalInstances = opt.Backward.MaxGoalInstances,
                Cancellation = token
            };

            var backward = BackwardSolver.SolveAll(
                kb,
                forward.Memory,
                kb.Goals,
                backwardOptions);

            result.Backward.AddRange(backward);

            result.Log.Add(
                $"backward: goals={backward.Count}, " +
                $"instances={backward.Sum(g => g.Results.Count)}, " +
                $"proven={backward.Sum(g => g.Proven.Count())}, " +
                $"cycles={result.CycleHitsTotal}");
        }

        result.TimedOut = timeoutCts.IsCancellationRequested;
        result.ElapsedMs = sw.ElapsedMilliseconds;

        return result;
    }
}