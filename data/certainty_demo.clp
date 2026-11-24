
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: УЧЕБНЫЙ МИНИ-ПРИМЕР
;;  КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ В ДИАПАЗОНЕ [0..1]
;;
;;  Зачем: показать механику cf на короткой цепочке (3 входных факта,
;;  8 правил, 2 цели) вместо полной базы certainty.clp
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


; ============================================================================
;  1. МАШИНЕРИЯ КОЭФФИЦИЕНТОВ УВЕРЕННОСТИ
; ============================================================================

; Порог: факты с меньшей уверенностью в базу не попадают.
; В этом мини-примере поднимите порог до 0.35 — и обе цели перестанут
; доказываться: цепочка обрывается на последнем звене.
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
(defglobal ?*goals* = (create$ recommended_Knockdown_anti_poke recommended_MetalSkin_anti_channel))

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


; ============================================================================
;  2. ВХОДНЫЕ ФАКТЫ (слой 0)
; ============================================================================

(deffacts input-facts
   ; разведка: враг — Vindicta
   (fact (name hero_Vindicta) (cf 0.90) (depth 0) (rule input))
   ; Knockdown — контр-предмет от poke
   (fact (name item_provides_Knockdown_anti_poke) (cf 0.95) (depth 0) (rule input))
   ; MetalSkin — контр-предмет от channel
   (fact (name item_provides_MetalSkin_anti_channel) (cf 0.90) (depth 0) (rule input))
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
