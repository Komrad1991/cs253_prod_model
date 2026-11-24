#!/usr/bin/env python3
"""Генератор data/certainty_signed.clp — та же база знаний Deadlock, что и в
data/certainty.clp, но коэффициенты уверенности реально работают в диапазоне
[-1..1]: положительный cf — свидетельство ЗА факт, отрицательный — ПРОТИВ.

Отличия от data/certainty.clp:
  * входной факт hero_Vindicta получает cf -0.40 (разведка сомневается, что
	Vindicta в матче), поэтому её угрозы частично «гасятся»;
  * добавлены три входных факта-контрдовода и три правила с отрицательным cf:
	  patch_nerfed_MetalSkin   -> recommended_MetalSkin_anti_channel   -0.60
	  ally_has_heal_reduction  -> recommended_ToxicBullets_anti_heal   -0.65
	  enemy_has_item_disable   -> recommended_EtherealShift_anti_execute -0.95
  * порог применяется к |cf|, а факт обновляется при изменении |new - old|;
  * в отчёте цель с cf < 0 помечается как «опровергнута».

Запуск:  python tools/gen_certainty_signed_clp.py
"""

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "data" / "deadlock_kb.txt"
DST = ROOT / "data" / "certainty_signed.clp"

# Общие с обычным генератором разбор БЗ и таблица положительных cf.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_certainty_clp import FACT_CF, RULE_CF, conclusion_cf, parse_kb, split_rule  # noqa: E402

# cf входных фактов: к обычным разведданным добавлен отрицательный пример.
SIGNED_FACT_CF = dict(FACT_CF)
SIGNED_FACT_CF["hero_Vindicta"] = -0.40

FACT_NOTES = {
	"hero_Shiv": "разведданные: Shiv не в каждом матче",
	"hero_Vindicta": "разведка: Vindicta, скорее, не в матче",
}

# Входные факты-контрдоводы, которых нет в обычной БЗ.
EXTRA_FACTS = [
	("patch_nerfed_MetalSkin", 1.00, "патч ослабил Metal Skin"),
	("ally_has_heal_reduction", 1.00, "лечение уже сокращает союзник"),
	("enemy_has_item_disable", 0.90, "у врага есть отключение предметов (Curse)"),
]

# Правила с ОТРИЦАТЕЛЬНЫМ cf: посылки дают свидетельство ПРОТИВ заключения.
EXTRA_RULES = [
	(["patch_nerfed_MetalSkin"], ["recommended_MetalSkin_anti_channel"], -0.60,
	 "Metal Skin ослаблен патчем: рекомендация теряет уверенность."),
	(["ally_has_heal_reduction"], ["recommended_ToxicBullets_anti_heal"], -0.65,
	 "Лечение уже сокращает союзник: свои Toxic Bullets не нужны."),
	(["enemy_has_item_disable"], ["recommended_EtherealShift_anti_execute"], -0.95,
	 "Curse врага отключает Ethereal Shift: рекомендация опровергается."),
]

HEADER = """;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: контр-предметы — КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ В ДИАПАЗОНЕ [-1..1]
;;  Схема вывода: только ПРЯМОЙ вывод. Обратный вывод не рассматривается.
;;
;;  Основа — та же база знаний, что в data/certainty.clp (data/deadlock_kb.txt):
;;  {base_facts} входной факт и {base_rules} правило. Здесь дополнительно
;;  добавлены контрдоводы — итого {facts} входных факта и {rules} правила.
;;  Отличие от certainty.clp только в модели CF: коэффициенты могут быть
;;  отрицательными.
;;
;;  СМЫСЛ ЗНАКА
;;    cf > 0  — свидетельство ЗА факт (чем ближе к 1, тем увереннее);
;;    cf = 0  — свидетельств нет;
;;    cf < 0  — свидетельство ПРОТИВ факта (чем ближе к -1, тем сильнее).
;;  Отрицательный факт остаётся в рабочей памяти: он нужен, чтобы передать
;;  контрдовод дальше по цепочке и чтобы объяснить, почему цель отброшена.
;;
;;  МОДЕЛЬ УВЕРЕННОСТИ (EMYCIN, Shortliffe–Buchanan)
;;    * посылки правила соединяются через И  ->  cf = min(cf1, cf2, ...);
;;      если среди посылок есть отрицательная, вывод тоже становится
;;      отрицательным (свидетельство против заключения);
;;    * у правила свой cf, в том числе отрицательный:
;;      вывод правила: cf = min(посылки) * cf_правила;
;;    * если факт выводится несколькими правилами, оценки объединяются:
;;          cf1 + cf2 * (1 - cf1)                 — обе оценки > 0
;;          cf1 + cf2 * (1 + cf1)                 — обе оценки < 0
;;          (cf1 + cf2) / (1 - min(|cf1|, |cf2|)) — оценки разных знаков
;;      при |cf1| = |cf2| = 1 и разных знаках результат равен 0 (полный
;;      конфликт свидетельств), деления на ноль нет;
;;    * факт с |cf| < ?*threshold* в базу не заносится: правило «не дотянуло».
;;      Порог по умолчанию 0.10 — при нём часть целей может стать
;;      опровергнутой (recommended_MetalSkin_anti_channel), а остальные
;;      остаются доказанными; отрицательные рекомендации видны со знаком минус;
;;    * повторный вывод того же факта: в ?*contrib* хранится вклад КАЖДОГО
;;      правила, поэтому повторное срабатывание ЗАМЕНЯЕТ вклад правила, а не
;;      добавляет его ещё раз;
;;    * факт обновляется, когда |new - old| > ?*epsilon*: отрицательный
;;      контрдовод может как повысить, так и понизить cf (в обычной модели
;;      [0..1] обновление было только при росте);
;;    * ЦИКЛЫ: слот anc хранит факты, от которых зависит вывод. Если правило
;;      пытается вывести факт из числа своих предков, выводка отбрасывается
;;      и считается в ?*cycles*.
;;
;;  ВХОДНЫЕ ФАКТЫ С НЕУВЕРЕННОСТЬЮ (|cf| < 1) И ОТРИЦАТЕЛЬНЫЕ КОНТРДОВОДЫ
{negative_inputs}
;;    правила-контрдоводы (отрицательный cf):
{negative_rules}
;;
;;  ПОЛОЖИТЕЛЬНЫЕ CF ПРАВИЛ (слой вывода определяется префиксом заключения):
{cf_table}
;;
;;  ЗАПУСК:  clips -f data/certainty_signed.clp
;;           либо выбрать файл в списке примеров режима CLIPS приложения.
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
"""

MACHINERY = """
; ============================================================================
;  1. МАШИНЕРИЯ КОЭФФИЦИЕНТОВ УВЕРЕННОСТИ В ДИАПАЗОНЕ [-1..1]
; ============================================================================

; Порог по МОДУЛЮ: и слабое «за», и слабое «против» в базу не попадают.
(defglobal ?*threshold* = 0.10)

; Насколько |cf| должен измениться, чтобы факт считался обновлённым.
(defglobal ?*epsilon* = 0.0005)

; Последний напечатанный слой — чтобы заголовок слоя выводился один раз.
(defglobal ?*shown-layer* = 0)

; Сколько выводов правил отброшено порогом (для отчёта).
(defglobal ?*rejected* = 0)

; Сколько раз цикл в правилах был предотвращён (для отчёта).
(defglobal ?*cycles* = 0)

; Вклад каждого правила в каждый факт: тройки (факт правило значение).
(defglobal ?*contrib* = (create$))

; Целевые факты из базы знаний: для них печатается цепочка объяснения.
(defglobal ?*goals* = (create$ {goals}))

; Шаблон факта: имя, уверенность, чем и на каком слое выведен.
(deftemplate fact
   (slot name (type SYMBOL))
   (slot cf (type FLOAT) (default 1.0))
   (slot depth (type INTEGER) (default 0))
   (slot rule (type SYMBOL) (default input))
   (slot rcf (type FLOAT) (default 1.0))
   (slot from (type STRING) (default ""))
   ; anc — все факты, от которых этот вывод зависит (защита от циклов)
   (slot anc (type STRING) (default "")))

; Красивый вывод числа.
(deffunction fmt (?x)
   (format nil "%.3f" ?x))

; Объединение двух оценок одного и того же факта (формулы EMYCIN).
; Диапазон входа и результата — [-1..1].
(deffunction cf-combine (?a ?b)
   (if (and (> ?a 0) (> ?b 0)) then
	  (+ ?a (* ?b (- 1.0 ?a)))
	else
	  (if (and (< ?a 0) (< ?b 0)) then
		 (+ ?a (* ?b (+ 1.0 ?a)))
	   else
		 (if (or (eq ?a 0.0) (eq ?b 0.0)) then
			(+ ?a ?b)
		   else
			 (bind ?den (- 1.0 (min (abs ?a) (abs ?b))))
			 (if (> ?den 0.0) then (/ (+ ?a ?b) ?den) else 0.0)))))

; Адрес факта по имени или FALSE, если такого факта нет.
(deffunction fact-address (?name)
   (bind ?found (find-all-facts ((?f fact)) (eq ?f:name ?name)))
   (if (> (length$ ?found) 0) then (nth$ 1 ?found) else FALSE))
; Сокращение для чтения слота (в deffunction запись ?f:slot не работает).
(deffunction slot (?f ?name)
   (fact-slot-value ?f ?name))

; Печать заголовка слоя при первом факте этого слоя.
(deffunction show-layer (?depth)
   (if (> ?depth ?*shown-layer*) then
	  (bind ?*shown-layer* ?depth)
	  (printout t crlf "── слой " ?depth " ──────────────────────────────────────" crlf)))

; Все имена, от которых уже зависит вывод: посылки и их предки.
; Если среди них есть сам выводимый факт — это цикл, такую выводку отбрасываем.
(deffunction ancestors (?from)
   (bind ?set (create$))
   (foreach ?p (explode$ ?from)
	  (if (not (member$ ?p ?set)) then (bind ?set (create$ ?set ?p)))
	  (bind ?f (fact-address ?p))
	  (if (eq (type ?f) FACT-ADDRESS) then
		 (foreach ?a (explode$ (slot ?f anc))
			(if (not (member$ ?a ?set)) then (bind ?set (create$ ?set ?a))))))
   ?set)

; Итоговая уверенность факта: EMYCIN-свёртка вкладов всех его правил.
(deffunction cf-total (?name)
   (bind ?total 0.0)
   (bind ?i 1)
   (bind ?n (length$ ?*contrib*))
   (while (<= ?i ?n)
	  (if (eq (nth$ ?i ?*contrib*) ?name) then
		 (bind ?total (cf-combine ?total (nth$ (+ ?i 2) ?*contrib*))))
	  (bind ?i (+ ?i 3)))
   ?total)

; Записать вклад правила в факт, заменив прежний вклад этого же правила.
(deffunction contrib-put (?name ?rule ?value)
   (bind ?out (create$))
   (bind ?i 1)
   (bind ?found FALSE)
   (bind ?n (length$ ?*contrib*))
   (while (<= ?i ?n)
	  (bind ?t (nth$ ?i ?*contrib*))
	  (bind ?r (nth$ (+ ?i 1) ?*contrib*))
	  (if (and (eq ?t ?name) (eq ?r ?rule)) then
		 (bind ?out (create$ ?out ?t ?r ?value))
		 (bind ?found TRUE)
	   else
		 (bind ?out (create$ ?out ?t ?r (nth$ (+ ?i 2) ?*contrib*))))
	  (bind ?i (+ ?i 3)))
   (if (not ?found) then (bind ?out (create$ ?out ?name ?rule ?value)))
   (bind ?*contrib* ?out))

; Главная операция: занести вывод правила в базу с учётом уверенности.
;   ?cf   — уверенность посылок (min по всем посылкам)
;   ?rcf  — cf самого правила (может быть отрицательным)
(deffunction derive (?name ?cf ?rcf ?rule ?from ?depth)
   (bind ?value (* ?cf ?rcf))
   (if (< (abs ?value) ?*threshold*) then
	  (bind ?*rejected* (+ ?*rejected* 1))
	  (return FALSE))
   (bind ?anc (ancestors ?from))
   (if (member$ ?name ?anc) then
	  (bind ?*cycles* (+ ?*cycles* 1))
	  (printout t "  цикл предотвращён: " ?rule " -> " ?name
				 " (посылки уже опираются на этот факт)" crlf)
	  (return FALSE))
   (bind ?slot (fact-address ?name))
   (contrib-put ?name ?rule ?value)
   (bind ?new (cf-total ?name))
   (if (eq (type ?slot) FACT-ADDRESS) then
	  (bind ?old (slot ?slot cf))
	  (if (<= (abs (- ?new ?old)) ?*epsilon*) then
		 (return FALSE))
	  (modify ?slot (cf ?new) (depth (min ?depth (slot ?slot depth))) (rule ?rule) (rcf ?rcf) (from ?from) (anc (implode$ ?anc)))
	  (show-layer (slot ?slot depth))
	  (printout t "  " ?name " = " (fmt ?new)
				 "  (было " (fmt ?old) ", вклад " ?rule " = " (fmt ?value) ")" crlf)
	else
	  (assert (fact (name ?name) (cf ?new) (depth ?depth) (rule ?rule) (rcf ?rcf) (from ?from) (anc (implode$ ?anc))))
	  (show-layer ?depth)
	  (printout t "  " ?name " = " (fmt ?value) "  <- " ?rule
				 " (cf правила " (fmt ?rcf) ") из: " ?from crlf)))
"""

REPORT = """
; ============================================================================
;  3. ОТЧЁТ И ОБЪЯСНЕНИЯ
; ============================================================================

; Рекурсивное объяснение: факт -> правило -> его посылки -> ...
(deffunction explain (?name ?indent ?seen)
   (bind ?f (fact-address ?name))
   (if (eq (type ?f) FACT-ADDRESS) then
	  (printout t ?indent ?name "  cf " (fmt (slot ?f cf)) "  слой " (slot ?f depth)
			 (if (< (slot ?f cf) 0.0) then "  (против)" else "") crlf)
	  (if (or (eq (slot ?f rule) input) (member$ ?name ?seen) (> (length$ ?seen) 12)) then
		 (return))
	  (printout t ?indent "   правило " (slot ?f rule) " (cf " (fmt (slot ?f rcf)) ")" crlf)
	  (bind ?seen (create$ ?seen ?name))
	  (foreach ?p (explode$ (slot ?f from))
		 (explain ?p (str-cat ?indent "     ") ?seen))
	else
	  (printout t ?indent ?name "  — не выведен (|cf| ниже порога)" crlf)))

; Итог: сколько выведено, диапазон cf, цели с объяснением.
(deffunction report ()
   (bind ?all (find-all-facts ((?f fact)) TRUE))
   (bind ?derived (create$))
   (bind ?inputs 0)
   (bind ?maxdepth 0)
   (bind ?pos 0)
   (bind ?neg 0)
   (bind ?mincf 1.0)
   (bind ?maxcf -1.0)
   (foreach ?f ?all
	  (if (neq (slot ?f rule) input) then
		 (bind ?derived (create$ ?derived ?f))
		 (if (> (slot ?f depth) ?maxdepth) then (bind ?maxdepth (slot ?f depth)))
	   else
		 (bind ?inputs (+ ?inputs 1))))
   (foreach ?f ?all
	  (bind ?c (slot ?f cf))
	  (if (> ?c 0.0) then (bind ?pos (+ ?pos 1)))
	  (if (< ?c 0.0) then (bind ?neg (+ ?neg 1)))
	  (if (< ?c ?mincf) then (bind ?mincf ?c))
	  (if (> ?c ?maxcf) then (bind ?maxcf ?c)))
   (printout t crlf
	  "======== ИТОГ ПРЯМОГО ВЫВОДА С CF (ДИАПАЗОН -1..1) ========" crlf
	  "Входных фактов: " ?inputs "   выведено: " (length$ ?derived) crlf
	  "Глубина вывода: " ?maxdepth " слоёв   порог |cf|: " (fmt ?*threshold*) crlf
	  "Отброшено порогом: " ?*rejected* " выводов правил (и всё, что из них следует)" crlf
	  "Предотвращено циклов: " ?*cycles* crlf
	  "cf > 0 (за): " ?pos "   cf < 0 (против): " ?neg crlf
	  "Диапазон cf в рабочей памяти: " (fmt ?mincf) " .. " (fmt ?maxcf) crlf)

   (bind ?d 1)
   (while (<= ?d ?maxdepth)
	  (printout t crlf "Слой " ?d ":" crlf)
	  (foreach ?f ?derived
		 (if (eq (slot ?f depth) ?d) then
			(printout t "  " (fmt (slot ?f cf)) "  " (slot ?f name) crlf)))
	  (bind ?d (+ ?d 1)))

   (printout t crlf "ЦЕЛИ (cf > 0 — доказана, cf < 0 — опровергнута):" crlf)
   (foreach ?g ?*goals*
	  (bind ?f (fact-address ?g))
	  (if (eq (type ?f) FACT-ADDRESS) then
		 (bind ?c (slot ?f cf))
		 (if (> ?c 0.0) then
			(printout t "  [доказана] " ?g "  cf " (fmt ?c) crlf)
		   else
			 (if (< ?c 0.0) then
				(printout t "  [опровергнута] " ?g "  cf " (fmt ?c) crlf)
			   else
				(printout t "  [нейтральна] " ?g "  cf " (fmt ?c) crlf)))
	   else
		 (printout t "  [не доказана] " ?g crlf)))

   (printout t crlf "ОБЪЯСНЕНИЕ ЦЕЛЕЙ (факт -> правило -> посылки):" crlf)
   (foreach ?g ?*goals*
	 (bind ?f (fact-address ?g))
	 (if (eq (type ?f) FACT-ADDRESS) then
			  (printout t crlf "* " ?g crlf)
			  (explain ?g "   " (create$))))
		(printout t crlf "=========================================================" crlf)

   ; Машиночитаемый слепок вывода для визуализации блоками в приложении.
   ; N|слой|cf|имя|правило|cf правила|посылки   (посылки через пробел, "-" если нет)
   ; G|цель|cf                                  (cf "-" если цель не доказана)
   (printout t "#GRAPH" crlf)
   (foreach ?f ?all
	  (printout t "N|" (slot ?f depth) "|" (fmt (slot ?f cf)) "|" (slot ?f name) "|"
				 (slot ?f rule) "|" (fmt (slot ?f rcf)) "|"
				 (if (eq (slot ?f from) "") then "-" else (slot ?f from)) crlf))
   (foreach ?g ?*goals*
	  (bind ?f (fact-address ?g))
	  (printout t "G|" ?g "|" (if (eq (type ?f) FACT-ADDRESS) then (fmt (slot ?f cf)) else "-") crlf))
   (printout t "#END" crlf))

; Отчёт печатается, когда агенда исчерпана: самая низкая салиентность.
(defrule report-and-halt
   (declare (salience -10000))
   =>
   (report)
   (halt))
"""


def emit_rule(index, premises, conclusions, comment, rule_cf=None):
	out = []
	label = f"r{index:03d}"
	if comment:
		out.append(f"; {comment}")
	out.append(f";   {', '.join(premises)} -> {', '.join(conclusions)}")
	out.append(f"(defrule {label}")
	for i, premise in enumerate(premises, start=1):
		out.append(f"   (fact (name {premise}) (cf ?c{i}) (depth ?d{i}))")
	out.append("   =>")
	if len(premises) == 1:
		premise_cf = "?c1"
		depth = "(+ 1 ?d1)"
	else:
		premise_cf = "(min " + " ".join(f"?c{i}" for i in range(1, len(premises) + 1)) + ")"
		depth = "(+ 1 (max " + " ".join(f"?d{i}" for i in range(1, len(premises) + 1)) + "))"
	source = " ".join(premises)
	for conclusion in conclusions:
		cf = rule_cf if rule_cf is not None else conclusion_cf(conclusion)
		out.append(f"   (derive {conclusion} {premise_cf} {cf:.2f} "
				   f"{label} \"{source}\" {depth})")
	out.append(")")
	return "\n".join(out)


def main():
	facts, rules, goals = parse_kb()

	negative_inputs = "\n".join(
		f";;    {name:<28} {cf:+.2f}   {FACT_NOTES.get(name, '')}"
		for name, cf in SIGNED_FACT_CF.items() if cf != 1.0)
	negative_rules = "\n".join(
		f";;    {', '.join(premises):<28} -> {', '.join(conclusions):<42} {cf:+.2f}"
		for premises, conclusions, cf, _ in EXTRA_RULES)
	cf_table = "\n".join(
		f";;    {prefix:<28} {cf:+.2f}   {title}" for prefix, cf, title in RULE_CF)

	all_facts = len(facts) + len(EXTRA_FACTS)
	all_rules = len(rules) + len(EXTRA_RULES)

	parts = [HEADER.format(facts=all_facts, rules=all_rules, base_facts=len(facts),
						   base_rules=len(rules), cf_table=cf_table,
						   negative_inputs=negative_inputs, negative_rules=negative_rules)]
	parts.append(MACHINERY.format(goals=" ".join(goals)))

	parts.append("""
; ============================================================================
;  2. ВХОДНЫЕ ФАКТЫ (слой 0; cf может быть отрицательным)
; ============================================================================

(deffacts input-facts""")
	for name in facts:
		cf = SIGNED_FACT_CF.get(name, 1.0)
		parts.append(f"   (fact (name {name}) (cf {cf:.2f}) (depth 0) (rule input))")
	for name, cf, comment in EXTRA_FACTS:
		parts.append(f"   ; {comment}")
		parts.append(f"   (fact (name {name}) (cf {cf:.2f}) (depth 0) (rule input))")
	parts.append(")")

	parts.append("""
; ============================================================================
;  2. ПРАВИЛА (прямой вывод; cf правила — по слою заключения)
; ============================================================================
""")
	for index, line in enumerate(rules, start=1):
		premises, conclusions, comment = split_rule(line)
		parts.append(emit_rule(index, premises, conclusions, comment))
		parts.append("")

	parts.append("""
; ============================================================================
;  2. ПРАВИЛА-КОНТРДОВОДЫ (отрицательный cf)
; ============================================================================
""")
	for index, (premises, conclusions, cf, comment) in enumerate(EXTRA_RULES, start=len(rules) + 1):
		parts.append(emit_rule(index, premises, conclusions, comment, rule_cf=cf))
		parts.append("")

	parts.append(REPORT)

	DST.write_text("\n".join(parts), encoding="utf-8", newline="\n")
	print(f"{DST}: фактов {all_facts}, правил {all_rules}, целей {len(goals)}, "
		  f"строк {len('\n'.join(parts).splitlines())}")


if __name__ == "__main__":
	main()
