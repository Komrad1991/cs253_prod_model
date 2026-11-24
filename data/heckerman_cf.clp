;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  УЧЕБНЫЙ ПРИМЕР: варианты объединения коэффициентов уверенности
;;
;;  По статье: David Heckerman. The Certainty-Factor Model.
;;  В сб. G. Shafer, J. Pearl (eds.), Readings in Uncertain Reasoning,
;;  Morgan Kaufmann, 1990.
;;
;;  Статья разбирает учебную задачу Шерлока Холмса:
;;    R1: if WATSON'S CALL then ALARM     CF 0.5
;;    R2: if GIBBON'S CALL then ALARM     CF 0.9
;;    R3: if ALARM         then BURGLARY  CF 0.99
;;  и показывает, что весь вывод держится на трёх функциях:
;;
;;    (1) параллельное объединение — два правила об одной гипотезе:
;;        cf1 + cf2 - cf1*cf2        при cf1, cf2 >= 0
;;        cf1 + cf2 + cf1*cf2        при cf1, cf2 <  0
;;        (cf1 + cf2) / (1 - min(|cf1|,|cf2|))   иначе
;;    (2) последовательное объединение — гипотеза одного правила служит
;;        посылкой другого: cf = cf правила * cf посылки, но при cf посылки <= 0
;;        результат 0;
;;    (3) конъюнкция и дизъюнкция посылок: min и max.
;;
;;  Числа статьи: 0.5 (+) 0.9 = 0.95;  0.99 * 0.95 = 0.9405;
;;                0.9 * min(0.7, 0.9) = 0.63.
;;
;;  Эта программа сначала воспроизводит эти числа настоящим прямым выводом,
;;  а затем печатает таблицу: ЧЕМ каждую функцию можно заменить и к чему это
;;  приводит. Замены сгруппированы по трём defglobal ниже.
;;
;;  КАК ПЕРЕКЛЮЧАТЬ ВАРИАНТ:
;;    - в приложении (режим CLIPS) — список формул в шапке: выбор сразу
;;      перезапускает вывод с новым режимом;
;;    - вручную — поменять ?*parallel* / ?*serial* / ?*and-mode* ниже
;;      и нажать «Рестарт», затем «Дальше»;
;;    - из командной строки — (bind ?*parallel* linear) после (reset).
;;  Полная таблица сравнения печатается при любом выборе.
;;
;;  ┌ параллельное объединение ────────────────────────────────────────────┐
;;  │ standard   уравнение (1), как в статье                               │
;;  │ lambda     уравнения (4)-(6): cf -> отношение шансов λ, λ1*λ2, назад │
;;  │ linear     cf1 + cf2 с обрезкой до [-1..1] — «наивное» сложение       │
;;  │ strongest  побеждает более сильное свидетельство, слабое теряется     │
;;  └──────────────────────────────────────────────────────────────────────┘
;;  ┌ последовательное объединение ────────────────────────────────────────┐
;;  │ clamp      уравнение (2): при cf посылки <= 0 вывод 0                │
;;  │ open       без отсечки: минус проходит в заключение (так считают      │
;;  │            certainty.clp и certainty_signed.clp в этом проекте)       │
;;  │ deadzone   вариант MYCIN: cf посылки < 0.2 считается нулём            │
;;  └──────────────────────────────────────────────────────────────────────┘
;;  ┌ конъюнкция посылок ──────────────────────────────────────────────────┐
;;  │ min        уравнение (3), как в статье                                │
;;  │ product    перемножение: каждая посылка «штрафует» вывод             │
;;  └──────────────────────────────────────────────────────────────────────┘
;;
;;  ЗАПУСК:  clips -f data/heckerman_cf.clp
;;           либо выбрать пример heckerman_cf.clp в режиме CLIPS приложения.
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;


; ============================================================================
;  1. РЕЖИМЫ ОБЪЕДИНЕНИЯ (что именно демонстрируем)
; ============================================================================

; Два правила об одной гипотезе: standard | lambda | linear | strongest.
(defglobal ?*parallel* = standard)

; Правило над правилом: clamp | open | deadzone.
(defglobal ?*serial* = clamp)

; Конъюнкция посылок: min | product.
(defglobal ?*and-mode* = min)

; Порог: вывод с |cf| ниже порога в базу не попадает.
(defglobal ?*threshold* = 0.05)

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

; Цели: для них печатается итог и цепочка объяснения.
(defglobal ?*goals* = (create$ burglary heart_attack))


; ============================================================================
;  2. МАШИНЕРИЯ: шаблон факта и служебные функции
; ============================================================================

(deftemplate fact
   (slot name (type SYMBOL))
   (slot cf (type FLOAT) (default 1.0))
   (slot depth (type INTEGER) (default 0))
   (slot rule (type SYMBOL) (default input))
   (slot rcf (type FLOAT) (default 1.0))
   (slot from (type STRING) (default ""))
   ; anc — все факты, от которых этот вывод зависит (защита от циклов)
   (slot anc (type STRING) (default "")))

; Красивый вывод числа. Четыре знака — чтобы было видно 0.9405 из статьи.
(deffunction fmt (?x)
   (format nil "%.4f" ?x))

; Строка таблицы: число слева, подпись справа. Числа — ASCII, поэтому колонка
; всегда выровнена (format в CLIPS считает ширину в байтах и кириллицу ломает).
(deffunction row (?label ?value)
   (printout t "   " (fmt ?value) "  " ?label crlf))


; ----------------------------------------------------------------------------
;  2.1. ПАРАЛЛЕЛЬНОЕ ОБЪЕДИНЕНИЕ: варианты
; ----------------------------------------------------------------------------

; Уравнение (1) статьи — стандарт.
(deffunction cf-par (?a ?b)
   (if (and (> ?a 0.0) (> ?b 0.0)) then
	  (+ ?a (* ?b (- 1.0 ?a)))
	else
	  (if (and (< ?a 0.0) (< ?b 0.0)) then
		 (+ ?a (* ?b (+ 1.0 ?a)))
	   else
		 (if (or (eq ?a 0.0) (eq ?b 0.0)) then
			(+ ?a ?b)
		   else
			 (bind ?den (- 1.0 (min (abs ?a) (abs ?b))))
			 (if (> ?den 0.0) then (/ (+ ?a ?b) ?den) else 0.0)))))

; cf -> отношение шансов λ (уравнение 6): λ = 1/(1-cf) при cf >= 0, иначе 1+cf.
(deffunction cf-to-lambda (?cf)
   (if (>= ?cf 1.0) then 1000000000.0
	else
	  (if (>= ?cf 0.0) then (/ 1.0 (- 1.0 ?cf))
	   else (+ 1.0 ?cf))))

; λ -> cf (уравнение 6 в обратную сторону).
(deffunction lambda-to-cf (?l)
   (if (>= ?l 1.0) then (/ (- ?l 1.0) ?l) else (- ?l 1.0)))

; Уравнения (4)-(6): шансы перемножаются, как требует теорема Байеса.
; Результат совпадает с уравнением (1) — это одна и та же операция.
(deffunction cf-par-lambda (?a ?b)
   (lambda-to-cf (* (cf-to-lambda ?a) (cf-to-lambda ?b))))

; Наивное сложение с обрезкой: переоценивает, быстро упирается в 1.
(deffunction cf-par-linear (?a ?b)
   (bind ?sum (+ ?a ?b))
   (if (> ?sum 1.0) then (bind ?sum 1.0))
   (if (< ?sum -1.0) then (bind ?sum -1.0))
   ?sum)

; Побеждает более сильное свидетельство: слабое не добавляет ничего.
(deffunction cf-par-strongest (?a ?b)
   (if (>= (abs ?a) (abs ?b)) then ?a else ?b))


; ----------------------------------------------------------------------------
;  2.2. ПОСЛЕДОВАТЕЛЬНОЕ ОБЪЕДИНЕНИЕ: варианты
; ----------------------------------------------------------------------------

; Уравнение (2) статьи: отрицательная посылка не даёт заключения.
(deffunction cf-ser (?premise ?rule)
   (if (> ?premise 0.0) then (* ?premise ?rule) else 0.0))

; Без отсечки: cf правила * cf посылки при любом знаке посылки.
(deffunction cf-ser-open (?premise ?rule)
   (* ?premise ?rule))

; Вариант MYCIN: cf посылки меньше 0.2 считается нулём (экономия вычислений).
(deffunction cf-ser-deadzone (?premise ?rule)
   (if (>= ?premise 0.2) then (* ?premise ?rule) else 0.0))


; ----------------------------------------------------------------------------
;  2.3. КОНЪЮНКЦИЯ И ДИЗЪЮНКЦИЯ ПОСЫЛОК: варианты
; ----------------------------------------------------------------------------

; Уравнение (3): min для конъюнкции (product — более строгий вариант).
(deffunction cf-and (?a ?b)
   (if (eq ?*and-mode* product) then (* ?a ?b) else (min ?a ?b)))

; max для дизъюнкции (cf-or-sum — вероятностная сумма).
(deffunction cf-or-sum (?a ?b)
   (+ ?a (* ?b (- 1.0 ?a))))


; ----------------------------------------------------------------------------
;  2.4. Действующие функции: выбираются режимами из раздела 1
; ----------------------------------------------------------------------------

; Объединение двух оценок одного и того же факта.
(deffunction cf-combine (?a ?b)
   (if (eq ?*parallel* lambda) then
	  (cf-par-lambda ?a ?b)
	else
	  (if (eq ?*parallel* linear) then
		 (cf-par-linear ?a ?b)
	   else
		 (if (eq ?*parallel* strongest) then
			(cf-par-strongest ?a ?b)
		  else
			(cf-par ?a ?b)))))

; Передача уверенности по правилу.
(deffunction cf-serial (?premise ?rule)
   (if (eq ?*serial* open) then
	  (cf-ser-open ?premise ?rule)
	else
	  (if (eq ?*serial* deadzone) then
		 (cf-ser-deadzone ?premise ?rule)
	   else
		 (cf-ser ?premise ?rule))))


; ----------------------------------------------------------------------------
;  2.5. Работа с базой фактов
; ----------------------------------------------------------------------------

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
(deffunction ancestors (?from)
   (bind ?set (create$))
   (foreach ?p (explode$ ?from)
	  (if (not (member$ ?p ?set)) then (bind ?set (create$ ?set ?p)))
	  (bind ?f (fact-address ?p))
	  (if (eq (type ?f) FACT-ADDRESS) then
		 (foreach ?a (explode$ (slot ?f anc))
			(if (not (member$ ?a ?set)) then (bind ?set (create$ ?set ?a))))))
   ?set)

; Итоговая уверенность факта: свёртка вкладов всех его правил.
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
;   ?cf   — уверенность посылок (min или max по посылкам)
;   ?rcf  — cf самого правила
(deffunction derive (?name ?cf ?rcf ?rule ?from ?depth)
   (bind ?value (cf-serial ?cf ?rcf))
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
;  3. ВХОДНЫЕ ФАКТЫ И ПРАВИЛА ИЗ СТАТЬИ
; ============================================================================

(deffacts input-facts
   ; Звонки соседей: события достоверны, неуверенность — в правилах R1 и R2.
   (fact (name watson_call) (cf 1.00) (depth 0) (rule input))
   (fact (name gibbon_call) (cf 1.00) (depth 0) (rule input))
   ; Пациент: гримаса и хватается за горло (пример уравнения 3).
   (fact (name patient_grimaces) (cf 1.00) (depth 0) (rule input))
   (fact (name patient_clutches_throat) (cf 1.00) (depth 0) (rule input))
   ; Радионовости: раскомментируйте строку ниже, чтобы включить сценарий
   ; землетрясения из раздела 4.1 статьи (R11, R12 и компенсирующее R13).
   ; (fact (name radio_newscast) (cf 1.00) (depth 0) (rule input))
)

; R1: if WATSON'S CALL then ALARM CF 0.5
(defrule r001
   (fact (name watson_call) (cf ?c1) (depth ?d1))
   =>
   (derive alarm ?c1 0.50 r001 "watson_call" (+ 1 ?d1)))

; R2: if GIBBON'S CALL then ALARM CF 0.9
(defrule r002
   (fact (name gibbon_call) (cf ?c1) (depth ?d1))
   =>
   (derive alarm ?c1 0.90 r002 "gibbon_call" (+ 1 ?d1)))

; R3: if ALARM then BURGLARY CF 0.99
(defrule r003
   (fact (name alarm) (cf ?c1) (depth ?d1))
   =>
   (derive burglary ?c1 0.99 r003 "alarm" (+ 1 ?d1)))

; R7: if PATIENT GRIMACES then CHEST PAIN CF 0.7
(defrule r007
   (fact (name patient_grimaces) (cf ?c1) (depth ?d1))
   =>
   (derive chest_pain ?c1 0.70 r007 "patient_grimaces" (+ 1 ?d1)))

; R8: if PATIENT CLUTCHES THROAT then SHORTNESS OF BREATH CF 0.9
(defrule r008
   (fact (name patient_clutches_throat) (cf ?c1) (depth ?d1))
   =>
   (derive shortness_of_breath ?c1 0.90 r008 "patient_clutches_throat" (+ 1 ?d1)))

; R9: if CHEST PAIN and SHORTNESS OF BREATH then HEART ATTACK CF 0.9
(defrule r009
   (fact (name chest_pain) (cf ?c1) (depth ?d1))
   (fact (name shortness_of_breath) (cf ?c2) (depth ?d2))
   =>
   (derive heart_attack (cf-and ?c1 ?c2) 0.90 r009
		   "chest_pain shortness_of_breath" (+ 1 (max ?d1 ?d2))))

; R11: if RADIO NEWSCAST then EARTHQUAKE CF 0.9998
(defrule r011
   (fact (name radio_newscast) (cf ?c1) (depth ?d1))
   =>
   (derive earthquake ?c1 0.9998 r011 "radio_newscast" (+ 1 ?d1)))

; R12: if EARTHQUAKE then ALARM CF 0.96
(defrule r012
   (fact (name earthquake) (cf ?c1) (depth ?d1))
   =>
   (derive alarm ?c1 0.96 r012 "earthquake" (+ 1 ?d1)))

; R13: if EARTHQUAKE then BURGLARY CF -0.5 — «компенсирующее» правило,
; которым статья пытается починить сеть (раздел 4.1).
(defrule r013
   (fact (name earthquake) (cf ?c1) (depth ?d1))
   =>
   (derive burglary ?c1 -0.50 r013 "earthquake" (+ 1 ?d1)))


; ============================================================================
;  4. ОТЧЁТ: итог вывода и сравнение вариантов объединения
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
	  (printout t ?indent ?name "  — не выведен (|cf| ниже порога)" crlf)))

; 1. Параллельное объединение: два правила об одной гипотезе.
(deffunction compare-parallel ()
   (bind ?a 0.5)
   (bind ?b 0.9)
   (printout t crlf
	  "1. ПАРАЛЛЕЛЬНОЕ ОБЪЕДИНЕНИЕ: R1 и R2 дают одну гипотезу ALARM" crlf
	  "   cf R1 = " (fmt ?a) ", cf R2 = " (fmt ?b) crlf)
   (row "standard, уравнение (1)" (cf-par ?a ?b))
   (row "lambda, уравнения (4)-(6)" (cf-par-lambda ?a ?b))
   (row "linear, cf1 + cf2" (cf-par-linear ?a ?b))
   (row "strongest, только сильнейшее" (cf-par-strongest ?a ?b))
   (printout t "   lambda: " (fmt (cf-to-lambda ?a)) " * " (fmt (cf-to-lambda ?b))
			 " = " (fmt (* (cf-to-lambda ?a) (cf-to-lambda ?b)))
			 " -> обратно в cf" crlf
			 "   Уравнение (1) и пересчёт через lambda дают одно и то же: это одна" crlf
			 "   операция, просто записанная через отношения шансов." crlf
			 "   linear переоценивает (0.5+0.9 = 1.4 -> обрезка до 1.000), strongest" crlf
			 "   недооценивает: второй звонок ничего не добавляет." crlf))

; 2. Последовательное объединение: cf правила * cf посылки.
(deffunction compare-serial ()
   (printout t crlf
	  "2. ПОСЛЕДОВАТЕЛЬНОЕ ОБЪЕДИНЕНИЕ: ALARM -> BURGLARY" crlf
	  "   cf правила R3 = 0.990, cf посылки = 0.950" crlf)
   (row "clamp, уравнение (2)" (cf-ser 0.95 0.99))
   (row "open, без отсечки" (cf-ser-open 0.95 0.99))
   (printout t "   отрицательная посылка: cf правила 0.800, cf посылки -0.500" crlf)
   (row "clamp, уравнение (2)" (cf-ser -0.5 0.8))
   (row "open, без отсечки" (cf-ser-open -0.5 0.8))
   (printout t "   мёртвая зона MYCIN: cf правила 0.990, cf посылки 0.150" crlf)
   (row "deadzone, порог 0.2" (cf-ser-deadzone 0.15 0.99))
   (row "clamp, без мёртвой зоны" (cf-ser 0.15 0.99))
   (printout t "   Отсечка в уравнении (2) — это осторожность: нет уверенности в" crlf
			 "   посылке, нет и заключения. Вариант open пропускает минус дальше," crlf
			 "   и тогда одно сомнительное свидетельство опровергает всё, что из" crlf
			 "   него выведено." crlf))

; 3. Конъюнкция и дизъюнкция посылок.
(deffunction compare-and ()
   (printout t crlf
	  "3. КОНЪЮНКЦИЯ И ДИЗЪЮНКЦИЯ ПОСЫЛОК" crlf
	  "   R9: CHEST PAIN 0.700 и SHORTNESS OF BREATH 0.900 -> HEART ATTACK, cf 0.900" crlf)
   (row "конъюнкция: min, уравнение (3)" (cf-ser (min 0.7 0.9) 0.9))
   (row "конъюнкция: product" (cf-ser (* 0.7 0.9) 0.9))
   (printout t "   дизъюнкция (два независимых пути к одной посылке):" crlf)
   (row "дизъюнкция: max, уравнение (3)" (cf-ser (max 0.7 0.9) 0.9))
   (row "дизъюнкция: вероятностная сумма" (cf-ser (cf-or-sum 0.7 0.9) 0.9))
   (printout t "   min берёт самую слабую посылку — конъюнкция не может быть прочнее" crlf
			 "   своего слабейшего звена. product штрафует за каждую посылку сразу." crlf))

; 4. Несколько причин одного следствия (раздел 4.1 статьи).
(deffunction compare-multiple-causes ()
   (bind ?calls (cf-par 0.5 0.9))
   (bind ?from-news (cf-ser 0.9998 0.96))
   (bind ?alarm-both (cf-par ?calls ?from-news))
   (bind ?burg-calls (cf-ser ?calls 0.99))
   (bind ?burg-both (cf-ser ?alarm-both 0.99))
   (bind ?r13 (cf-ser 0.9998 -0.5))
   (bind ?burg-both-r13 (cf-par ?burg-both ?r13))
   (bind ?burg-news (cf-ser ?from-news 0.99))
   (bind ?burg-news-r13 (cf-par ?burg-news ?r13))
   (printout t crlf
	  "4. НЕСКОЛЬКО ПРИЧИН ОДНОГО СЛЕДСТВИЯ (раздел 4.1 статьи)" crlf
	  "   Землетрясение тоже включает тревогу (R11, R12). По смыслу вера в" crlf
	  "   кражу должна УПАСТЬ: тревогу объясняет землетрясение, а не кража." crlf)
   (row "тревога: только звонки" ?calls)
   (row "тревога: звонки + новости" ?alarm-both)
   (row "кража: только звонки" ?burg-calls)
   (row "кража: звонки + новости" ?burg-both)
   (row "кража: звонки + новости + R13" ?burg-both-r13)
   (printout t "   Вера в кражу не падает, а растёт: модель складывает свидетельства," crlf
			 "   не замечая, что они объясняют друг друга. Компенсирующее правило R13" crlf
			 "   отчасти возвращает прежнюю оценку, но нарушает локальность —" crlf
			 "   оно срабатывает и без единого звонка:" crlf)
   (row "кража: только новости" ?burg-news)
   (row "кража: только новости + R13" ?burg-news-r13)
   (printout t "   Ни одного звонка не было, а вера в кражу всё равно снизилась." crlf))

; 5. Коррелированные свидетельства (раздел 4.2 статьи).
(deffunction compare-correlated ()
   (bind ?one 0.6)
   (bind ?two (cf-par ?one ?one))
   (bind ?three (cf-par ?two ?one))
   (printout t crlf
	  "5. КОРРЕЛИРОВАННЫЕ СВИДЕТЕЛЬСТВА (раздел 4.2 статьи)" crlf
	  "   Три сообщения о катастрофе, каждое cf 0.600." crlf)
   (row "два сообщения" ?two)
   (row "три сообщения" ?three)
   (printout t "   Модель не спрашивает, независимы ли источники: три перепечатки" crlf
			 "   одного сообщения дают тот же 0.936, что три независимых свидетеля." crlf
			 "   Если источник один, честная оценка — 0.600." crlf))

; 6. Переключение режимов: тот же расчёт, но через функции самого вывода.
(deffunction compare-modes ()
   (bind ?saved-p ?*parallel*)
   (bind ?saved-s ?*serial*)
   (printout t crlf
	  "6. ПЕРЕКЛЮЧЕНИЕ РЕЖИМОВ: тот же вывод через cf-combine / cf-serial" crlf
	  "   Режим задают ?*parallel* и ?*serial* в начале файла; в приложении" crlf
	  "   его выбирают списком формул в шапке. Здесь режимы перебираются" crlf
	  "   вручную, чтобы было видно, как меняется результат." crlf
	  "   Цепочка: watson_call 0.500 (+) gibbon_call 0.900 -> ALARM -> BURGLARY" crlf)
   (foreach ?m (create$ standard lambda linear strongest)
	  (bind ?*parallel* ?m)
	  (bind ?alarm (cf-combine 0.5 0.9))
	  (row (str-cat "parallel=" ?m "  (ALARM " (fmt ?alarm) ")") (cf-serial ?alarm 0.99)))
   (bind ?*parallel* ?saved-p)
   (printout t "   Посылка под сомнением: ALARM 0.150, cf правила 0.990." crlf)
   (foreach ?m (create$ clamp open deadzone)
	  (bind ?*serial* ?m)
	  (row (str-cat "serial=" ?m) (cf-serial 0.15 0.99)))
   (printout t "   Отрицательная посылка: ALARM -0.500, cf правила 0.800." crlf)
   (foreach ?m (create$ clamp open deadzone)
	  (bind ?*serial* ?m)
	  (row (str-cat "serial=" ?m) (cf-serial -0.5 0.8)))
   (bind ?*serial* ?saved-s)
   (printout t "   Сейчас в файле: parallel=" ?*parallel* "  serial=" ?*serial* crlf))

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
	  "======== ПРИМЕР ИЗ СТАТЬИ ХЕКЕРМАНА: ПРЯМОЙ ВЫВОД С CF ========" crlf
	  "Входных фактов: " ?inputs "   выведено: " (length$ ?derived) crlf
	  "Глубина вывода: " ?maxdepth " слоёв   порог |cf|: " (fmt ?*threshold*) crlf
	  "Режимы: parallel=" ?*parallel* "  serial=" ?*serial* "  and=" ?*and-mode* crlf
	  "Отброшено порогом: " ?*rejected* "   предотвращено циклов: " ?*cycles* crlf)

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

   (printout t crlf
	  "=========== ЧЕМ МОЖНО ЗАМЕНИТЬ ОБЪЕДИНЕНИЕ CF (по статье) ===========" crlf)
   (compare-parallel)
   (compare-serial)
   (compare-and)
   (compare-multiple-causes)
   (compare-correlated)
   (compare-modes)
   (printout t crlf "=========================================================" crlf)

   ; Машиночитаемый слепок вывода для блок-схемы в приложении.
   ; N|слой|cf|имя|правило|cf правила|посылки
   ; G|цель|cf
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
