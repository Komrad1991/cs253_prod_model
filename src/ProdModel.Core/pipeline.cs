using System.Diagnostics;

namespace ProdModel.Core;

/// <summary>Параметры полного прогона: загрузка → прямой вывод → обратный вывод → сборка.</summary>
public sealed class PipelineOptions
{
    public ForwardOptions Forward { get; init; } = new();
    public BackwardOptions Backward { get; init; } = new();
    public ReverseQueryOptions Reverse { get; init; } = new();

    /// <summary>Общий wall-clock лимит на весь pipeline.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Запускать обратный вывод по целям БЗ (если цели есть).</summary>
    public bool RunBackward { get; init; } = true;

    /// <summary>Собирать сборку по результату прямого вывода.</summary>
    public bool RunBuild { get; init; } = true;

    public IProgress<string>? Progress { get; init; }
}

public sealed class PipelineResult
{
    public required ForwardResult Forward { get; init; }
    public List<GoalSolutions> Backward { get; } = new();
    public BuildPlan? Build { get; set; }
    public bool TimedOut { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Log { get; } = new();

    /// <summary>Суммарное число обнаруженных циклов в обратном выводе (по всем целям).</summary>
    public int CycleHitsTotal =>
        Backward.SelectMany(g => g.Results).Sum(r => r.CycleHits);

    /// <summary>Сколько целей провалились именно из-за цикла.</summary>
    public int FailedDueToCycleCount =>
        Backward.SelectMany(g => g.Results).Count(r => r.FailedDueToCycle);

    /// <summary>Сами результаты, проваленные из-за цикла (для UI-раздела «циклы»).</summary>
    public IEnumerable<BackwardResult> CycleResults =>
        Backward.SelectMany(g => g.Results).Where(r => r.FailedDueToCycle);
}

/// <summary>
/// Оркестратор: гоняет этапы пайплайна с общим таймаутом и отменой,
/// логирует время каждого этапа. Зависание отдельного этапа превращается
/// в «частичный» результат вместо вечного ожидания.
/// </summary>
public static class PipelineRunner
{
    public static PipelineResult Run(KnowledgeBase kb, PipelineOptions? options = null)
    {
        var opt = options ?? new PipelineOptions();
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(opt.TotalTimeout);

        var forwardOptions = new ForwardOptions
        {
            MaxPasses = opt.Forward.MaxPasses,
            MaxFacts = opt.Forward.MaxFacts,
            MaxMatches = opt.Forward.MaxMatches,
            Threshold = opt.Forward.Threshold,
            MaxElapsedMs = opt.Forward.MaxElapsedMs,
            Cancellation = cts.Token
        };

        opt.Progress?.Report("прямой вывод…");
        var forward = new ForwardChainer().Run(kb, forwardOptions);
        var result = new PipelineResult { Forward = forward };
        result.Log.Add($"forward: pass={forward.Passes}, derived={forward.DerivedCount}, " +
                       $"truncated={forward.MatchTruncations}, circular={forward.CircularSupports}, " +
                       $"{forward.ElapsedMs} ms");

        if (cts.IsCancellationRequested)
        {
            result.TimedOut = true;
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
                Cancellation = cts.Token
            };
            var backward = BackwardSolver.SolveAll(kb, forward.Memory, kb.Goals, backwardOptions);
            result.Backward.AddRange(backward);
            result.Log.Add($"backward: goals={backward.Count}, " +
                           $"instances={backward.Sum(g => g.Results.Count)}, " +
                           $"proven={backward.Sum(g => g.Proven.Count())}, " +
                           $"cycles={result.CycleHitsTotal}, " +
                           $"failedByCycle={result.FailedDueToCycleCount}");
        }

        if (opt.RunBuild && !cts.IsCancellationRequested)
        {
            opt.Progress?.Report("сборка…");
            try
            {
                result.Build = new BuildPlanner().Build(forward, kb);
                result.Log.Add($"build: chosen={result.Build.Chosen.Count}, " +
                               $"overflow={result.Build.Overflow.Count}, " +
                               $"actives={result.Build.ActiveCount}");
            }
            catch (Exception ex)
            {
                result.Log.Add("build: ошибка " + ex.Message);
            }
        }

        result.TimedOut = cts.IsCancellationRequested;
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }
}