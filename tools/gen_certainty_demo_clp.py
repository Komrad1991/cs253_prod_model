#!/usr/bin/env python3
"""Генератор двух УЧЕБНЫХ мини-примеров с коэффициентами уверенности.

В отличие от полной базы (``gen_certainty_clp.py`` — 121 факт / 431 правило)
здесь одна короткая цепочка вывода, на которой видно, как считаются cf:

  data/certainty_demo.clp         — cf в диапазоне [0..1];
  data/certainty_demo_signed.clp  — cf в диапазоне [-1..1] (есть контрдоводы).

Машинерия (deftemplate, cf-combine, derive, отчёт, блок #GRAPH) берётся из
больших генераторов без изменений, чтобы примеры демонстрировали ровно ту же
модель, что и certainty.clp / certainty_signed.clp.

Запуск: python tools/gen_certainty_demo_clp.py
"""

import sys
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
ROOT = TOOLS.parent
sys.path.insert(0, str(TOOLS))

from gen_certainty_clp import MACHINERY as POSITIVE_MACHINERY  # noqa: E402
from gen_certainty_clp import REPORT as POSITIVE_REPORT  # noqa: E402
from gen_certainty_signed_clp import MACHINERY as SIGNED_MACHINERY  # noqa: E402
from gen_certainty_signed_clp import REPORT as SIGNED_REPORT  # noqa: E402
from gen_certainty_signed_clp import emit_rule  # noqa: E402

DST_POSITIVE = ROOT / "data" / "certainty_demo.clp"
DST_SIGNED = ROOT / "data" / "certainty_demo_signed.clp"

# В большой базе комментарий к порогу говорит про её 9 целей — в мини-примере
# это неверно, поэтому подсказка заменяется на уместную для демонстрации.
POSITIVE_MACHINERY_DEMO = POSITIVE_MACHINERY.replace(
	"; Порог: факты с меньшей уверенностью в базу не попадают.\n"
	"; 0.10 — все 9 целей доказываются; 0.30 — 5 слабых целей отсекаются.",
	"; Порог: факты с меньшей уверенностью в базу не попадают.\n"
	"; В этом мини-примере поднимите порог до 0.35 — и обе цели перестанут\n"
	"; доказываться: цепочка обрывается на последнем звене.")

POSITIVE_HEADER = """
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: УЧЕБНЫЙ МИНИ-ПРИМЕР
;;  КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ В ДИАПАЗОНЕ [0..1]
;;
;;  Зачем: показать механику cf на короткой цепочке ({facts} входных факта,
;;  {rules} правил, {goals} цели) вместо полной базы certainty.clp
;;  (121 факт, 431 правило). Схема вывода — только ПРЯМОЙ вывод.
;;
;;  ЦЕПОЧКА ВЫВОДА (в скобках — cf правила):
;;    hero_Vindicta 0.90
;;      -> threat_poke (0.90)
;;      -> need_anti_poke (0.85)
;;    need_anti_poke + item_provides_Knockdown_anti_poke
;;      -> candidate_Knockdown_anti_poke (0.80)
;;      -> pick_Knockdown_anti_poke (0.80)
;;      -> recommended_Knockdown_anti_poke (0.75)
;;    need_anti_poke + item_provides_MetalSkin_anti_channel
;;      -> candidate_MetalSkin_anti_channel (0.75)
;;      -> pick_MetalSkin_anti_channel (0.75)
;;      -> recommended_MetalSkin_anti_channel (0.75)
;;
;;  МОДЕЛЬ [0..1] (EMYCIN, Shortliffe–Buchanan):
;;    * посылки правила соединяются через И  ->  cf = min(cf посылок);
;;    * cf вывода = min(cf посылок) * cf правила;
;;    * один факт выводят несколько правил -> cf1 + cf2 * (1 - cf1);
;;    * факт с cf < ?*threshold* (0.10) в базу не попадает;
;;    * повторный вывод того же правила ЗАМЕНЯЕТ свой вклад, а не добавляет;
;;    * циклы отсекаются по слою anc (зависимости факта).
;;
;;  ОЖИДАЕМЫЙ РЕЗУЛЬТАТ при пороге 0.10:
;;    threat_poke                            0.810
;;    need_anti_poke                         0.689
;;    recommended_Knockdown_anti_poke        0.330  [доказана]
;;    recommended_MetalSkin_anti_channel     0.290  [доказана]
;;
;;  Сравните с certainty_demo_signed.clp: там те же правила, но cf может быть
;;  отрицательным, и часть целей опровергается.
;;
;;  ЗАПУСК:  clips -f data/certainty_demo.clp
;;           либо выбрать пример в списке режима CLIPS приложения.
;;  Сгенерирован: python tools/gen_certainty_demo_clp.py
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
"""

SIGNED_HEADER = """
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: УЧЕБНЫЙ МИНИ-ПРИМЕР
;;  КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ В ДИАПАЗОНЕ [-1..1]
;;
;;  Та же цепочка, что в certainty_demo.clp, но cf может быть отрицательным:
;;  -1 — «определённо против», 0 — «ничего не известно», +1 — «определённо за».
;;  ({facts} входных фактов, {rules} правил, {goals} цели)
;;
;;  ЧТО ДОБАВЛЕНО К certainty_demo.clp:
;;    * hero_Vindicta = -0.40: разведка сомневается, что Vindicta в матче, —
;;      вся цепочка угрозы poke уходит в минус;
;;    * ally_reports_Knockdown_good = +0.80: союзник подтверждает Knockdown,
;;      и положительный вклад перевешивает отрицательный (оценки разных знаков
;;      складываются как (a + b) / (1 - min(|a|, |b|))), поэтому цель снова
;;      доказана;
;;    * patch_nerfed_MetalSkin -> recommended_MetalSkin_anti_channel (cf -0.60):
;;      контрдовод перевешивает, цель ОПРОВЕРГНУТА;
;;    * enemy_has_item_disable -> recommended_EtherealShift_anti_execute
;;      (cf -0.95): одного правила достаточно, чтобы опровергнуть цель.
;;
;;  МОДЕЛЬ [-1..1] (отличия от [0..1]):
;;    * порог считается по модулю: |cf| < ?*threshold* — вывод отбрасывается,
;;      поэтому слабое «против» тоже не попадает в базу;
;;    * обе оценки положительны: a + b * (1 - a);
;;    * обе оценки отрицательны: a + b * (1 + a);
;;    * знаки разные:             (a + b) / (1 - min(|a|, |b|));
;;    * факт обновляется, когда |cf| меняется больше ?*epsilon*.
;;
;;  ОЖИДАЕМЫЙ РЕЗУЛЬТАТ при пороге 0.10:
;;    threat_poke                            -0.360
;;    need_anti_poke                         -0.306
;;    recommended_Knockdown_anti_poke         0.340  [доказана]
;;    recommended_MetalSkin_anti_channel     -0.652  [опровергнута]
;;    recommended_EtherealShift_anti_execute -0.855  [опровергнута]
;;
;;  ЗАПУСК:  clips -f data/certainty_demo_signed.clp
;;           либо выбрать пример в списке режима CLIPS приложения.
;;  Сгенерирован: python tools/gen_certainty_demo_clp.py
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
"""

# (имя, cf, комментарий) — входные факты слоя 0.
POSITIVE_FACTS = [
	("hero_Vindicta", 0.90, "разведка: враг — Vindicta"),
	("item_provides_Knockdown_anti_poke", 0.95, "Knockdown — контр-предмет от poke"),
	("item_provides_MetalSkin_anti_channel", 0.90, "MetalSkin — контр-предмет от channel"),
]

SIGNED_FACTS = [
	("hero_Vindicta", -0.40, "разведка: Vindicta, скорее, не в матче"),
	("item_provides_Knockdown_anti_poke", 0.95, "Knockdown — контр-предмет от poke"),
	("item_provides_MetalSkin_anti_channel", 0.90, "MetalSkin — контр-предмет от channel"),
	("ally_reports_Knockdown_good", 0.80, "союзник подтверждает Knockdown"),
	("patch_nerfed_MetalSkin", 1.00, "патч ослабил MetalSkin"),
	("enemy_has_item_disable", 0.90, "у врага есть disable"),
]

# (посылки, заключения, cf правила, комментарий)
COMMON_RULES = [
	(["hero_Vindicta"], ["threat_poke"], 0.90, "Vindicta угрожает poke"),
	(["threat_poke"], ["need_anti_poke"], 0.85, "нужен контр-предмет от poke"),
	(["need_anti_poke", "item_provides_Knockdown_anti_poke"],
	 ["candidate_Knockdown_anti_poke"], 0.80, "Knockdown подходит под потребность"),
	(["candidate_Knockdown_anti_poke"], ["pick_Knockdown_anti_poke"], 0.80,
	 "выбираем Knockdown"),
	(["pick_Knockdown_anti_poke"], ["recommended_Knockdown_anti_poke"], 0.75,
	 "итоговая рекомендация"),
	(["need_anti_poke", "item_provides_MetalSkin_anti_channel"],
	 ["candidate_MetalSkin_anti_channel"], 0.75, "MetalSkin подходит под потребность"),
	(["candidate_MetalSkin_anti_channel"], ["pick_MetalSkin_anti_channel"], 0.75,
	 "выбираем MetalSkin"),
	(["pick_MetalSkin_anti_channel"], ["recommended_MetalSkin_anti_channel"], 0.75,
	 "итоговая рекомендация"),
]

SIGNED_RULES = COMMON_RULES + [
	(["patch_nerfed_MetalSkin"], ["recommended_MetalSkin_anti_channel"], -0.60,
	 "патч ослабил MetalSkin — рекомендация отменяется"),
	(["enemy_has_item_disable"], ["recommended_EtherealShift_anti_execute"], -0.95,
	 "у врага есть disable — EtherealShift не спасает"),
	(["ally_reports_Knockdown_good"], ["pick_Knockdown_anti_poke"], 0.70,
	 "союзник подтверждает Knockdown"),
]

POSITIVE_GOALS = ["recommended_Knockdown_anti_poke", "recommended_MetalSkin_anti_channel"]

SIGNED_GOALS = [
	"recommended_Knockdown_anti_poke",
	"recommended_MetalSkin_anti_channel",
	"recommended_EtherealShift_anti_execute",
]


def build(header, machinery, report, facts, rules, goals):
	parts = [header.format(facts=len(facts), rules=len(rules), goals=len(goals))]
	parts.append(machinery.format(goals=" ".join(goals)))

	parts.append("""
; ============================================================================
;  2. ВХОДНЫЕ ФАКТЫ (слой 0)
; ============================================================================

(deffacts input-facts""")
	for name, cf, comment in facts:
		parts.append(f"   ; {comment}")
		parts.append(f"   (fact (name {name}) (cf {cf:.2f}) (depth 0) (rule input))")
	parts.append(")")

	parts.append("""
; ============================================================================
;  3. ПРАВИЛА (прямой вывод)
; ============================================================================
""")
	for index, (premises, conclusions, cf, comment) in enumerate(rules, start=1):
		parts.append(emit_rule(index, premises, conclusions, comment, rule_cf=cf))
		parts.append("")

	parts.append(report)
	return "\n".join(parts)


def write(path, text):
	path.write_text(text, encoding="utf-8", newline="\n")
	print(f"{path}: строк {len(text.splitlines())}")


def main():
	write(DST_POSITIVE, build(POSITIVE_HEADER, POSITIVE_MACHINERY_DEMO, POSITIVE_REPORT,
							   POSITIVE_FACTS, COMMON_RULES, POSITIVE_GOALS))
	write(DST_SIGNED, build(SIGNED_HEADER, SIGNED_MACHINERY, SIGNED_REPORT,
							SIGNED_FACTS, SIGNED_RULES, SIGNED_GOALS))


if __name__ == "__main__":
	main()
