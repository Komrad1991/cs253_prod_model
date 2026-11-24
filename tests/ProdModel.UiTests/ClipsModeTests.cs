using Xunit;

namespace ProdModel.UiTests;

/// <summary>Режим «CLIPS»: пример «Тиндер» должен реально считаться машиной вывода.</summary>
public class ClipsModeTests
{
    [StaFact]
    public void ClipsMode_loads_persons_program_and_produces_pair_messages()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        Assert.Equal("persons.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        Assert.Equal(1, clips.TestStep);
        Assert.Contains("У нас есть пара", clips.TestOutput);
    }

    [StaFact]
    public void ClipsMode_restart_clears_previous_run()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.Next();
        clips.Next();
        Assert.Equal(2, clips.TestStep);

        clips.Restart();
        Assert.Equal(0, clips.TestStep);
        Assert.DoesNotContain("У нас есть пара", clips.TestOutput);
    }

    [StaFact]
    public void ClipsMode_views_switch_between_log_facts_and_agenda()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.Next();
        Assert.Contains("У нас есть пара", clips.TestOutput);

        clips.TestSelectView(1);
        Assert.Contains("(person (name", clips.TestOutput);

        clips.TestSelectView(2);
        Assert.Contains("activation", clips.TestOutput);

        clips.TestSelectView(0);
        Assert.Contains("У нас есть пара", clips.TestOutput);
    }

    [StaFact]
    public void ClipsMode_program_list_offers_all_examples()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        var list = clips.TestProgramList();

        Assert.Contains("persons.clp", list);
        Assert.Contains("certainty.clp", list);
        Assert.Contains("certainty_signed.clp", list);
        Assert.Contains("certainty_demo.clp", list);
        Assert.Contains("certainty_demo_signed.clp", list);
        Assert.Contains("heckerman_cf.clp", list);
    }

    [StaFact]
    public void ClipsMode_certainty_program_computes_cf_for_forward_chain()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("certainty.clp");
        Assert.Equal("certainty.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        var text = clips.TestOutput;

        // Та же база знаний, что в C#-версии, но с коэффициентами уверенности.
        Assert.Contains("ИТОГ ПРЯМОГО ВЫВОДА С CF", text);
        Assert.Contains("Входных фактов: 121", text);
        Assert.Contains("порог cf: 0.100", text);

        // Все цели доказаны, у каждой посчитан cf.
        Assert.Contains("[доказана] recommended_MetalSkin_anti_channel", text);
        Assert.Contains("[доказана] second_pick_Curse_anti_purge", text);
        Assert.DoesNotContain("[не доказана]", text);

        // Цепочка считается по формулам: hero_Vindicta 0.75 -> threat_poke 0.675.
        Assert.Contains("threat_poke = 0.675", text);
    }

    [StaFact]
    public void ClipsMode_signed_certainty_program_uses_negative_cf()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("certainty_signed.clp");
        Assert.Equal("certainty_signed.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        var text = clips.TestOutput;

        // Диапазон [-1..1] и порог по модулю.
        Assert.Contains("ИТОГ ПРЯМОГО ВЫВОДА С CF (ДИАПАЗОН -1..1)", text);
        Assert.Contains("Входных фактов: 124", text);
        Assert.Contains("порог |cf|: 0.100", text);
        Assert.Contains("cf < 0 (против):", text);

        // Отрицательный входной факт действительно попал в программу.
        Assert.Contains("(fact (name hero_Vindicta) (cf -0.40)", clips.TestProgram);

        // Контрдовод опровергает рекомендацию, а не просто ослабляет её.
        Assert.Contains("[опровергнута] recommended_MetalSkin_anti_channel", text);
        Assert.Contains("recommended_EtherealShift_anti_execute = -0.", text);

        // Остальные цели остаются доказанными.
        Assert.Contains("[доказана] recommended_Curse_anti_channel", text);
        Assert.Contains("[доказана] i_answers_Knockdown", text);

        // В блок-схеме опровергнутая цель не считается доказанной.
        clips.TestSelectView(3);
        Assert.DoesNotContain("recommended_MetalSkin_anti_channel", clips.TestGraphGoalList());
    }

    [StaFact]
    public void ClipsMode_demo_certainty_program_keeps_cf_positive()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("certainty_demo.clp");
        Assert.Equal("certainty_demo.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        var text = clips.TestOutput;

        // Учебный мини-пример: 3 входа, 8 правил, 2 цели, все cf положительные.
        Assert.Contains("ИТОГ ПРЯМОГО ВЫВОДА С CF", text);
        Assert.Contains("Входных фактов: 3", text);
        Assert.Contains("порог cf: 0.100", text);

        // 0.90 * 0.90 = 0.810, затем 0.810 * 0.85 = 0.689.
        Assert.Contains("threat_poke = 0.810", text);
        Assert.Contains("need_anti_poke = 0.689", text);

        // Цепочка доходит до целей: 0.330 и 0.290.
        Assert.Contains("[доказана] recommended_Knockdown_anti_poke  cf 0.330", text);
        Assert.Contains("[доказана] recommended_MetalSkin_anti_channel  cf 0.290", text);
        Assert.DoesNotContain("[не доказана]", text);

        // Обе цели видны в блок-схеме.
        clips.TestSelectView(3);
        var goals = clips.TestGraphGoalList();
        Assert.Contains("recommended_Knockdown_anti_poke", goals);
        Assert.Contains("recommended_MetalSkin_anti_channel", goals);
    }

    [StaFact]
    public void ClipsMode_demo_signed_certainty_program_refutes_goals()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("certainty_demo_signed.clp");
        Assert.Equal("certainty_demo_signed.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        var text = clips.TestOutput;

        // Диапазон [-1..1] и порог по модулю.
        Assert.Contains("ИТОГ ПРЯМОГО ВЫВОДА С CF (ДИАПАЗОН -1..1)", text);
        Assert.Contains("Входных фактов: 6", text);
        Assert.Contains("порог |cf|: 0.100", text);
        Assert.Contains("cf < 0 (против):", text);

        // Отрицательный вход тянет цепочку в минус: -0.40 * 0.90 = -0.360,
        // затем -0.360 * 0.85 = -0.306.
        Assert.Contains("(fact (name hero_Vindicta) (cf -0.40)", clips.TestProgram);
        Assert.Contains("threat_poke = -0.360", text);
        Assert.Contains("need_anti_poke = -0.306", text);

        // Положительный контрдовод возвращает цель в доказанные:
        // (-0.196 + 0.560) / (1 - 0.196) = 0.453, * 0.75 = 0.340.
        Assert.Contains("[доказана] recommended_Knockdown_anti_poke  cf 0.340", text);

        // Отрицательные контрдоводы опровергают рекомендации.
        Assert.Contains("[опровергнута] recommended_MetalSkin_anti_channel  cf -0.652", text);
        Assert.Contains("[опровергнута] recommended_EtherealShift_anti_execute  cf -0.855", text);

        // В блок-схеме опровергнутые цели не считаются доказанными.
        clips.TestSelectView(3);
        var goals = clips.TestGraphGoalList();
        Assert.Contains("recommended_Knockdown_anti_poke", goals);
        Assert.DoesNotContain("recommended_MetalSkin_anti_channel", goals);
    }

    [StaFact]
    public void ClipsMode_heckerman_example_reproduces_article_numbers()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("heckerman_cf.clp");
        Assert.Equal("heckerman_cf.clp", clips.ProgramName, ignoreCase: true);

        clips.Next();
        var text = clips.TestOutput;

        Assert.Contains("ПРИМЕР ИЗ СТАТЬИ ХЕКЕРМАНА", text);
        Assert.Contains("Входных фактов: 4", text);

        // Уравнение 1: 0.5 (+) 0.9 = 0.95.
        // Уравнение 2: 0.99 * 0.95 = 0.9405.
        // Уравнение 3: 0.9 * min(0.7, 0.9) = 0.63.
        Assert.Contains("alarm = 0.9500", text);
        Assert.Contains("[доказана] burglary  cf 0.9405", text);
        Assert.Contains("[доказана] heart_attack  cf 0.6300", text);

        // Таблицы сравнения вариантов объединения CF.
        Assert.Contains("ЧЕМ МОЖНО ЗАМЕНИТЬ ОБЪЕДИНЕНИЕ CF", text);
        Assert.Contains("1. ПАРАЛЛЕЛЬНОЕ ОБЪЕДИНЕНИЕ", text);
        Assert.Contains("2. ПОСЛЕДОВАТЕЛЬНОЕ ОБЪЕДИНЕНИЕ", text);
        Assert.Contains("3. КОНЪЮНКЦИЯ И ДИЗЪЮНКЦИЯ ПОСЫЛОК", text);
        Assert.Contains("4. НЕСКОЛЬКО ПРИЧИН ОДНОГО СЛЕДСТВИЯ", text);
        Assert.Contains("5. КОРРЕЛИРОВАННЫЕ СВИДЕТЕЛЬСТВА", text);
        Assert.Contains("6. ПЕРЕКЛЮЧЕНИЕ РЕЖИМОВ", text);

        // Раздел 4.1: вера в кражу не падает, а растёт; R13 снижает её даже
        // без единого звонка.
        Assert.Contains("0.9405  кража: только звонки", text);
        Assert.Contains("0.9880  кража: звонки + новости", text);
        Assert.Contains("0.9760  кража: звонки + новости + R13", text);
        Assert.Contains("0.9004  кража: только новости + R13", text);

        // Раздел 4.2: три коррелированных сообщения считаются как независимые.
        Assert.Contains("0.8400  два сообщения", text);
        Assert.Contains("0.9360  три сообщения", text);

        // Режимы действительно управляют функциями вывода.
        Assert.Contains("0.9900  parallel=linear  (ALARM 1.0000)", text);
        Assert.Contains("0.8910  parallel=strongest  (ALARM 0.9000)", text);
        Assert.Contains("0.1485  serial=open", text);
        Assert.Contains("-0.4000  serial=open", text);
        Assert.Contains("Сейчас в файле: parallel=standard  serial=clamp", text);

        // В блок-схеме видны обе цели примера.
        clips.TestSelectView(3);
        var goals = clips.TestGraphGoalList();
        Assert.Contains("burglary", goals);
        Assert.Contains("heart_attack", goals);
    }

    [StaFact]
    public void ClipsMode_cf_mode_list_switches_variant_and_reruns()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("heckerman_cf.clp");

        // В примере есть ?*parallel* / ?*serial* / ?*and-mode*, значит список доступен.
        Assert.True(clips.TestCfModeEnabled);

        // В списке — сами формулы, а не названия режимов.
        var modes = clips.TestCfModeList();
        Assert.Equal(7, modes.Length);
        Assert.Equal("cf1+cf2−cf1·cf2 (уравнение 1)", modes[0]);
        Assert.Equal("λ1·λ2 → cf (уравнения 4–6)", modes[1]);
        Assert.Equal("max(cf1, cf2)", modes[3]);
        Assert.Equal("Π cf_i вместо min", modes[6]);

        clips.Next();
        Assert.Contains("Режимы: parallel=standard  serial=clamp  and=min", clips.TestOutput);
        Assert.Contains("[доказана] burglary  cf 0.9405", clips.TestOutput);

        // linear: 0.5 + 0.9 = 1.4 -> обрезка до 1.0, значит кража = 0.9900.
        clips.TestSelectCfMode(2);
        var linear = clips.TestOutput;
        Assert.Contains("Режимы: parallel=linear  serial=clamp  and=min", linear);
        Assert.Contains("alarm = 1.0000", linear);
        Assert.Contains("[доказана] burglary  cf 0.9900", linear);

        // Журнал очищается при перезапуске: отчёт в выводе ровно один.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(linear, "ПРИМЕР ИЗ СТАТЬИ ХЕКЕРМАНА"));

        // strongest: слабый звонок теряется, кража = 0.900 * 0.99 = 0.8910.
        clips.TestSelectCfMode(3);
        var strongest = clips.TestOutput;
        Assert.Contains("Режимы: parallel=strongest  serial=clamp  and=min", strongest);
        Assert.Contains("[доказана] burglary  cf 0.8910", strongest);

        // deadzone: посылка 0.15 ниже порога 0.2 — факты под сомнением не выводятся.
        clips.TestSelectCfMode(5);
        Assert.Contains("Режимы: parallel=standard  serial=deadzone  and=min", clips.TestOutput);
    }

    [StaFact]
    public void ClipsMode_cf_mode_list_is_disabled_for_programs_without_modes()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        Assert.False(clips.TestCfModeEnabled);

        clips.TestSelectProgram("heckerman_cf.clp");
        Assert.True(clips.TestCfModeEnabled);

        clips.TestSelectProgram("persons.clp");
        Assert.False(clips.TestCfModeEnabled);
    }

    [StaFact]
    public void ClipsMode_switching_program_back_restarts_engine()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;

        clips.EnsureReady();
        clips.TestSelectProgram("certainty.clp");
        clips.Next();
        Assert.Contains("ИТОГ ПРЯМОГО ВЫВОДА С CF", clips.TestOutput);

        clips.TestSelectProgram("persons.clp");
        Assert.Equal("persons.clp", clips.ProgramName, ignoreCase: true);
        Assert.Equal(0, clips.TestStep);
        Assert.DoesNotContain("ИТОГ ПРЯМОГО ВЫВОДА С CF", clips.TestOutput);

        clips.Next();
        Assert.Contains("У нас есть пара", clips.TestOutput);
    }

    [StaFact]
    public void ClipsMode_graph_view_draws_blocks_for_goal_trees()
    {
        using var form = new ProdModel.App.MainForm();
        form.Size = new Size(1500, 950);
        form.Show();
        Application.DoEvents();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;
        clips.EnsureReady();
        clips.TestSelectProgram("certainty.clp");
        clips.Next();

        clips.TestSelectView(3);
        Application.DoEvents();

        // По умолчанию выбрана первая цель — в схеме только её дерево вывода.
        var goals = clips.TestGraphGoalList();
        Assert.True(goals.Length > 1, "в списке целей: " + string.Join(", ", goals));
        Assert.True(clips.TestGraphNodes >= 3 && clips.TestGraphNodes <= 40,
            "узлов в дереве одной цели: " + clips.TestGraphNodes);

        // Режим «Цели» целиком: в схеме только факты из деревьев доказанных целей.
        clips.TestSelectGraphGoal(0);
        Application.DoEvents();
        Assert.True(clips.TestGraphNodes > 40, "узлов в схеме целей: " + clips.TestGraphNodes);
        Assert.True(clips.TestGraphNodes < 400, "схема целей не должна включать всю базу");

        // Вся база: 121 входной факт + выведенные.
        clips.TestSelectGraphMode(1);
        Application.DoEvents();
        Assert.True(clips.TestGraphNodes > 400, "узлов в полной схеме: " + clips.TestGraphNodes);

        // Отрисовка не должна падать и должна оставлять след на битмапе.
        var panel = clips.TestGraphView;
        using var bitmap = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(bitmap, new Rectangle(0, 0, panel.Width, panel.Height));

        var painted = 0;
        for (var x = 0; x < bitmap.Width; x += 5)
        for (var y = 0; y < bitmap.Height; y += 5)
            if (bitmap.GetPixel(x, y).ToArgb() != bitmap.GetPixel(0, bitmap.Height - 1).ToArgb()) painted++;

        Assert.True(painted > 100, "на схеме что-то нарисовано: пикселей " + painted);

        form.Hide();
    }

    [StaFact]
    public void ClipsMode_graph_is_empty_for_program_without_graph_block()
    {
        using var form = new ProdModel.App.MainForm();

        form.TestSetModeIndex(2);
        var clips = form.TestClips;
        clips.EnsureReady();
        clips.Next();

        clips.TestSelectView(3);
        Assert.Equal(0, clips.TestGraphNodes);
    }

    [StaFact]
    public void ClipsMode_is_hidden_until_selected()
    {
        using var form = new ProdModel.App.MainForm();

        // Visible у дочернего контрола осмысленно только у показанной формы.
        form.Show();
        Application.DoEvents();

        Assert.False(form.TestClips.Visible);

        form.TestSetModeIndex(2);
        Assert.True(form.TestClips.Visible);

        form.TestSetModeIndex(0);
        Assert.False(form.TestClips.Visible);

        form.Hide();
    }
}
