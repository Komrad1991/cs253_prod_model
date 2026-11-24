#!/usr/bin/env python3
"""Генератор data/certainty.clp — та же база знаний, что в C#-версии, но с
коэффициентами уверенности (CF) и только прямым выводом.

Источник: data/deadlock_kb.txt (пропозициональный формат:
	ФАКТЫ:   имя - Отображаемое имя
	ПРАВИЛА: посылка1, посылка2 -> заключение1, заключение2
	ЦЕЛИ:    имя

Что делает генератор:
  * каждый факт превращается в (fact (name ...) (cf ...) (depth 0) (rule input));
  * каждое правило — в defrule, который считает cf = min(посылки) * cf_правила;
  * cf правила берётся из таблицы слоёв (по префиксу заключения);
  * шапка файла + отчёт с объяснениями пишутся здесь же.

Запуск:  python tools/gen_certainty_clp.py
"""

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "data" / "deadlock_kb.txt"
DST = ROOT / "data" / "certainty.clp"

# CF правил по слою вывода: (префикс заключения, cf, подпись слоя).
# Проверяются по порядку, поэтому длинные префиксы идут первыми.
RULE_CF = [
	("expensive_pick_", 0.95, "свойства предмета: дорогой"),
	("cheap_pick_", 0.95, "свойства предмета: дешёвый"),
	("mid_pick_", 0.95, "свойства предмета: средний по цене"),
	("active_pick_", 0.95, "свойства предмета: активный"),
	("threat_", 0.90, "угроза героя"),
	("need_", 0.85, "потребность в контр-предмете"),
	("candidate_", 0.80, "кандидат под потребность"),
	("pick_", 0.80, "выбор контр-предмета"),
	("recommended_", 0.75, "итоговая рекомендация"),
	("second_pick_", 0.70, "второй контр-предмет"),
	("warning_", 0.65, "предупреждение о связке"),
	("enemy_buys_", 0.60, "гипотеза: что купит враг"),
	("enemy_answers_", 0.60, "гипотеза: чем враг ответит"),
	("i_answers_", 0.55, "наш ответ на ответ врага"),
]

# CF входных фактов: справочные данные о предметах считаем точными,
# а по героям врага допускаем неуверенность (разведданные).
FACT_CF = {
	"hero_Haze": 1.00,
	"hero_Seven": 1.00,
	"hero_MoKrill": 1.00,
	"hero_Shiv": 0.85,     # разведданные: Shiv не в каждом матче
	"hero_Vindicta": 0.75,  # разведданные: Vindicta под вопросом
}

HEADER = """;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: контр-предметы — КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ (CF)
;;  Схема вывода: только ПРЯМОЙ вывод. Обратный вывод не рассматривается.
;;
;;  Основа — та же база знаний, что в C#-версии (data/deadlock_kb.txt):
;;  {facts} входных фактов и {rules} правил. Файл сгенерирован из неё
;;  скриптом tools/gen_certainty_clp.py, поэтому состав фактов и правил
;;  совпадает с лабораторной, добавлена только работа с CF.
;;
;;  МОДЕЛЬ УВЕРЕННОСТИ (EMYCIN, Shortliffe–Buchanan)
;;    * у факта есть слот cf — вещественное число в диапазоне [-1..1],
;;      на практике здесь [0..1]; входные факты получают cf из разведданных;
;;    * посылки правила соединяются через И  ->  cf = min(cf1, cf2, ...)
;;      (уверенность правила не выше самого слабого звена);
;;    * у правила свой cf; вывод правила: cf = min(посылки) * cf_правила;
;;    * если факт выводится несколькими правилами, оценки объединяются:
;;          cf1 + cf2 * (1 - cf1)                 — обе оценки > 0
;;          cf1 + cf2 * (1 + cf1)                 — обе оценки < 0
;;          (cf1 + cf2) / (1 - min(|cf1|, |cf2|)) — оценки разных знаков
;;    * факт с cf < ?*threshold* в базу не заносится: правило «не дотянуло»
;;      и в объяснении не участвует. Порог по умолчанию 0.10 — при нём
;;      доказываются все 9 целей (как и без CF), но цели второго эшелона
;;      (second_pick_*) получают cf 0.12–0.22, т.е. видно, что они слабые.
;;      Поднимите порог до 0.30 — и все 5 целей second_pick_* честно
;;      перестанут доказываться (отсекаются 33 вывода правил и всё, что из
;;      них следует); попутно исчезает и цикл: его факты не добирают cf;
;;    * повторный вывод того же факта: в ?*contrib* хранится вклад КАЖДОГО
;;      правила (тройки факт–правило–значение), при повторном срабатывании
;;      вклад правила ЗАМЕНЯЕТСЯ, а итог пересчитывается заново. Поэтому
;;      пересчёт не «накапливает» одно и то же свидетельство дважды и cf
;;      не уползает к 1.0;
;;    * ЦИКЛЫ: у факта есть слот anc — множество фактов, от которых вывод
;;      зависит (посылки + их anc). Если правило пытается вывести факт,
;;      который уже есть среди его предков, выводка отбрасывается и
;;      считается в ?*cycles*. Поэтому цикл вида
;;          enemy_answers_Knockdown -> i_answers_Knockdown
;;          i_answers_Knockdown     -> enemy_answers_Knockdown
;;      не «подпирает» сам себя и не раздувает cf до 1.0: остаётся честная
;;      оценка от независимой цепочки, а цикл виден в отчёте.
;;
;;  ОТКУДА ВЗЯТ CF ПРАВИЛ (слой вывода определяется префиксом заключения):
{cf_table}
;;
;;  ЗАПУСК:  clips -f data/certainty.clp
;;           либо кнопка «Дальше» в режиме CLIPS приложения (файл уже в data).
;;  Нужна последовательность срабатываний? Добавьте в начало файла
;;  (watch rules) — это штатная трассировка самого CLIPS.
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
"""

MACHINERY = """
; ============================================================================
;  1. МАШИНЕРИЯ КОЭФФИЦИЕНТОВ УВЕРЕННОСТИ
; ============================================================================

; Порог: факты с меньшей уверенностью в базу не попадают.
; 0.10 — все 9 целей доказываются; 0.30 — 5 слабых целей отсекаются.
(defglobal ?*threshold* = 0.10)

; Насколько cf должен вырасти, чтобы считаться улучшением (защита от дребезга).
(defglobal ?*epsilon* = 0.0005)

; Последний напечатанный слой — чтобы заголовок слоя выводился один раз.
(defglobal ?*shown-layer* = 0)

; Сколько выводов правил отброшено порогом (для отчёта).
(defglobal ?*rejected* = 0)

; Сколько раз цикл в правилах был предотвращён (для отчёта).
(defglobal ?*cycles* = 0)

; Вклад каждого правила в каждый факт: тройки (факт правило значение).
; Нужен, чтобы повторное срабатывание того же правила ЗАМЕНЯЛО свой вклад,
; а не добавляло его ещё раз (иначе cf растёт от каждого пересчёта).
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
(deffunction cf-combine (?a ?b)
   (if (and (> ?a 0) (> ?b 0)) then
	  (+ ?a (* ?b (- 1.0 ?a)))
	else
	  (if (and (< ?a 0) (< ?b 0)) then
		 (+ ?a (* ?b (+ 1.0 ?a)))
	   else
		 (/ (+ ?a ?b) (- 1.0 (min (abs ?a) (abs ?b)))))))

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
;   ?rcf  — cf самого правила
(deffunction derive (?name ?cf ?rcf ?rule ?from ?depth)
   (bind ?value (* ?cf ?rcf))
   (if (< ?value ?*threshold*) then
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
	  (if (<= ?new (+ ?old ?*epsilon*)) then
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
	  (printout t ?indent ?name "  cf " (fmt (slot ?f cf)) "  слой " (slot ?f depth) crlf)
	  (if (or (eq (slot ?f rule) input) (member$ ?name ?seen) (> (length$ ?seen) 12)) then
		 (return))
	  (printout t ?indent "   правило " (slot ?f rule) " (cf " (fmt (slot ?f rcf)) ")" crlf)
	  (bind ?seen (create$ ?seen ?name))
	  (foreach ?p (explode$ (slot ?f from))
		 (explain ?p (str-cat ?indent "     ") ?seen))
	else
	  (printout t ?indent ?name "  — не выведен (cf ниже порога)" crlf)))

; Итог: сколько выведено, глубина, цели с объяснением.
(deffunction report ()
   (bind ?all (find-all-facts ((?f fact)) TRUE))
   (bind ?derived (create$))
   (bind ?inputs 0)
   (bind ?maxdepth 0)
   (foreach ?f ?all
	  (if (neq (slot ?f rule) input) then
		 (bind ?derived (create$ ?derived ?f))
		 (if (> (slot ?f depth) ?maxdepth) then (bind ?maxdepth (slot ?f depth)))
	   else
		 (bind ?inputs (+ ?inputs 1))))
   (printout t crlf
	  "================ ИТОГ ПРЯМОГО ВЫВОДА С CF ================" crlf
	  "Входных фактов: " ?inputs "   выведено: " (length$ ?derived) crlf
	  "Глубина вывода: " ?maxdepth " слоёв   порог cf: " (fmt ?*threshold*) crlf
	  "Отброшено порогом: " ?*rejected* " выводов правил (и всё, что из них следует)" crlf
	  "Предотвращено циклов: " ?*cycles* crlf)

   (bind ?d 1)
   (while (<= ?d ?maxdepth)
	  (printout t crlf "Слой " ?d ":" crlf)
	  (foreach ?f ?derived
		 (if (eq (slot ?f depth) ?d) then
			(printout t "  " (fmt (slot ?f cf)) "  " (slot ?f name) crlf)))
	  (bind ?d (+ ?d 1)))

   (printout t crlf "ЦЕЛИ:" crlf)
   (foreach ?g ?*goals*
	  (bind ?f (fact-address ?g))
	  (if (eq (type ?f) FACT-ADDRESS) then
		 (printout t "  [доказана] " ?g "  cf " (fmt (slot ?f cf)) crlf)
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


def parse_kb():
	facts, rules, goals = [], [], []
	section = None
	for raw in SRC.read_text(encoding="utf-8").splitlines():
		line = raw.strip()
		if not line or line.startswith("#"):
			continue
		if line.startswith("ФАКТЫ:"):
			section = "f"
			continue
		if line.startswith("ПРАВИЛА:"):
			section = "r"
			continue
		if line.startswith("ЦЕЛИ:"):
			section = "g"
			continue
		if line.startswith("ИМЯ:") or line.startswith("ОПИСАНИЕ:"):
			continue
		if section == "f":
			name = line.split(" - ")[0].strip()
			facts.append(name)
		elif section == "r":
			rules.append(line)
		elif section == "g":
			goals.append(line)
	return facts, rules, goals


def split_rule(line):
	body, _, comment = line.partition("#")
	left, _, right = body.partition("->")
	premises = [p.strip() for p in left.split(",") if p.strip()]
	conclusions = [c.strip() for c in right.split(",") if c.strip()]
	return premises, conclusions, comment.strip()


def conclusion_cf(name):
	for prefix, cf, _ in RULE_CF:
		if name.startswith(prefix):
			return cf
	return 0.80


def emit_rule(index, premises, conclusions, comment):
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
		out.append(f"   (derive {conclusion} {premise_cf} {conclusion_cf(conclusion):.2f} "
				   f"{label} \"{source}\" {depth})")
	out.append(")")
	return "\n".join(out)


def main():
	facts, rules, goals = parse_kb()

	cf_table = "\n".join(
		f";;    {prefix:<18} {cf:.2f}   {title}" for prefix, cf, title in RULE_CF)

	parts = [HEADER.format(facts=len(facts), rules=len(rules), cf_table=cf_table)]
	parts.append(MACHINERY.format(goals=" ".join(goals)))

	parts.append("""
; ============================================================================
;  2. ВХОДНЫЕ ФАКТЫ (cf из разведданных, слой 0)
; ============================================================================

(deffacts input-facts""")
	for name in facts:
		cf = FACT_CF.get(name, 1.0)
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

	parts.append(REPORT)

	DST.write_text("\n".join(parts), encoding="utf-8", newline="\n")
	print(f"{DST}: фактов {len(facts)}, правил {len(rules)}, целей {len(goals)}, "
		  f"строк {len('\n'.join(parts).splitlines())}")


if __name__ == "__main__":
	main()
