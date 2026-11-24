
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: УЧЕБНЫЙ МИНИ-ПРИМЕР
;;  КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ В ДИАПАЗОНЕ [-1..1]
;;
;;  Та же цепочка, что в certainty_demo.clp, но cf может быть отрицательным:
;;  -1 — «определённо против», 0 — «ничего не известно», +1 — «определённо за».
;;  (6 входных фактов, 11 правил, 3 цели)
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
(defglobal ?*goals* = (create$ recommended_Knockdown_anti_poke recommended_MetalSkin_anti_channel recommended_EtherealShift_anti_execute))

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


; ============================================================================
;  2. ВХОДНЫЕ ФАКТЫ (слой 0)
; ============================================================================

(deffacts input-facts
   ; разведка: Vindicta, скорее, не в матче
   (fact (name hero_Vindicta) (cf -0.40) (depth 0) (rule input))
   ; Knockdown — контр-предмет от poke
   (fact (name item_provides_Knockdown_anti_poke) (cf 0.95) (depth 0) (rule input))
   ; MetalSkin — контр-предмет от channel
   (fact (name item_provides_MetalSkin_anti_channel) (cf 0.90) (depth 0) (rule input))
   ; союзник подтверждает Knockdown
   (fact (name ally_reports_Knockdown_good) (cf 0.80) (depth 0) (rule input))
   ; патч ослабил MetalSkin
   (fact (name patch_nerfed_MetalSkin) (cf 1.00) (depth 0) (rule input))
   ; у врага есть disable
   (fact (name enemy_has_item_disable) (cf 0.90) (depth 0) (rule input))
)

; ============================================================================
;  3. ПРАВИЛА (прямой вывод)
; ============================================================================

; Vindicta угрожает poke
;   hero_Vindicta -> threat_poke
(defrule r001
   (fact (name hero_Vindicta) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r001 "hero_Vindicta" (+ 1 ?d1))
)

; нужен контр-предмет от poke
;   threat_poke -> need_anti_poke
(defrule r002
   (fact (name threat_poke) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_poke ?c1 0.85 r002 "threat_poke" (+ 1 ?d1))
)

; Knockdown подходит под потребность
;   need_anti_poke, item_provides_Knockdown_anti_poke -> candidate_Knockdown_anti_poke
(defrule r003
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Knockdown_anti_poke) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Knockdown_anti_poke (min ?c1 ?c2) 0.80 r003 "need_anti_poke item_provides_Knockdown_anti_poke" (+ 1 (max ?d1 ?d2)))
)

; выбираем Knockdown
;   candidate_Knockdown_anti_poke -> pick_Knockdown_anti_poke
(defrule r004
   (fact (name candidate_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Knockdown_anti_poke ?c1 0.80 r004 "candidate_Knockdown_anti_poke" (+ 1 ?d1))
)

; итоговая рекомендация
;   pick_Knockdown_anti_poke -> recommended_Knockdown_anti_poke
(defrule r005
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Knockdown_anti_poke ?c1 0.75 r005 "pick_Knockdown_anti_poke" (+ 1 ?d1))
)

; MetalSkin подходит под потребность
;   need_anti_poke, item_provides_MetalSkin_anti_channel -> candidate_MetalSkin_anti_channel
(defrule r006
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_MetalSkin_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MetalSkin_anti_channel (min ?c1 ?c2) 0.75 r006 "need_anti_poke item_provides_MetalSkin_anti_channel" (+ 1 (max ?d1 ?d2)))
)

; выбираем MetalSkin
;   candidate_MetalSkin_anti_channel -> pick_MetalSkin_anti_channel
(defrule r007
   (fact (name candidate_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_MetalSkin_anti_channel ?c1 0.75 r007 "candidate_MetalSkin_anti_channel" (+ 1 ?d1))
)

; итоговая рекомендация
;   pick_MetalSkin_anti_channel -> recommended_MetalSkin_anti_channel
(defrule r008
   (fact (name pick_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MetalSkin_anti_channel ?c1 0.75 r008 "pick_MetalSkin_anti_channel" (+ 1 ?d1))
)

; патч ослабил MetalSkin — рекомендация отменяется
;   patch_nerfed_MetalSkin -> recommended_MetalSkin_anti_channel
(defrule r009
   (fact (name patch_nerfed_MetalSkin) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MetalSkin_anti_channel ?c1 -0.60 r009 "patch_nerfed_MetalSkin" (+ 1 ?d1))
)

; у врага есть disable — EtherealShift не спасает
;   enemy_has_item_disable -> recommended_EtherealShift_anti_execute
(defrule r010
   (fact (name enemy_has_item_disable) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_EtherealShift_anti_execute ?c1 -0.95 r010 "enemy_has_item_disable" (+ 1 ?d1))
)

; союзник подтверждает Knockdown
;   ally_reports_Knockdown_good -> pick_Knockdown_anti_poke
(defrule r011
   (fact (name ally_reports_Knockdown_good) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Knockdown_anti_poke ?c1 0.70 r011 "ally_reports_Knockdown_good" (+ 1 ?d1))
)


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
