using ProdModel.Core;
using Xunit;

namespace ProdModel.UiTests;

/// <summary>
/// Проверки загрузки базы знаний: диагностика небезопасных правил и
/// локализация ошибок разбора.
/// </summary>
public sealed class KbLoadingTests
{
    private static Rule RuleWith(
        string id,
        IEnumerable<Predicate> conditions,
        IEnumerable<Predicate> conclusions)
    {
        var rule = new Rule
        {
            Id = id,
            Line = 7,
            SourceFile = "test.txt"
        };

        foreach (var condition in conditions)
            rule.Conditions.Add(new Condition(condition, false));

        foreach (var conclusion in conclusions)
            rule.Conclusions.Add(conclusion);

        return rule;
    }

    private static KnowledgeBase NewKb(params Rule[] rules)
    {
        var kb = new KnowledgeBase();
        kb.AddFact(new Fact(new Predicate("seed"), FactOrigin.Input));

        foreach (var rule in rules)
            kb.AddRule(rule);

        return kb;
    }

    [Fact]
    public void Validator_WarnsAboutHeadVariableNotBoundByConditions()
    {
        var kb = NewKb(RuleWith(
            "R1",
            new[] { new Predicate("a") },
            new[] { new Predicate("g", Arg.Var("x")) }));

        KbValidator.Validate(kb);

        var warning = Assert.Single(
            kb.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning);

        Assert.Contains("R1", warning.Message);
        Assert.Contains("?x", warning.Message);
        Assert.Equal(7, warning.Line);
    }

    [Fact]
    public void Validator_AcceptsBodyOnlyVariable()
    {
        var kb = NewKb(RuleWith(
            "R2",
            new[] { new Predicate("a", Arg.Var("x")), new Predicate("b") },
            new[] { new Predicate("c") }));

        KbValidator.Validate(kb);

        Assert.DoesNotContain(
            kb.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Loader_ReportsEmptyRuleElementWithFileAndLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "prodmodel_test_empty_element.txt");
        File.WriteAllText(path, "ИМЯ: broken\nПРАВИЛА:\na,,b -> c\nЦЕЛИ:\nc\n");

        var error = Assert.Throws<KbParseException>(() => KbLoader.Load(path));

        Assert.Equal(3, error.Line);
        Assert.Contains("пустой элемент", error.Message);
    }
}
