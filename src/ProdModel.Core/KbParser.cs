using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProdModel.Core;



public sealed class KbParseException : Exception
{
    public string FilePath { get; }
    public int Line { get; }

    public KbParseException(string filePath, int line, string message)
        : base($"{Path.GetFileName(filePath)}:{line}: {message}")
    {
        FilePath = filePath;
        Line = line;
    }
}

/// <summary>
/// Загрузчик базы знаний из текстового формата.
///
/// Формат (см. docs/format.md):
///   kb "Название"
///   description "..."
///   sort item = MetalSkin, Curse
///   fact enemy(Haze) cf=0.95
///   rule HZ_BULLET cf=0.9
///     if enemy(Haze)
///     then threat(bullet_dps)
///     why "текст объяснения"
///   goal pick(?i:item, ?n:need)
///   include "other.txt"
/// </summary>
public static class KbLoader
{
    public static KnowledgeBase Load(params string[] paths) => Load((IEnumerable<string>)paths);

    public static KnowledgeBase Load(IEnumerable<string> paths)
    {
        var kb = new KnowledgeBase();
        var loader = new Loader(kb);
        var any = false;
        foreach (var path in paths)
        {
            loader.LoadFile(Path.GetFullPath(path));
            any = true;
        }

        if (!any) throw new InvalidOperationException("Не указан ни один файл базы знаний.");

        KbValidator.Validate(kb);
        return kb;
    }
}

internal sealed class Loader
{
    private static readonly Regex CfRegex = new(@"\bcf\s*[=:]?\s*(-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly KnowledgeBase _kb;
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _stack = new();

    private RuleBuilder? _currentRule;

    public Loader(KnowledgeBase kb)
    {
        _kb = kb;
    }

    public void LoadFile(string fullPath)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Файл базы знаний не найден: {fullPath}", fullPath);

        if (!_visited.Add(fullPath))
        {
            _kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Повторное включение файла пропущено: {Path.GetFileName(fullPath)}",
                File = fullPath
            });
            return;
        }

        if (!_kb.Sources.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) _kb.Sources.Add(fullPath);

        var lines = File.ReadAllLines(fullPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNo = i + 1;
            var raw = StripComment(lines[i]).Trim();
            if (raw.Length == 0) continue;

            var directive = FirstToken(raw, out var rest);
            switch (directive.ToLowerInvariant())
            {
                case "kb":
                    _kb.Name = Unquote(rest.Trim());
                    break;

                case "description":
                    _kb.Description = Unquote(rest.Trim());
                    break;

                case "sort":
                    ParseSort(rest, fullPath, lineNo);
                    break;

                case "fact":
                    FlushRule();
                    ParseFact(rest, fullPath, lineNo);
                    break;

                case "goal":
                    FlushRule();
                    ParseGoal(rest, fullPath, lineNo);
                    break;

                case "include":
                    FlushRule();
                    Include(rest, fullPath, lineNo);
                    break;

                case "rule":
                    FlushRule();
                    StartRule(rest, fullPath, lineNo);
                    break;

                case "if":
                case "and":
                    RequireRule(fullPath, lineNo, directive);
                    ParseCondition(rest, fullPath, lineNo);
                    break;

                case "then":
                    RequireRule(fullPath, lineNo, directive);
                    ParseConclusion(rest, fullPath, lineNo);
                    break;

                case "why":
                    RequireRule(fullPath, lineNo, directive);
                    _currentRule!.Why = Unquote(rest.Trim());
                    break;

                default:
                    throw new KbParseException(fullPath, lineNo, $"Неизвестная директива «{directive}». Ожидалось: kb, description, sort, fact, rule, if, then, why, goal, include.");
            }
        }

        FlushRule();
    }

    private void Include(string rest, string file, int line)
    {
        var target = Unquote(rest.Trim());
        if (target.Length == 0) throw new KbParseException(file, line, "Не указан файл для include.");

        var full = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? ".", target));

        if (_stack.Contains(full, StringComparer.OrdinalIgnoreCase))
            throw new KbParseException(file, line, $"Циклический include: {Path.GetFileName(full)}");

        _stack.Push(full);
        try
        {
            LoadFile(full);
        }
        finally
        {
            _stack.Pop();
        }
    }

    private void ParseSort(string rest, string file, int line)
    {
        var eq = rest.IndexOf('=');
        if (eq < 0) throw new KbParseException(file, line, "Ожидалось «sort имя = значение, значение, ...».");

        var name = rest[..eq].Trim();
        if (name.Length == 0) throw new KbParseException(file, line, "Не указано имя сорта.");

        var values = rest[(eq + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0);

        _kb.AddSort(name, values);
    }

    private void ParseFact(string rest, string file, int line)
    {
        var (atomText, cf) = ExtractCf(rest, 1.0, file, line);
        var atom = ParsePredicate(atomText, file, line);

        if (!atom.IsGround)
            throw new KbParseException(file, line, $"Факт не может содержать переменных: {atom}");

        _kb.AddFact(new Fact(atom, cf, FactOrigin.Input));
    }

    private void ParseGoal(string rest, string file, int line)
    {
        var text = rest.Trim();
        if (text.Length == 0) throw new KbParseException(file, line, "Пустая цель.");

        var raw = ParsePredicate(text, file, line);
        var sorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var args = new Arg[raw.Args.Count];

        // Разметка сортов у переменных: pick(?i:item, ?n:need)
        for (var i = 0; i < args.Length; i++)
        {
            var arg = raw.Args[i];
            if (!arg.IsVariable)
            {
                args[i] = arg;
                continue;
            }

            var colon = arg.Name.IndexOf(':');
            if (colon > 0)
            {
                var variable = arg.Name[..colon];
                var sort = arg.Name[(colon + 1)..].Trim();
                if (sort.Length > 0) sorts[variable] = sort;
                args[i] = Arg.Var(variable);
            }
            else
            {
                args[i] = arg;
            }
        }

        var goal = new GoalDefinition
        {
            Pattern = new Predicate(raw.Name, args),
            Line = line,
            SourceFile = file
        };
        foreach (var kv in sorts) goal.VariableSorts[kv.Key] = kv.Value;
        _kb.Goals.Add(goal);
    }

    private void StartRule(string rest, string file, int line)
    {
        var (headText, cf) = ExtractCf(rest, 1.0, file, line);
        var id = headText.Trim().TrimEnd(':').Trim();
        if (id.Length == 0) throw new KbParseException(file, line, "Не указан идентификатор правила.");

        _currentRule = new RuleBuilder(id, cf, file, line);
    }

    private void ParseCondition(string rest, string file, int line)
    {
        foreach (var part in SplitOnAnd(rest))
        {
            var text = part.Trim();
            var negated = false;
            if (text.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            {
                negated = true;
                text = text[4..].Trim();
            }
            else if (text.StartsWith("не ", StringComparison.OrdinalIgnoreCase))
            {
                negated = true;
                text = text[3..].Trim();
            }

            if (text.Length == 0) throw new KbParseException(file, line, "Пустое условие правила.");
            _currentRule!.Conditions.Add(new Condition(ParsePredicate(text, file, line), negated));
        }
    }

    /// <summary>Разбивает строку условий по « and » / « и » верхнего уровня (вне скобок и кавычек).</summary>
    private static List<string> SplitOnAnd(string text)
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
            if (inQuotes) continue;
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
            if (depth != 0) continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1])) continue;

            var isAnd = (c is 'a' or 'A') && MatchesWord(text, i, "and");
            var isRuAnd = (c is 'и' or 'И') && MatchesWord(text, i, "и");
            if (!isAnd && !isRuAnd) continue;

            var wordLength = isAnd ? 3 : 1;
            var after = i + wordLength;
            if (after >= text.Length || !char.IsWhiteSpace(text[after])) continue;

            result.Add(text[start..i].Trim());
            start = after;
            i = after - 1;
        }

        result.Add(text[start..].Trim());
        return result.Where(p => p.Length > 0).ToList();
    }

    private static bool MatchesWord(string text, int index, string word)
    {
        if (index + word.Length > text.Length) return false;
        return string.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private void ParseConclusion(string rest, string file, int line)
    {
        var text = rest.Trim();
        if (text.Length == 0) throw new KbParseException(file, line, "Пустое заключение правила.");
        _currentRule!.Conclusions.Add(ParsePredicate(text, file, line));
    }

    private void RequireRule(string file, int line, string directive)
    {
        if (_currentRule is null)
            throw new KbParseException(file, line, $"Директива «{directive}» вне правила.");
    }

    private void FlushRule()
    {
        if (_currentRule is null) return;

        var rule = _currentRule.Build();
        if (rule.Conditions.Count == 0)
            _kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Правило {rule.Id} без условий — сработает всегда.",
                File = rule.SourceFile,
                Line = rule.Line
            });

        if (rule.Conclusions.Count == 0)
            throw new KbParseException(rule.SourceFile ?? "?", rule.Line, $"У правила {rule.Id} нет заключения (then).");

        _kb.AddRule(rule);
        _currentRule = null;
    }

    private (string text, double cf) ExtractCf(string input, double defaultCf, string file, int line)
    {
        var match = CfRegex.Match(input);
        if (!match.Success) return (input.Trim(), defaultCf);

        var raw = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var cf))
            throw new KbParseException(file, line, $"Не удалось разобрать коэффициент уверенности «{match.Value}».");

        if (cf < 0 || cf > 1)
        {
            _kb.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Коэффициент {cf:0.###} вне диапазона [0,1] — приведён к допустимому.",
                File = file,
                Line = line
            });
            cf = Certainty.Clamp(cf);
        }

        return (input.Remove(match.Index, match.Length).Trim(), cf);
    }

    private static Predicate ParsePredicate(string text, string file, int line)
    {
        text = text.Trim();
        if (text.Length == 0) throw new KbParseException(file, line, "Пустой предикат.");

        var open = text.IndexOf('(');
        if (open < 0) return new Predicate(text, Array.Empty<Arg>());

        if (!text.EndsWith(')'))
            throw new KbParseException(file, line, $"Не закрыта скобка в предикате «{text}».");

        var name = text[..open].Trim();
        if (name.Length == 0) throw new KbParseException(file, line, "Не указано имя предиката.");

        var inner = text[(open + 1)..^1].Trim();
        if (inner.Length == 0) return new Predicate(name, Array.Empty<Arg>());

        var parts = inner.Split(',');
        var args = new List<Arg>(parts.Length);
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0) throw new KbParseException(file, line, "Пустой аргумент предиката.");
            args.Add(token.StartsWith('?') ? Arg.Var(token[1..]) : Arg.Const(token));
        }

        return new Predicate(name, args);
    }

    internal static string FirstToken(string line, out string rest)
    {
        var idx = 0;
        while (idx < line.Length && !char.IsWhiteSpace(line[idx])) idx++;
        var token = line[..idx];
        rest = idx < line.Length ? line[(idx + 1)..].Trim() : string.Empty;
        return token;
    }

    internal static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == '#' && !inQuotes) return line[..i];
        }
        return line;
    }

    internal static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return text[1..^1].Replace("\\\"", "\"");
        return text;
    }

    private sealed class RuleBuilder
    {
        private readonly string _id;
        private readonly double _cf;
        private readonly string _file;
        private readonly int _line;

        public RuleBuilder(string id, double cf, string file, int line)
        {
            _id = id;
            _cf = cf;
            _file = file;
            _line = line;
        }

        public List<Condition> Conditions { get; } = new();
        public List<Predicate> Conclusions { get; } = new();
        public string Why { get; set; } = string.Empty;

        public Rule Build()
        {
            var rule = new Rule
            {
                Id = _id,
                Cf = _cf,
                Why = Why,
                Line = _line,
                SourceFile = _file
            };
            rule.Conditions.AddRange(Conditions);
            rule.Conclusions.AddRange(Conclusions);
            return rule;
        }
    }
}


/// <summary>Семантические проверки базы знаний (не мешают работе, но видны в отчёте и в интерфейсе).</summary>
public static class KbValidator
{
    public static void Validate(KnowledgeBase kb)
    {
        if (kb.Rules.Count == 0)
            kb.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "В базе знаний нет ни одного правила." });
        if (kb.Facts.Count == 0)
            kb.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "В базе знаний нет ни одного факта." });

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in kb.Rules)
        {
            if (!ruleIds.Add(rule.Id))
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Повторяющийся идентификатор правила «{rule.Id}».",
                    File = rule.SourceFile,
                    Line = rule.Line
                });

            if (rule.Conditions.Count == 0)
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Правило {rule.Id} без посылок — сработает всегда.",
                    File = rule.SourceFile,
                    Line = rule.Line
                });

            // Переменные отрицательных условий обязаны быть связаны раньше.
            var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var condition in rule.Conditions)
            {
                if (condition.Negated)
                {
                    foreach (var v in condition.Pattern.Variables)
                    {
                        if (!bound.Contains(v))
                            kb.Diagnostics.Add(new Diagnostic
                            {
                                Severity = DiagnosticSeverity.Error,
                                Message = $"Правило {rule.Id}: в отрицании {condition.Pattern} переменная ?{v} не связана предыдущими условиями.",
                                File = rule.SourceFile,
                                Line = rule.Line
                            });
                    }
                }
                else
                {
                    foreach (var v in condition.Pattern.Variables) bound.Add(v);
                }
            }

            // Безопасность для обратного вывода: все переменные тела должны быть в заключении.
            var headVars = rule.HeadVariables();
            var unsafeVars = rule.BodyVariables().Where(v => !headVars.Contains(v)).ToList();
            if (unsafeVars.Count > 0)
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Правило {rule.Id} не является безопасным для обратного вывода: переменные {string.Join(", ", unsafeVars.Select(v => "?" + v))} не входят в заключение. " +
                           "Обратный вывод пропустит это правило (прямой вывод работает).",
                    File = rule.SourceFile,
                    Line = rule.Line
                });
        }

        var factKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in kb.Facts)
        {
            if (!factKeys.Add(fact.Key))
                kb.Diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"Повторяющийся факт {fact.Text}."
                });
        }

        foreach (var goal in kb.Goals)
        {
            foreach (var (variable, sort) in goal.VariableSorts)
            {
                if (kb.GetSort(sort) is null)
                    kb.Diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Message = $"Цель {goal}: для переменной ?{variable} указан неизвестный сорт «{sort}».",
                        File = goal.SourceFile,
                        Line = goal.Line
                    });
            }
        }

        kb.Diagnostics.Add(new Diagnostic
        {
            Severity = DiagnosticSeverity.Info,
            Message = $"Загружено: фактов {kb.Facts.Count}, правил {kb.Rules.Count}, целей {kb.Goals.Count}, предикатов {kb.Predicates().Count()}."
        });
    }
}
