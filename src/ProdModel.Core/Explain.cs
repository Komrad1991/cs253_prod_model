using System.Text;

namespace ProdModel.Core;



/// <summary>Формирование текстовых объяснений для прямого и обратного вывода.</summary>
public static class TextExplainer
{
    public static string ExplainFiring(Firing firing)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Шаг {firing.Step}, слой {firing.Layer}: правило {firing.Rule.Id} (cf правила {Certainty.Format(firing.RuleCf)})");
        if (!string.IsNullOrWhiteSpace(firing.Rule.Why)) sb.AppendLine($"  Смысл правила: {firing.Rule.Why}");
        sb.AppendLine("  Посылки:");
        if (firing.Premises.Count == 0) sb.AppendLine("    • (посылок нет)");
        foreach (var premise in firing.Premises)
        {
            sb.AppendLine($"    • {premise.Text} cf={Certainty.Format(premise.Cf)}" +
                          (premise.IsDerived ? $" (слой {premise.Depth}, правило {premise.Rule?.Id ?? "—"})" : " (входной факт)"));
        }

        var premiseMin = firing.PremiseCf;

        sb.AppendLine("  Вывод:");
        var combined = false;
        foreach (var conclusion in firing.Conclusions)
        {
            var differs = Math.Abs(conclusion.Cf - firing.Cf) > 0.005;
            combined |= differs;

            sb.AppendLine($"    • {conclusion.Text} cf={Certainty.Format(conclusion.Cf)}" +
                          (differs
                              ? $" (вклад этого срабатывания {Certainty.Format(firing.Cf)}; cf повышен объединением свидетельств)"
                              : string.Empty));
        }

        sb.AppendLine($"  Расчёт: cf этого срабатывания = cf правила {Certainty.Format(firing.RuleCf)} × " +
                      $"min(cf посылок) {Certainty.Format(premiseMin)} = {Certainty.Format(firing.Cf)}");
        if (combined)
        {
            sb.AppendLine("  Пояснение: факт уже был выведен другими правилами, поэтому его cf выше — " +
                          "свидетельства складываются по формуле cf₁ + cf₂ × (1 − cf₁).");
        }

        if (firing.Bindings.Count > 0) sb.AppendLine($"  Подстановка: {firing.BindingsText}");
        return sb.ToString();
    }

    /// <summary>Дерево обоснования факта (итеративный обход, без рекурсии).</summary>
    public static string ExplainFact(Fact fact, int maxNodes = 300)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Факт: {fact.Text}  cf={Certainty.Format(fact.Cf)}");
        sb.AppendLine(fact.IsDerived
            ? $"Источник: выведен правилом {fact.Rule?.Id ?? "—"} на слое {fact.Depth}"
            : "Источник: задан во входном файле");

        if (fact.Derivations.Count > 0)
        {
            sb.AppendLine("Все обоснования:");
            foreach (var derivation in fact.Derivations.OrderByDescending(d => d.Cf).Take(10))
            {
                sb.AppendLine($"    • {derivation.Rule.Id} (cf={Certainty.Format(derivation.Cf)}; шаг {derivation.Step})");
            }
        }

        var stack = new Stack<(Fact Fact, int Level)>();
        stack.Push((fact, 0));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = 0;

        while (stack.Count > 0 && nodes++ < maxNodes)
        {
            var (current, level) = stack.Pop();
            if (level > 0)
            {
                var kind = current.IsDerived ? $"← {current.Rule?.Id}" : "← вход";
                sb.AppendLine(new string(' ', level * 2) + $"• {current.Text} cf={Certainty.Format(current.Cf)}  {kind}");
            }

            if (!visited.Add(current.Key)) continue;

            for (var i = current.Premises.Count - 1; i >= 0; i--)
            {
                stack.Push((current.Premises[i], level + 1));
            }
        }

        return sb.ToString();
    }

    public static string ExplainBackward(BackwardResult result, bool includeRejected = false)
    {
        var sb = new StringBuilder();
        var status = result.Proven
            ? $"ЦЕЛЬ ДОКАЗАНА, cf={Certainty.Format(result.Cf)}"
            : $"ЦЕЛЬ НЕ ДОКАЗАНА ({result.FailureReason})";

        sb.AppendLine($"Цель: {result.Goal}  →  {status}");
        sb.AppendLine($"Узлов: {result.NodesVisited}, слоёв: {result.Layers}, " +
                      $"циклов предотвращено: {result.CycleHits}, повторов из кэша: {result.MemoHits}, " +
                      $"время: {result.ElapsedMs} мс");

        sb.AppendLine();
        sb.AppendLine(result.Proven
            ? "Дерево доказательства (только продукции, участвующие в выводе цели):"
            : "Дерево поиска (причина неудачи):");
        RenderTree(sb, result.Root, includeRejected);

        if (result.Proven)
        {
            sb.AppendLine();
            sb.AppendLine("Нормальный порядок (посылки раньше заключений):");
            var n = 1;
            foreach (var node in result.Steps)
            {
                var via = node.Fact is not null
                    ? "факт"
                    : node.Rule is null ? "—" : $"правило {node.Rule.Id}";
                var neg = node.Negated ? "НЕ " : string.Empty;
                sb.AppendLine($"  {n++,2}. {neg}{node.Goal} cf={Certainty.Format(node.Cf)} — {via}");
            }
        }

        return sb.ToString();
    }

    private static void RenderTree(StringBuilder sb, SearchNode root, bool includeRejected)
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
                SearchState.Cyclic => "⟲",
                SearchState.DepthLimit => "…",
                _ => "?"
            };

            var via = node.Fact is not null
                ? "факт из базы"
                : node.Rule is null ? "—" : $"правило {node.Rule.Id}";
            var bindings = node.Bindings.Count > 0 ? " [" + node.BindingsText + "]" : string.Empty;

            sb.AppendLine($"{indent}{mark} {node.Label} cf={Certainty.Format(node.Cf)} ← {via}{bindings}" +
                          (string.IsNullOrEmpty(node.Note) ? string.Empty : $"  // {node.Note}"));

            if (includeRejected)
            {
                foreach (var rejected in node.Rejected)
                {
                    sb.AppendLine($"{indent}   ✘ отклонено: {rejected}");
                }
            }

            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push((node.Children[i], level + 1));
            }
        }
    }
}
