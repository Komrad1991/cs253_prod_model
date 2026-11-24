;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;
;;  Deadlock: контр-предметы — КОЭФФИЦИЕНТЫ УВЕРЕННОСТИ (CF)
;;  Схема вывода: только ПРЯМОЙ вывод. Обратный вывод не рассматривается.
;;
;;  Основа — та же база знаний, что в C#-версии (data/deadlock_kb.txt):
;;  121 входных фактов и 431 правил. Файл сгенерирован из неё
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
;;    expensive_pick_    0.95   свойства предмета: дорогой
;;    cheap_pick_        0.95   свойства предмета: дешёвый
;;    mid_pick_          0.95   свойства предмета: средний по цене
;;    active_pick_       0.95   свойства предмета: активный
;;    threat_            0.90   угроза героя
;;    need_              0.85   потребность в контр-предмете
;;    candidate_         0.80   кандидат под потребность
;;    pick_              0.80   выбор контр-предмета
;;    recommended_       0.75   итоговая рекомендация
;;    second_pick_       0.70   второй контр-предмет
;;    warning_           0.65   предупреждение о связке
;;    enemy_buys_        0.60   гипотеза: что купит враг
;;    enemy_answers_     0.60   гипотеза: чем враг ответит
;;    i_answers_         0.55   наш ответ на ответ врага
;;
;;  ЗАПУСК:  clips -f data/certainty.clp
;;           либо кнопка «Дальше» в режиме CLIPS приложения (файл уже в data).
;;  Нужна последовательность срабатываний? Добавьте в начало файла
;;  (watch rules) — это штатная трассировка самого CLIPS.
;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;;


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
(defglobal ?*goals* = (create$ i_answers_Knockdown second_pick_Unstoppable_anti_silence second_pick_Inhibitor_anti_regen second_pick_Curse_anti_immunity second_pick_Curse_anti_purge second_pick_DivinersKevlar_anti_disable recommended_MetalSkin_anti_channel recommended_Unstoppable_anti_cc recommended_Curse_anti_channel))

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
;  2. ВХОДНЫЕ ФАКТЫ (cf из разведданных, слой 0)
; ============================================================================

(deffacts input-facts
   (fact (name hero_Haze) (cf 1.00) (depth 0) (rule input))
   (fact (name hero_Seven) (cf 1.00) (depth 0) (rule input))
   (fact (name hero_MoKrill) (cf 1.00) (depth 0) (rule input))
   (fact (name hero_Shiv) (cf 0.85) (depth 0) (rule input))
   (fact (name hero_Vindicta) (cf 0.75) (depth 0) (rule input))
   (fact (name item_provides_MysticShot_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Berserker_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ToxicBullets_anti_heal) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ToxicBullets_anti_tank) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Headhunter_anti_poke) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Headhunter_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_EscalatingResilience_anti_bullet) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SlowingHex_anti_mobility) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SlowingHex_anti_dive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Sharpshooter_anti_poke) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Sharpshooter_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Inhibitor_anti_heal) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Inhibitor_anti_mobility) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Inhibitor_anti_regen) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_CripplingHeadshot_anti_tank) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_HuntersAura_anti_tank) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_HuntersAura_anti_bullet) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Silencer_anti_channel) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Silencer_anti_mobility) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SpiritualOverflow_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ExtraStamina_anti_dive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_HealingRite_sustain_self) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_BulletArmor_anti_bullet) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SpiritArmor_anti_magic) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DebuffReducer_anti_dot) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ReactiveBarrier_anti_cc) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ReactiveBarrier_anti_magic) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DivineBarrier_anti_magic) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DivineBarrier_anti_execute) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_MetalSkin_anti_bullet) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_MetalSkin_anti_channel) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_ReturnFire_anti_bullet) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SuperiorStamina_anti_dive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Fortitude_anti_poke) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Fortitude_sustain_self) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DebuffRemover_anti_dot) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DebuffRemover_anti_cc) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Colossus_anti_dive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Colossus_sustain_self) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SoulRebirth_anti_execute) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DivinersKevlar_anti_magic) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DivinersKevlar_anti_cc) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_DivinersKevlar_anti_disable) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Leech_sustain_self) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_MysticBurst_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Knockdown_anti_channel) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Knockdown_anti_poke) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_EtherealShift_anti_execute) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_EtherealShift_anti_zone) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_WarpStone_anti_dive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_WarpStone_anti_zone) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SilenceGlyph_anti_channel) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SilenceGlyph_anti_mobility) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_SilenceGlyph_anti_invis) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_EscalatingExposure_anti_tank) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Curse_anti_channel) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Curse_anti_mobility) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Curse_anti_heal) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Curse_anti_immunity) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Curse_anti_purge) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Unstoppable_anti_cc) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_Unstoppable_anti_silence) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_BoundlessSpirit_damage_core) (cf 1.00) (depth 0) (rule input))
   (fact (name item_provides_BoundlessSpirit_sustain_self) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_MysticShot_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Berserker_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_ToxicBullets_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Headhunter_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_EscalatingResilience_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SlowingHex_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Sharpshooter_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Inhibitor_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_CripplingHeadshot_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_HuntersAura_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Silencer_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SpiritualOverflow_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_ExtraStamina_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_HealingRite_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_BulletArmor_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SpiritArmor_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_DebuffReducer_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_ReactiveBarrier_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_DivineBarrier_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_MetalSkin_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_ReturnFire_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SuperiorStamina_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Fortitude_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_DebuffRemover_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Colossus_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SoulRebirth_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_DivinersKevlar_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Leech_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_MysticBurst_cheap) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Knockdown_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_EtherealShift_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_WarpStone_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_SilenceGlyph_mid) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_EscalatingExposure_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Curse_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_Unstoppable_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_tier_BoundlessSpirit_expensive) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_SlowingHex) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_Silencer) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_HealingRite) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_ReactiveBarrier) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_DivineBarrier) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_MetalSkin) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_ReturnFire) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_DebuffRemover) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_Colossus) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_Knockdown) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_EtherealShift) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_WarpStone) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_SilenceGlyph) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_Curse) (cf 1.00) (depth 0) (rule input))
   (fact (name item_active_Unstoppable) (cf 1.00) (depth 0) (rule input))
)

; ============================================================================
;  2. ПРАВИЛА (прямой вывод; cf правила — по слою заключения)
; ============================================================================

; Abrams играет от передней линии: Infernal Resilience возвращает часть урона в здоровье.
;   hero_Abrams -> threat_tank_frontline
(defrule r001
   (fact (name hero_Abrams) (cf ?c1) (depth ?d1))
   =>
   (derive threat_tank_frontline ?c1 0.90 r001 "hero_Abrams" (+ 1 ?d1))
)

; Shoulder Charge врывается в строй и разбрасывает его.
;   hero_Abrams -> threat_dive
(defrule r002
   (fact (name hero_Abrams) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r002 "hero_Abrams" (+ 1 ?d1))
)

; Таран и Seismic Impact — жёсткий контроль зоны.
;   hero_Abrams -> threat_hard_cc
(defrule r003
   (fact (name hero_Abrams) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r003 "hero_Abrams" (+ 1 ?d1))
)

; Hook вытаскивает цель из позиции — жёсткий контроль на дистанции.
;   hero_Bebop -> threat_hard_cc
(defrule r004
   (fact (name hero_Bebop) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r004 "hero_Bebop" (+ 1 ?d1))
)

; Hyper Beam и Sticky Bomb дают магический берст.
;   hero_Bebop -> threat_burst_magic
(defrule r005
   (fact (name hero_Bebop) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r005 "hero_Bebop" (+ 1 ?d1))
)

; Держит дистанцию и наказывает за выход вперёд.
;   hero_Bebop -> threat_poke
(defrule r006
   (fact (name hero_Bebop) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r006 "hero_Bebop" (+ 1 ?d1))
)

; Singularity собирает команду в одну точку — канальный AoE-ультимейт.
;   hero_Dynamo -> threat_aoe_channel
(defrule r007
   (fact (name hero_Dynamo) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r007 "hero_Dynamo" (+ 1 ?d1))
)

; Kinetic Pulse и Singularity держат цель.
;   hero_Dynamo -> threat_hard_cc
(defrule r008
   (fact (name hero_Dynamo) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r008 "hero_Dynamo" (+ 1 ?d1))
)

; Dynamo играет впереди и имеет большой запас здоровья.
;   hero_Dynamo -> threat_tank_frontline
(defrule r009
   (fact (name hero_Dynamo) (cf ?c1) (depth ?d1))
   =>
   (derive threat_tank_frontline ?c1 0.90 r009 "hero_Dynamo" (+ 1 ?d1))
)

; Charged Shot и Rain of Arrows наказывают с дистанции.
;   hero_GreyTalon -> threat_poke
(defrule r010
   (fact (name hero_GreyTalon) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r010 "hero_GreyTalon" (+ 1 ?d1))
)

; Большой магический залп по площади.
;   hero_GreyTalon -> threat_burst_magic
(defrule r011
   (fact (name hero_GreyTalon) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r011 "hero_GreyTalon" (+ 1 ?d1))
)

; Guided Owl добивает убегающих.
;   hero_GreyTalon -> threat_execute
(defrule r012
   (fact (name hero_GreyTalon) (cf ?c1) (depth ?d1))
   =>
   (derive threat_execute ?c1 0.90 r012 "hero_GreyTalon" (+ 1 ?d1))
)

; Fixation и пулемётный урон: постоянное пулевое давление.
;   hero_Haze -> threat_bullet_dps
(defrule r013
   (fact (name hero_Haze) (cf ?c1) (depth ?d1))
   =>
   (derive threat_bullet_dps ?c1 0.90 r013 "hero_Haze" (+ 1 ?d1))
)

; Smoke Bomb даёт невидимость и уход из фокуса.
;   hero_Haze -> threat_invisibility
(defrule r014
   (fact (name hero_Haze) (cf ?c1) (depth ?d1))
   =>
   (derive threat_invisibility ?c1 0.90 r014 "hero_Haze" (+ 1 ?d1))
)

; Bullet Dance — канальный AoE по всем в радиусе.
;   hero_Haze -> threat_aoe_channel
(defrule r015
   (fact (name hero_Haze) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r015 "hero_Haze" (+ 1 ?d1))
)

; Catalyst и Afterburn жгут цель длительным уроном.
;   hero_Infernus -> threat_dot_pressure
(defrule r016
   (fact (name hero_Infernus) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dot_pressure ?c1 0.90 r016 "hero_Infernus" (+ 1 ?d1))
)

; Concussive Combustion — канальный AoE-взрыв.
;   hero_Infernus -> threat_aoe_channel
(defrule r017
   (fact (name hero_Infernus) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r017 "hero_Infernus" (+ 1 ?d1))
)

; Flame Dash и огонь держат зону.
;   hero_Infernus -> threat_zone_control
(defrule r018
   (fact (name hero_Infernus) (cf ?c1) (depth ?d1))
   =>
   (derive threat_zone_control ?c1 0.90 r018 "hero_Infernus" (+ 1 ?d1))
)

; Kudzu и Air Drop залетают в тыл.
;   hero_Ivy -> threat_dive
(defrule r019
   (fact (name hero_Ivy) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r019 "hero_Ivy" (+ 1 ?d1))
)

; Опутывание лишает мобильности.
;   hero_Ivy -> threat_hard_cc
(defrule r020
   (fact (name hero_Ivy) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r020 "hero_Ivy" (+ 1 ?d1))
)

; Быстро меняет позицию и уходит.
;   hero_Ivy -> threat_mobility
(defrule r021
   (fact (name hero_Ivy) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r021 "hero_Ivy" (+ 1 ?d1))
)

; Ice Path и Arctic Beam контролируют пространство.
;   hero_Kelvin -> threat_zone_control
(defrule r022
   (fact (name hero_Kelvin) (cf ?c1) (depth ?d1))
   =>
   (derive threat_zone_control ?c1 0.90 r022 "hero_Kelvin" (+ 1 ?d1))
)

; Frozen Shelter — канальный купол, меняющий бой.
;   hero_Kelvin -> threat_aoe_channel
(defrule r023
   (fact (name hero_Kelvin) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r023 "hero_Kelvin" (+ 1 ?d1))
)

; Лечит союзников и себя.
;   hero_Kelvin -> threat_sustain
(defrule r024
   (fact (name hero_Kelvin) (cf ?c1) (depth ?d1))
   =>
   (derive threat_sustain ?c1 0.90 r024 "hero_Kelvin" (+ 1 ?d1))
)

; Essence Bomb и Malice дают магический берст.
;   hero_LadyGeist -> threat_burst_magic
(defrule r025
   (fact (name hero_LadyGeist) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r025 "hero_LadyGeist" (+ 1 ?d1))
)

; Soul Exchange меняет здоровье местами — казнь для раненого.
;   hero_LadyGeist -> threat_execute
(defrule r026
   (fact (name hero_LadyGeist) (cf ?c1) (depth ?d1))
   =>
   (derive threat_execute ?c1 0.90 r026 "hero_LadyGeist" (+ 1 ?d1))
)

; Life Drain восстанавливает здоровье.
;   hero_LadyGeist -> threat_sustain
(defrule r027
   (fact (name hero_LadyGeist) (cf ?c1) (depth ?d1))
   =>
   (derive threat_sustain ?c1 0.90 r027 "hero_LadyGeist" (+ 1 ?d1))
)

; Death Slam и прыжки — агрессивный заход в тыл.
;   hero_Lash -> threat_dive
(defrule r028
   (fact (name hero_Lash) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r028 "hero_Lash" (+ 1 ?d1))
)

; Подброс и удар держат цель в воздухе.
;   hero_Lash -> threat_hard_cc
(defrule r029
   (fact (name hero_Lash) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r029 "hero_Lash" (+ 1 ?d1))
)

; Death Slam накрывает площадь.
;   hero_Lash -> threat_aoe_channel
(defrule r030
   (fact (name hero_Lash) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r030 "hero_Lash" (+ 1 ?d1))
)

; Турели и стена запирают зону.
;   hero_McGinnis -> threat_zone_control
(defrule r031
   (fact (name hero_McGinnis) (cf ?c1) (depth ?d1))
   =>
   (derive threat_zone_control ?c1 0.90 r031 "hero_McGinnis" (+ 1 ?d1))
)

; Турели и пулемёт дают устойчивый пулевой урон.
;   hero_McGinnis -> threat_bullet_dps
(defrule r032
   (fact (name hero_McGinnis) (cf ?c1) (depth ?d1))
   =>
   (derive threat_bullet_dps ?c1 0.90 r032 "hero_McGinnis" (+ 1 ?d1))
)

; Heavy Barrage — канальный обстрел площади.
;   hero_McGinnis -> threat_aoe_channel
(defrule r033
   (fact (name hero_McGinnis) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r033 "hero_McGinnis" (+ 1 ?d1))
)

; Combo хватает и держит цель — самый жёсткий контроль.
;   hero_MoKrill -> threat_hard_cc
(defrule r034
   (fact (name hero_MoKrill) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r034 "hero_MoKrill" (+ 1 ?d1))
)

; Много здоровья, играет впереди.
;   hero_MoKrill -> threat_tank_frontline
(defrule r035
   (fact (name hero_MoKrill) (cf ?c1) (depth ?d1))
   =>
   (derive threat_tank_frontline ?c1 0.90 r035 "hero_MoKrill" (+ 1 ?d1))
)

; Burrow заходит под землёй в тыл.
;   hero_MoKrill -> threat_dive
(defrule r036
   (fact (name hero_MoKrill) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r036 "hero_MoKrill" (+ 1 ?d1))
)

; Pulse Grenade и Carbine — магический берст.
;   hero_Paradox -> threat_burst_magic
(defrule r037
   (fact (name hero_Paradox) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r037 "hero_Paradox" (+ 1 ?d1))
)

; Time Wall и Swap ломают позицию.
;   hero_Paradox -> threat_hard_cc
(defrule r038
   (fact (name hero_Paradox) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r038 "hero_Paradox" (+ 1 ?d1))
)

; Быстро меняет позицию.
;   hero_Paradox -> threat_mobility
(defrule r039
   (fact (name hero_Paradox) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r039 "hero_Paradox" (+ 1 ?d1))
)

; Barrage и Affliction — сильный магический урон.
;   hero_Pocket -> threat_burst_magic
(defrule r040
   (fact (name hero_Pocket) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r040 "hero_Pocket" (+ 1 ?d1))
)

; Affliction накрывает площадь.
;   hero_Pocket -> threat_aoe_channel
(defrule r041
   (fact (name hero_Pocket) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r041 "hero_Pocket" (+ 1 ?d1))
)

; Satchel и Cloak дают уход.
;   hero_Pocket -> threat_mobility
(defrule r042
   (fact (name hero_Pocket) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r042 "hero_Pocket" (+ 1 ?d1))
)

; Storm Cloud — канальный AoE, выжигающий зону.
;   hero_Seven -> threat_aoe_channel
(defrule r043
   (fact (name hero_Seven) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r043 "hero_Seven" (+ 1 ?d1))
)

; Lightning Ball и Storm держат территорию.
;   hero_Seven -> threat_zone_control
(defrule r044
   (fact (name hero_Seven) (cf ?c1) (depth ?d1))
   =>
   (derive threat_zone_control ?c1 0.90 r044 "hero_Seven" (+ 1 ?d1))
)

; Static Charge наказывает за кучность.
;   hero_Seven -> threat_poke
(defrule r045
   (fact (name hero_Seven) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r045 "hero_Seven" (+ 1 ?d1))
)

; Slice and Dice залетает в тыл.
;   hero_Shiv -> threat_dive
(defrule r046
   (fact (name hero_Shiv) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r046 "hero_Shiv" (+ 1 ?d1))
)

; Killing Blow казнит раненых.
;   hero_Shiv -> threat_execute
(defrule r047
   (fact (name hero_Shiv) (cf ?c1) (depth ?d1))
   =>
   (derive threat_execute ?c1 0.90 r047 "hero_Shiv" (+ 1 ?d1))
)

; Bloodletting откладывает урон и лечится.
;   hero_Shiv -> threat_sustain
(defrule r048
   (fact (name hero_Shiv) (cf ?c1) (depth ?d1))
   =>
   (derive threat_sustain ?c1 0.90 r048 "hero_Shiv" (+ 1 ?d1))
)

; Снайпер: Assassinate с дистанции.
;   hero_Vindicta -> threat_poke
(defrule r049
   (fact (name hero_Vindicta) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r049 "hero_Vindicta" (+ 1 ?d1))
)

; Assassinate добивает с большого расстояния.
;   hero_Vindicta -> threat_execute
(defrule r050
   (fact (name hero_Vindicta) (cf ?c1) (depth ?d1))
   =>
   (derive threat_execute ?c1 0.90 r050 "hero_Vindicta" (+ 1 ?d1))
)

; Полёт и уход из-под удара.
;   hero_Vindicta -> threat_mobility
(defrule r051
   (fact (name hero_Vindicta) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r051 "hero_Vindicta" (+ 1 ?d1))
)

; The Cube и Goo Ball — живучесть.
;   hero_Viscous -> threat_tank_frontline
(defrule r052
   (fact (name hero_Viscous) (cf ?c1) (depth ?d1))
   =>
   (derive threat_tank_frontline ?c1 0.90 r052 "hero_Viscous" (+ 1 ?d1))
)

; Goo Ball влетает в строй.
;   hero_Viscous -> threat_dive
(defrule r053
   (fact (name hero_Viscous) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r053 "hero_Viscous" (+ 1 ?d1))
)

; Puddle Punch и Goo Ball накрывают площадь.
;   hero_Viscous -> threat_aoe_channel
(defrule r054
   (fact (name hero_Viscous) (cf ?c1) (depth ?d1))
   =>
   (derive threat_aoe_channel ?c1 0.90 r054 "hero_Viscous" (+ 1 ?d1))
)

; Full Auto — пулевой берст.
;   hero_Wraith -> threat_bullet_dps
(defrule r055
   (fact (name hero_Wraith) (cf ?c1) (depth ?d1))
   =>
   (derive threat_bullet_dps ?c1 0.90 r055 "hero_Wraith" (+ 1 ?d1))
)

; Telekinesis поднимает и держит.
;   hero_Wraith -> threat_hard_cc
(defrule r056
   (fact (name hero_Wraith) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r056 "hero_Wraith" (+ 1 ?d1))
)

; Project Mind — телепорт.
;   hero_Wraith -> threat_mobility
(defrule r057
   (fact (name hero_Wraith) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r057 "hero_Wraith" (+ 1 ?d1))
)

; Flying Strike влетает в цель.
;   hero_Yamato -> threat_dive
(defrule r058
   (fact (name hero_Yamato) (cf ?c1) (depth ?d1))
   =>
   (derive threat_dive ?c1 0.90 r058 "hero_Yamato" (+ 1 ?d1))
)

; Shadow Transformation и Crimson Slash добивают.
;   hero_Yamato -> threat_execute
(defrule r059
   (fact (name hero_Yamato) (cf ?c1) (depth ?d1))
   =>
   (derive threat_execute ?c1 0.90 r059 "hero_Yamato" (+ 1 ?d1))
)

; Power Slash — сильный магический удар.
;   hero_Yamato -> threat_burst_magic
(defrule r060
   (fact (name hero_Yamato) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r060 "hero_Yamato" (+ 1 ?d1))
)

; Играет от передней линии.
;   hero_Warden -> threat_tank_frontline
(defrule r061
   (fact (name hero_Warden) (cf ?c1) (depth ?d1))
   =>
   (derive threat_tank_frontline ?c1 0.90 r061 "hero_Warden" (+ 1 ?d1))
)

; Ультимейт запирает зону.
;   hero_Warden -> threat_zone_control
(defrule r062
   (fact (name hero_Warden) (cf ?c1) (depth ?d1))
   =>
   (derive threat_zone_control ?c1 0.90 r062 "hero_Warden" (+ 1 ?d1))
)

; Пулевое давление с передовой.
;   hero_Warden -> threat_bullet_dps
(defrule r063
   (fact (name hero_Warden) (cf ?c1) (depth ?d1))
   =>
   (derive threat_bullet_dps ?c1 0.90 r063 "hero_Warden" (+ 1 ?d1))
)

; Держит дистанцию и наказывает.
;   hero_Mirage -> threat_poke
(defrule r064
   (fact (name hero_Mirage) (cf ?c1) (depth ?d1))
   =>
   (derive threat_poke ?c1 0.90 r064 "hero_Mirage" (+ 1 ?d1))
)

; Fire Beetles и метка дают берст.
;   hero_Mirage -> threat_burst_magic
(defrule r065
   (fact (name hero_Mirage) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r065 "hero_Mirage" (+ 1 ?d1))
)

; Телепорт и уход.
;   hero_Mirage -> threat_mobility
(defrule r066
   (fact (name hero_Mirage) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r066 "hero_Mirage" (+ 1 ?d1))
)

; Лассо вытаскивает цель.
;   hero_Holliday -> threat_hard_cc
(defrule r067
   (fact (name hero_Holliday) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r067 "hero_Holliday" (+ 1 ?d1))
)

; Быстро перемещается по карте.
;   hero_Holliday -> threat_mobility
(defrule r068
   (fact (name hero_Holliday) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r068 "hero_Holliday" (+ 1 ?d1))
)

; Пулевой урон вблизи.
;   hero_Holliday -> threat_bullet_dps
(defrule r069
   (fact (name hero_Holliday) (cf ?c1) (depth ?d1))
   =>
   (derive threat_bullet_dps ?c1 0.90 r069 "hero_Holliday" (+ 1 ?d1))
)

; Магический берст по площади.
;   hero_Sinclair -> threat_burst_magic
(defrule r070
   (fact (name hero_Sinclair) (cf ?c1) (depth ?d1))
   =>
   (derive threat_burst_magic ?c1 0.90 r070 "hero_Sinclair" (+ 1 ?d1))
)

; Контроль и кража ультимейта.
;   hero_Sinclair -> threat_hard_cc
(defrule r071
   (fact (name hero_Sinclair) (cf ?c1) (depth ?d1))
   =>
   (derive threat_hard_cc ?c1 0.90 r071 "hero_Sinclair" (+ 1 ?d1))
)

; Уход и перестановка.
;   hero_Sinclair -> threat_mobility
(defrule r072
   (fact (name hero_Sinclair) (cf ?c1) (depth ?d1))
   =>
   (derive threat_mobility ?c1 0.90 r072 "hero_Sinclair" (+ 1 ?d1))
)

; Пулевое давление снимается бронёй от пуль и предметами, блокирующими пули.
;   threat_bullet_dps -> need_anti_bullet
(defrule r073
   (fact (name threat_bullet_dps) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_bullet ?c1 0.85 r073 "threat_bullet_dps" (+ 1 ?d1))
)

; Магический берст требует сопротивления духу или барьера.
;   threat_burst_magic -> need_anti_magic
(defrule r074
   (fact (name threat_burst_magic) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_magic ?c1 0.85 r074 "threat_burst_magic" (+ 1 ?d1))
)

; Длительный урон снимается очищением дебаффов.
;   threat_dot_pressure -> need_anti_dot
(defrule r075
   (fact (name threat_dot_pressure) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_dot ?c1 0.85 r075 "threat_dot_pressure" (+ 1 ?d1))
)

; Жёсткий контроль требует иммунитета или сокращения длительности.
;   threat_hard_cc -> need_anti_cc
(defrule r076
   (fact (name threat_hard_cc) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_cc ?c1 0.85 r076 "threat_hard_cc" (+ 1 ?d1))
)

; Танка нельзя пробить без снижения брони и процентов здоровья.
;   threat_tank_frontline -> need_anti_tank
(defrule r077
   (fact (name threat_tank_frontline) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_tank ?c1 0.85 r077 "threat_tank_frontline" (+ 1 ?d1))
)

; Заход в тыл требует мобильности и живучести под фокусом.
;   threat_dive -> need_anti_dive
(defrule r078
   (fact (name threat_dive) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_dive ?c1 0.85 r078 "threat_dive" (+ 1 ?d1))
)

; Дальний урон снимается регенерацией и дистанцией.
;   threat_poke -> need_anti_poke
(defrule r079
   (fact (name threat_poke) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_poke ?c1 0.85 r079 "threat_poke" (+ 1 ?d1))
)

; Невидимость наказывается сайленсом и замедлением.
;   threat_invisibility -> need_anti_invis
(defrule r080
   (fact (name threat_invisibility) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_invis ?c1 0.85 r080 "threat_invisibility" (+ 1 ?d1))
)

; Канальный AoE срывается сайленсом, станом или иммунитетом к пулям.
;   threat_aoe_channel -> need_anti_channel
(defrule r081
   (fact (name threat_aoe_channel) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_channel ?c1 0.85 r081 "threat_aoe_channel" (+ 1 ?d1))
)

; Казнь предотвращается неуязвимостью, воскрешением и щитами.
;   threat_execute -> need_anti_execute
(defrule r082
   (fact (name threat_execute) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_execute ?c1 0.85 r082 "threat_execute" (+ 1 ?d1))
)

; Зону контроля нужно покидать телепортом или переживать.
;   threat_zone_control -> need_anti_zone
(defrule r083
   (fact (name threat_zone_control) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_zone ?c1 0.85 r083 "threat_zone_control" (+ 1 ?d1))
)

; Мобильную цель нужно замедлять и лишать рывков.
;   threat_mobility -> need_anti_mobility
(defrule r084
   (fact (name threat_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_mobility ?c1 0.85 r084 "threat_mobility" (+ 1 ?d1))
)

; Лечение и вампиризм снимаются сокращением лечения.
;   threat_sustain -> need_anti_heal
(defrule r085
   (fact (name threat_sustain) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_heal ?c1 0.85 r085 "threat_sustain" (+ 1 ?d1))
)

; Против дальнего урона помогает собственный регенерация и восстановление.
;   threat_poke -> need_sustain_self
(defrule r086
   (fact (name threat_poke) (cf ?c1) (depth ?d1))
   =>
   (derive need_sustain_self ?c1 0.85 r086 "threat_poke" (+ 1 ?d1))
)

; Против длительного урона нужен собственный запас восстановления.
;   threat_dot_pressure -> need_sustain_self
(defrule r087
   (fact (name threat_dot_pressure) (cf ?c1) (depth ?d1))
   =>
   (derive need_sustain_self ?c1 0.85 r087 "threat_dot_pressure" (+ 1 ?d1))
)

; Против танка нужен собственный устойчивый урон, иначе его не пробить.
;   threat_tank_frontline -> need_damage_core
(defrule r088
   (fact (name threat_tank_frontline) (cf ?c1) (depth ?d1))
   =>
   (derive need_damage_core ?c1 0.85 r088 "threat_tank_frontline" (+ 1 ?d1))
)

; Невидимость плюс пулевой урон: без контроля невидимости пулевое давление не остановить.
;   threat_bullet_dps, threat_invisibility -> need_anti_invis
(defrule r089
   (fact (name threat_bullet_dps) (cf ?c1) (depth ?d1))
   (fact (name threat_invisibility) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_invis (min ?c1 ?c2) 0.85 r089 "threat_bullet_dps threat_invisibility" (+ 1 (max ?d1 ?d2)))
)

; Канал под прикрытием контроля: нужен иммунитет или срыв канала.
;   threat_aoe_channel, threat_hard_cc -> need_anti_cc
(defrule r090
   (fact (name threat_aoe_channel) (cf ?c1) (depth ?d1))
   (fact (name threat_hard_cc) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_cc (min ?c1 ?c2) 0.85 r090 "threat_aoe_channel threat_hard_cc" (+ 1 (max ?d1 ?d2)))
)

; Добивающий урон с дистанции: нужен предмет, переживающий казнь.
;   threat_execute, threat_poke -> need_anti_execute
(defrule r091
   (fact (name threat_execute) (cf ?c1) (depth ?d1))
   (fact (name threat_poke) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_execute (min ?c1 ?c2) 0.85 r091 "threat_execute threat_poke" (+ 1 (max ?d1 ?d2)))
)

; Танк врывается в тыл: нужна мобильность и живучесть.
;   threat_dive, threat_tank_frontline -> need_anti_dive
(defrule r092
   (fact (name threat_dive) (cf ?c1) (depth ?d1))
   (fact (name threat_tank_frontline) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_dive (min ?c1 ?c2) 0.85 r092 "threat_dive threat_tank_frontline" (+ 1 (max ?d1 ?d2)))
)

; Урон по времени плюс лечение: без сокращения лечения цель не убить.
;   threat_dot_pressure, threat_sustain -> need_anti_heal
(defrule r093
   (fact (name threat_dot_pressure) (cf ?c1) (depth ?d1))
   (fact (name threat_sustain) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_heal (min ?c1 ?c2) 0.85 r093 "threat_dot_pressure threat_sustain" (+ 1 (max ?d1 ?d2)))
)

; Магический берст добивает: нужен барьер от духа.
;   threat_burst_magic, threat_execute -> need_anti_magic
(defrule r094
   (fact (name threat_burst_magic) (cf ?c1) (depth ?d1))
   (fact (name threat_execute) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_magic (min ?c1 ?c2) 0.85 r094 "threat_burst_magic threat_execute" (+ 1 (max ?d1 ?d2)))
)

; Пулевой танк: нужно снижение брони и процентов здоровья.
;   threat_bullet_dps, threat_tank_frontline -> need_anti_tank
(defrule r095
   (fact (name threat_bullet_dps) (cf ?c1) (depth ?d1))
   (fact (name threat_tank_frontline) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_tank (min ?c1 ?c2) 0.85 r095 "threat_bullet_dps threat_tank_frontline" (+ 1 (max ?d1 ?d2)))
)

; Зона плюс канал: из зоны нужно выходить, а канал срывать.
;   threat_zone_control, threat_aoe_channel -> need_anti_zone
(defrule r096
   (fact (name threat_zone_control) (cf ?c1) (depth ?d1))
   (fact (name threat_aoe_channel) (cf ?c2) (depth ?d2))
   =>
   (derive need_anti_zone (min ?c1 ?c2) 0.85 r096 "threat_zone_control threat_aoe_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_MysticShot_damage_core -> candidate_MysticShot_damage_core
(defrule r097
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_MysticShot_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MysticShot_damage_core (min ?c1 ?c2) 0.80 r097 "need_damage_core item_provides_MysticShot_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_Berserker_damage_core -> candidate_Berserker_damage_core
(defrule r098
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Berserker_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Berserker_damage_core (min ?c1 ?c2) 0.80 r098 "need_damage_core item_provides_Berserker_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_heal, item_provides_ToxicBullets_anti_heal -> candidate_ToxicBullets_anti_heal
(defrule r099
   (fact (name need_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ToxicBullets_anti_heal) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ToxicBullets_anti_heal (min ?c1 ?c2) 0.80 r099 "need_anti_heal item_provides_ToxicBullets_anti_heal" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_tank, item_provides_ToxicBullets_anti_tank -> candidate_ToxicBullets_anti_tank
(defrule r100
   (fact (name need_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ToxicBullets_anti_tank) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ToxicBullets_anti_tank (min ?c1 ?c2) 0.80 r100 "need_anti_tank item_provides_ToxicBullets_anti_tank" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_poke, item_provides_Headhunter_anti_poke -> candidate_Headhunter_anti_poke
(defrule r101
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Headhunter_anti_poke) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Headhunter_anti_poke (min ?c1 ?c2) 0.80 r101 "need_anti_poke item_provides_Headhunter_anti_poke" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_Headhunter_damage_core -> candidate_Headhunter_damage_core
(defrule r102
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Headhunter_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Headhunter_damage_core (min ?c1 ?c2) 0.80 r102 "need_damage_core item_provides_Headhunter_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_bullet, item_provides_EscalatingResilience_anti_bullet -> candidate_EscalatingResilience_anti_bullet
(defrule r103
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_provides_EscalatingResilience_anti_bullet) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EscalatingResilience_anti_bullet (min ?c1 ?c2) 0.80 r103 "need_anti_bullet item_provides_EscalatingResilience_anti_bullet" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_mobility, item_provides_SlowingHex_anti_mobility -> candidate_SlowingHex_anti_mobility
(defrule r104
   (fact (name need_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SlowingHex_anti_mobility) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SlowingHex_anti_mobility (min ?c1 ?c2) 0.80 r104 "need_anti_mobility item_provides_SlowingHex_anti_mobility" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dive, item_provides_SlowingHex_anti_dive -> candidate_SlowingHex_anti_dive
(defrule r105
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SlowingHex_anti_dive) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SlowingHex_anti_dive (min ?c1 ?c2) 0.80 r105 "need_anti_dive item_provides_SlowingHex_anti_dive" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_poke, item_provides_Sharpshooter_anti_poke -> candidate_Sharpshooter_anti_poke
(defrule r106
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Sharpshooter_anti_poke) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Sharpshooter_anti_poke (min ?c1 ?c2) 0.80 r106 "need_anti_poke item_provides_Sharpshooter_anti_poke" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_Sharpshooter_damage_core -> candidate_Sharpshooter_damage_core
(defrule r107
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Sharpshooter_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Sharpshooter_damage_core (min ?c1 ?c2) 0.80 r107 "need_damage_core item_provides_Sharpshooter_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_heal, item_provides_Inhibitor_anti_heal -> candidate_Inhibitor_anti_heal
(defrule r108
   (fact (name need_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Inhibitor_anti_heal) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Inhibitor_anti_heal (min ?c1 ?c2) 0.80 r108 "need_anti_heal item_provides_Inhibitor_anti_heal" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_mobility, item_provides_Inhibitor_anti_mobility -> candidate_Inhibitor_anti_mobility
(defrule r109
   (fact (name need_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Inhibitor_anti_mobility) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Inhibitor_anti_mobility (min ?c1 ?c2) 0.80 r109 "need_anti_mobility item_provides_Inhibitor_anti_mobility" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_regen, item_provides_Inhibitor_anti_regen -> candidate_Inhibitor_anti_regen
(defrule r110
   (fact (name need_anti_regen) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Inhibitor_anti_regen) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Inhibitor_anti_regen (min ?c1 ?c2) 0.80 r110 "need_anti_regen item_provides_Inhibitor_anti_regen" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_tank, item_provides_CripplingHeadshot_anti_tank -> candidate_CripplingHeadshot_anti_tank
(defrule r111
   (fact (name need_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_provides_CripplingHeadshot_anti_tank) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_CripplingHeadshot_anti_tank (min ?c1 ?c2) 0.80 r111 "need_anti_tank item_provides_CripplingHeadshot_anti_tank" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_tank, item_provides_HuntersAura_anti_tank -> candidate_HuntersAura_anti_tank
(defrule r112
   (fact (name need_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_provides_HuntersAura_anti_tank) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_HuntersAura_anti_tank (min ?c1 ?c2) 0.80 r112 "need_anti_tank item_provides_HuntersAura_anti_tank" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_bullet, item_provides_HuntersAura_anti_bullet -> candidate_HuntersAura_anti_bullet
(defrule r113
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_provides_HuntersAura_anti_bullet) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_HuntersAura_anti_bullet (min ?c1 ?c2) 0.80 r113 "need_anti_bullet item_provides_HuntersAura_anti_bullet" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_channel, item_provides_Silencer_anti_channel -> candidate_Silencer_anti_channel
(defrule r114
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Silencer_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Silencer_anti_channel (min ?c1 ?c2) 0.80 r114 "need_anti_channel item_provides_Silencer_anti_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_mobility, item_provides_Silencer_anti_mobility -> candidate_Silencer_anti_mobility
(defrule r115
   (fact (name need_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Silencer_anti_mobility) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Silencer_anti_mobility (min ?c1 ?c2) 0.80 r115 "need_anti_mobility item_provides_Silencer_anti_mobility" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_SpiritualOverflow_damage_core -> candidate_SpiritualOverflow_damage_core
(defrule r116
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SpiritualOverflow_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SpiritualOverflow_damage_core (min ?c1 ?c2) 0.80 r116 "need_damage_core item_provides_SpiritualOverflow_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dive, item_provides_ExtraStamina_anti_dive -> candidate_ExtraStamina_anti_dive
(defrule r117
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ExtraStamina_anti_dive) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ExtraStamina_anti_dive (min ?c1 ?c2) 0.80 r117 "need_anti_dive item_provides_ExtraStamina_anti_dive" (+ 1 (max ?d1 ?d2)))
)

;   need_sustain_self, item_provides_HealingRite_sustain_self -> candidate_HealingRite_sustain_self
(defrule r118
   (fact (name need_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_provides_HealingRite_sustain_self) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_HealingRite_sustain_self (min ?c1 ?c2) 0.80 r118 "need_sustain_self item_provides_HealingRite_sustain_self" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_bullet, item_provides_BulletArmor_anti_bullet -> candidate_BulletArmor_anti_bullet
(defrule r119
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_provides_BulletArmor_anti_bullet) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_BulletArmor_anti_bullet (min ?c1 ?c2) 0.80 r119 "need_anti_bullet item_provides_BulletArmor_anti_bullet" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_magic, item_provides_SpiritArmor_anti_magic -> candidate_SpiritArmor_anti_magic
(defrule r120
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SpiritArmor_anti_magic) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SpiritArmor_anti_magic (min ?c1 ?c2) 0.80 r120 "need_anti_magic item_provides_SpiritArmor_anti_magic" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dot, item_provides_DebuffReducer_anti_dot -> candidate_DebuffReducer_anti_dot
(defrule r121
   (fact (name need_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DebuffReducer_anti_dot) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DebuffReducer_anti_dot (min ?c1 ?c2) 0.80 r121 "need_anti_dot item_provides_DebuffReducer_anti_dot" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_cc, item_provides_ReactiveBarrier_anti_cc -> candidate_ReactiveBarrier_anti_cc
(defrule r122
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ReactiveBarrier_anti_cc) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ReactiveBarrier_anti_cc (min ?c1 ?c2) 0.80 r122 "need_anti_cc item_provides_ReactiveBarrier_anti_cc" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_magic, item_provides_ReactiveBarrier_anti_magic -> candidate_ReactiveBarrier_anti_magic
(defrule r123
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ReactiveBarrier_anti_magic) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ReactiveBarrier_anti_magic (min ?c1 ?c2) 0.80 r123 "need_anti_magic item_provides_ReactiveBarrier_anti_magic" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_magic, item_provides_DivineBarrier_anti_magic -> candidate_DivineBarrier_anti_magic
(defrule r124
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivineBarrier_anti_magic) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivineBarrier_anti_magic (min ?c1 ?c2) 0.80 r124 "need_anti_magic item_provides_DivineBarrier_anti_magic" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_execute, item_provides_DivineBarrier_anti_execute -> candidate_DivineBarrier_anti_execute
(defrule r125
   (fact (name need_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivineBarrier_anti_execute) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivineBarrier_anti_execute (min ?c1 ?c2) 0.80 r125 "need_anti_execute item_provides_DivineBarrier_anti_execute" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_bullet, item_provides_MetalSkin_anti_bullet -> candidate_MetalSkin_anti_bullet
(defrule r126
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_provides_MetalSkin_anti_bullet) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MetalSkin_anti_bullet (min ?c1 ?c2) 0.80 r126 "need_anti_bullet item_provides_MetalSkin_anti_bullet" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_channel, item_provides_MetalSkin_anti_channel -> candidate_MetalSkin_anti_channel
(defrule r127
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_provides_MetalSkin_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MetalSkin_anti_channel (min ?c1 ?c2) 0.80 r127 "need_anti_channel item_provides_MetalSkin_anti_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_bullet, item_provides_ReturnFire_anti_bullet -> candidate_ReturnFire_anti_bullet
(defrule r128
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_provides_ReturnFire_anti_bullet) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ReturnFire_anti_bullet (min ?c1 ?c2) 0.80 r128 "need_anti_bullet item_provides_ReturnFire_anti_bullet" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dive, item_provides_SuperiorStamina_anti_dive -> candidate_SuperiorStamina_anti_dive
(defrule r129
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SuperiorStamina_anti_dive) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SuperiorStamina_anti_dive (min ?c1 ?c2) 0.80 r129 "need_anti_dive item_provides_SuperiorStamina_anti_dive" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_poke, item_provides_Fortitude_anti_poke -> candidate_Fortitude_anti_poke
(defrule r130
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Fortitude_anti_poke) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Fortitude_anti_poke (min ?c1 ?c2) 0.80 r130 "need_anti_poke item_provides_Fortitude_anti_poke" (+ 1 (max ?d1 ?d2)))
)

;   need_sustain_self, item_provides_Fortitude_sustain_self -> candidate_Fortitude_sustain_self
(defrule r131
   (fact (name need_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Fortitude_sustain_self) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Fortitude_sustain_self (min ?c1 ?c2) 0.80 r131 "need_sustain_self item_provides_Fortitude_sustain_self" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dot, item_provides_DebuffRemover_anti_dot -> candidate_DebuffRemover_anti_dot
(defrule r132
   (fact (name need_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DebuffRemover_anti_dot) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DebuffRemover_anti_dot (min ?c1 ?c2) 0.80 r132 "need_anti_dot item_provides_DebuffRemover_anti_dot" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_cc, item_provides_DebuffRemover_anti_cc -> candidate_DebuffRemover_anti_cc
(defrule r133
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DebuffRemover_anti_cc) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DebuffRemover_anti_cc (min ?c1 ?c2) 0.80 r133 "need_anti_cc item_provides_DebuffRemover_anti_cc" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dive, item_provides_Colossus_anti_dive -> candidate_Colossus_anti_dive
(defrule r134
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Colossus_anti_dive) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Colossus_anti_dive (min ?c1 ?c2) 0.80 r134 "need_anti_dive item_provides_Colossus_anti_dive" (+ 1 (max ?d1 ?d2)))
)

;   need_sustain_self, item_provides_Colossus_sustain_self -> candidate_Colossus_sustain_self
(defrule r135
   (fact (name need_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Colossus_sustain_self) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Colossus_sustain_self (min ?c1 ?c2) 0.80 r135 "need_sustain_self item_provides_Colossus_sustain_self" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_execute, item_provides_SoulRebirth_anti_execute -> candidate_SoulRebirth_anti_execute
(defrule r136
   (fact (name need_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SoulRebirth_anti_execute) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SoulRebirth_anti_execute (min ?c1 ?c2) 0.80 r136 "need_anti_execute item_provides_SoulRebirth_anti_execute" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_magic, item_provides_DivinersKevlar_anti_magic -> candidate_DivinersKevlar_anti_magic
(defrule r137
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivinersKevlar_anti_magic) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivinersKevlar_anti_magic (min ?c1 ?c2) 0.80 r137 "need_anti_magic item_provides_DivinersKevlar_anti_magic" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_cc, item_provides_DivinersKevlar_anti_cc -> candidate_DivinersKevlar_anti_cc
(defrule r138
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivinersKevlar_anti_cc) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivinersKevlar_anti_cc (min ?c1 ?c2) 0.80 r138 "need_anti_cc item_provides_DivinersKevlar_anti_cc" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_disable, item_provides_DivinersKevlar_anti_disable -> candidate_DivinersKevlar_anti_disable
(defrule r139
   (fact (name need_anti_disable) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivinersKevlar_anti_disable) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivinersKevlar_anti_disable (min ?c1 ?c2) 0.80 r139 "need_anti_disable item_provides_DivinersKevlar_anti_disable" (+ 1 (max ?d1 ?d2)))
)

;   need_sustain_self, item_provides_Leech_sustain_self -> candidate_Leech_sustain_self
(defrule r140
   (fact (name need_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Leech_sustain_self) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Leech_sustain_self (min ?c1 ?c2) 0.80 r140 "need_sustain_self item_provides_Leech_sustain_self" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_MysticBurst_damage_core -> candidate_MysticBurst_damage_core
(defrule r141
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_MysticBurst_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MysticBurst_damage_core (min ?c1 ?c2) 0.80 r141 "need_damage_core item_provides_MysticBurst_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_channel, item_provides_Knockdown_anti_channel -> candidate_Knockdown_anti_channel
(defrule r142
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Knockdown_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Knockdown_anti_channel (min ?c1 ?c2) 0.80 r142 "need_anti_channel item_provides_Knockdown_anti_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_poke, item_provides_Knockdown_anti_poke -> candidate_Knockdown_anti_poke
(defrule r143
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Knockdown_anti_poke) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Knockdown_anti_poke (min ?c1 ?c2) 0.80 r143 "need_anti_poke item_provides_Knockdown_anti_poke" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_execute, item_provides_EtherealShift_anti_execute -> candidate_EtherealShift_anti_execute
(defrule r144
   (fact (name need_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_provides_EtherealShift_anti_execute) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EtherealShift_anti_execute (min ?c1 ?c2) 0.80 r144 "need_anti_execute item_provides_EtherealShift_anti_execute" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_zone, item_provides_EtherealShift_anti_zone -> candidate_EtherealShift_anti_zone
(defrule r145
   (fact (name need_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_provides_EtherealShift_anti_zone) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EtherealShift_anti_zone (min ?c1 ?c2) 0.80 r145 "need_anti_zone item_provides_EtherealShift_anti_zone" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_dive, item_provides_WarpStone_anti_dive -> candidate_WarpStone_anti_dive
(defrule r146
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_provides_WarpStone_anti_dive) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_WarpStone_anti_dive (min ?c1 ?c2) 0.80 r146 "need_anti_dive item_provides_WarpStone_anti_dive" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_zone, item_provides_WarpStone_anti_zone -> candidate_WarpStone_anti_zone
(defrule r147
   (fact (name need_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_provides_WarpStone_anti_zone) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_WarpStone_anti_zone (min ?c1 ?c2) 0.80 r147 "need_anti_zone item_provides_WarpStone_anti_zone" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_channel, item_provides_SilenceGlyph_anti_channel -> candidate_SilenceGlyph_anti_channel
(defrule r148
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SilenceGlyph_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SilenceGlyph_anti_channel (min ?c1 ?c2) 0.80 r148 "need_anti_channel item_provides_SilenceGlyph_anti_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_mobility, item_provides_SilenceGlyph_anti_mobility -> candidate_SilenceGlyph_anti_mobility
(defrule r149
   (fact (name need_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SilenceGlyph_anti_mobility) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SilenceGlyph_anti_mobility (min ?c1 ?c2) 0.80 r149 "need_anti_mobility item_provides_SilenceGlyph_anti_mobility" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_invis, item_provides_SilenceGlyph_anti_invis -> candidate_SilenceGlyph_anti_invis
(defrule r150
   (fact (name need_anti_invis) (cf ?c1) (depth ?d1))
   (fact (name item_provides_SilenceGlyph_anti_invis) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SilenceGlyph_anti_invis (min ?c1 ?c2) 0.80 r150 "need_anti_invis item_provides_SilenceGlyph_anti_invis" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_tank, item_provides_EscalatingExposure_anti_tank -> candidate_EscalatingExposure_anti_tank
(defrule r151
   (fact (name need_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_provides_EscalatingExposure_anti_tank) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EscalatingExposure_anti_tank (min ?c1 ?c2) 0.80 r151 "need_anti_tank item_provides_EscalatingExposure_anti_tank" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_channel, item_provides_Curse_anti_channel -> candidate_Curse_anti_channel
(defrule r152
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_channel (min ?c1 ?c2) 0.80 r152 "need_anti_channel item_provides_Curse_anti_channel" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_mobility, item_provides_Curse_anti_mobility -> candidate_Curse_anti_mobility
(defrule r153
   (fact (name need_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_mobility) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_mobility (min ?c1 ?c2) 0.80 r153 "need_anti_mobility item_provides_Curse_anti_mobility" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_heal, item_provides_Curse_anti_heal -> candidate_Curse_anti_heal
(defrule r154
   (fact (name need_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_heal) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_heal (min ?c1 ?c2) 0.80 r154 "need_anti_heal item_provides_Curse_anti_heal" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_immunity, item_provides_Curse_anti_immunity -> candidate_Curse_anti_immunity
(defrule r155
   (fact (name need_anti_immunity) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_immunity) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_immunity (min ?c1 ?c2) 0.80 r155 "need_anti_immunity item_provides_Curse_anti_immunity" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_purge, item_provides_Curse_anti_purge -> candidate_Curse_anti_purge
(defrule r156
   (fact (name need_anti_purge) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_purge) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_purge (min ?c1 ?c2) 0.80 r156 "need_anti_purge item_provides_Curse_anti_purge" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_cc, item_provides_Unstoppable_anti_cc -> candidate_Unstoppable_anti_cc
(defrule r157
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Unstoppable_anti_cc) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Unstoppable_anti_cc (min ?c1 ?c2) 0.80 r157 "need_anti_cc item_provides_Unstoppable_anti_cc" (+ 1 (max ?d1 ?d2)))
)

;   need_anti_silence, item_provides_Unstoppable_anti_silence -> candidate_Unstoppable_anti_silence
(defrule r158
   (fact (name need_anti_silence) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Unstoppable_anti_silence) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Unstoppable_anti_silence (min ?c1 ?c2) 0.80 r158 "need_anti_silence item_provides_Unstoppable_anti_silence" (+ 1 (max ?d1 ?d2)))
)

;   need_damage_core, item_provides_BoundlessSpirit_damage_core -> candidate_BoundlessSpirit_damage_core
(defrule r159
   (fact (name need_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_provides_BoundlessSpirit_damage_core) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_BoundlessSpirit_damage_core (min ?c1 ?c2) 0.80 r159 "need_damage_core item_provides_BoundlessSpirit_damage_core" (+ 1 (max ?d1 ?d2)))
)

;   need_sustain_self, item_provides_BoundlessSpirit_sustain_self -> candidate_BoundlessSpirit_sustain_self
(defrule r160
   (fact (name need_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_provides_BoundlessSpirit_sustain_self) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_BoundlessSpirit_sustain_self (min ?c1 ?c2) 0.80 r160 "need_sustain_self item_provides_BoundlessSpirit_sustain_self" (+ 1 (max ?d1 ?d2)))
)

; Metal Skin перекрывает окно Bullet Dance: пули не попадают.
;   need_anti_channel, hero_Haze -> candidate_MetalSkin_anti_channel
(defrule r161
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name hero_Haze) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_MetalSkin_anti_channel (min ?c1 ?c2) 0.80 r161 "need_anti_channel hero_Haze" (+ 1 (max ?d1 ?d2)))
)

; Curse снимает канал Storm Cloud и запрещает предметы.
;   need_anti_channel, hero_Seven -> candidate_Curse_anti_channel
(defrule r162
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name hero_Seven) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_channel (min ?c1 ?c2) 0.80 r162 "need_anti_channel hero_Seven" (+ 1 (max ?d1 ?d2)))
)

; Knockdown сбивает летящую Vindicta и срывает позицию.
;   need_anti_poke, hero_Vindicta -> candidate_Knockdown_anti_poke
(defrule r163
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name hero_Vindicta) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Knockdown_anti_poke (min ?c1 ?c2) 0.80 r163 "need_anti_poke hero_Vindicta" (+ 1 (max ?d1 ?d2)))
)

; Unstoppable не даёт Combo удержать цель.
;   need_anti_cc, hero_MoKrill -> candidate_Unstoppable_anti_cc
(defrule r164
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name hero_MoKrill) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Unstoppable_anti_cc (min ?c1 ?c2) 0.80 r164 "need_anti_cc hero_MoKrill" (+ 1 (max ?d1 ?d2)))
)

; Debuff Remover снимает Catalyst и Afterburn.
;   need_anti_dot, hero_Infernus -> candidate_DebuffRemover_anti_dot
(defrule r165
   (fact (name need_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name hero_Infernus) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DebuffRemover_anti_dot (min ?c1 ?c2) 0.80 r165 "need_anti_dot hero_Infernus" (+ 1 (max ?d1 ?d2)))
)

; Ethereal Shift переживает Soul Exchange.
;   need_anti_execute, hero_LadyGeist -> candidate_EtherealShift_anti_execute
(defrule r166
   (fact (name need_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name hero_LadyGeist) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EtherealShift_anti_execute (min ?c1 ?c2) 0.80 r166 "need_anti_execute hero_LadyGeist" (+ 1 (max ?d1 ?d2)))
)

; Toxic Bullets режут лечение Abrams и его регенерацию.
;   need_anti_heal, hero_Abrams -> candidate_ToxicBullets_anti_heal
(defrule r167
   (fact (name need_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name hero_Abrams) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ToxicBullets_anti_heal (min ?c1 ?c2) 0.80 r167 "need_anti_heal hero_Abrams" (+ 1 (max ?d1 ?d2)))
)

; Reactive Barrier гасит Telekinesis и даёт щит.
;   need_anti_cc, hero_Wraith -> candidate_ReactiveBarrier_anti_cc
(defrule r168
   (fact (name need_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name hero_Wraith) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ReactiveBarrier_anti_cc (min ?c1 ?c2) 0.80 r168 "need_anti_cc hero_Wraith" (+ 1 (max ?d1 ?d2)))
)

; Soul Rebirth возвращает в бой после Killing Blow.
;   need_anti_execute, hero_Yamato -> candidate_SoulRebirth_anti_execute
(defrule r169
   (fact (name need_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name hero_Yamato) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SoulRebirth_anti_execute (min ?c1 ?c2) 0.80 r169 "need_anti_execute hero_Yamato" (+ 1 (max ?d1 ?d2)))
)

; Reactive Barrier съедает Hook-комбо и магический берст.
;   need_anti_magic, hero_Bebop -> candidate_ReactiveBarrier_anti_magic
(defrule r170
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name hero_Bebop) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_ReactiveBarrier_anti_magic (min ?c1 ?c2) 0.80 r170 "need_anti_magic hero_Bebop" (+ 1 (max ?d1 ?d2)))
)

; Divine Barrier перекрывает Affliction.
;   need_anti_magic, hero_Pocket -> candidate_DivineBarrier_anti_magic
(defrule r171
   (fact (name need_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name hero_Pocket) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_DivineBarrier_anti_magic (min ?c1 ?c2) 0.80 r171 "need_anti_magic hero_Pocket" (+ 1 (max ?d1 ?d2)))
)

; Colossus переживает заход Shiv и даёт контроль.
;   need_anti_dive, hero_Shiv -> candidate_Colossus_anti_dive
(defrule r172
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name hero_Shiv) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Colossus_anti_dive (min ?c1 ?c2) 0.80 r172 "need_anti_dive hero_Shiv" (+ 1 (max ?d1 ?d2)))
)

; Warp Stone выводит из-под Death Slam.
;   need_anti_dive, hero_Lash -> candidate_WarpStone_anti_dive
(defrule r173
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name hero_Lash) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_WarpStone_anti_dive (min ?c1 ?c2) 0.80 r173 "need_anti_dive hero_Lash" (+ 1 (max ?d1 ?d2)))
)

; Ethereal Shift переживает купол и обстрел.
;   need_anti_zone, hero_Kelvin -> candidate_EtherealShift_anti_zone
(defrule r174
   (fact (name need_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name hero_Kelvin) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_EtherealShift_anti_zone (min ?c1 ?c2) 0.80 r174 "need_anti_zone hero_Kelvin" (+ 1 (max ?d1 ?d2)))
)

; Silence Glyph срывает Heavy Barrage.
;   need_anti_channel, hero_McGinnis -> candidate_SilenceGlyph_anti_channel
(defrule r175
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name hero_McGinnis) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_SilenceGlyph_anti_channel (min ?c1 ?c2) 0.80 r175 "need_anti_channel hero_McGinnis" (+ 1 (max ?d1 ?d2)))
)

; Curse срывает Singularity до сбора команды.
;   need_anti_channel, hero_Dynamo -> candidate_Curse_anti_channel
(defrule r176
   (fact (name need_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name hero_Dynamo) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Curse_anti_channel (min ?c1 ?c2) 0.80 r176 "need_anti_channel hero_Dynamo" (+ 1 (max ?d1 ?d2)))
)

; Sharpshooter отвечает на дальний урон.
;   need_anti_poke, hero_GreyTalon -> candidate_Sharpshooter_anti_poke
(defrule r177
   (fact (name need_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name hero_GreyTalon) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_Sharpshooter_anti_poke (min ?c1 ?c2) 0.80 r177 "need_anti_poke hero_GreyTalon" (+ 1 (max ?d1 ?d2)))
)

; Warp Stone уводит из-под Goo Ball.
;   need_anti_dive, hero_Viscous -> candidate_WarpStone_anti_dive
(defrule r178
   (fact (name need_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name hero_Viscous) (cf ?c2) (depth ?d2))
   =>
   (derive candidate_WarpStone_anti_dive (min ?c1 ?c2) 0.80 r178 "need_anti_dive hero_Viscous" (+ 1 (max ?d1 ?d2)))
)

;   candidate_MysticShot_damage_core -> pick_MysticShot_damage_core
(defrule r179
   (fact (name candidate_MysticShot_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_MysticShot_damage_core ?c1 0.80 r179 "candidate_MysticShot_damage_core" (+ 1 ?d1))
)

;   candidate_Berserker_damage_core -> pick_Berserker_damage_core
(defrule r180
   (fact (name candidate_Berserker_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Berserker_damage_core ?c1 0.80 r180 "candidate_Berserker_damage_core" (+ 1 ?d1))
)

;   candidate_ToxicBullets_anti_heal -> pick_ToxicBullets_anti_heal
(defrule r181
   (fact (name candidate_ToxicBullets_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ToxicBullets_anti_heal ?c1 0.80 r181 "candidate_ToxicBullets_anti_heal" (+ 1 ?d1))
)

;   candidate_ToxicBullets_anti_tank -> pick_ToxicBullets_anti_tank
(defrule r182
   (fact (name candidate_ToxicBullets_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ToxicBullets_anti_tank ?c1 0.80 r182 "candidate_ToxicBullets_anti_tank" (+ 1 ?d1))
)

;   candidate_Headhunter_anti_poke -> pick_Headhunter_anti_poke
(defrule r183
   (fact (name candidate_Headhunter_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Headhunter_anti_poke ?c1 0.80 r183 "candidate_Headhunter_anti_poke" (+ 1 ?d1))
)

;   candidate_Headhunter_damage_core -> pick_Headhunter_damage_core
(defrule r184
   (fact (name candidate_Headhunter_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Headhunter_damage_core ?c1 0.80 r184 "candidate_Headhunter_damage_core" (+ 1 ?d1))
)

;   candidate_EscalatingResilience_anti_bullet -> pick_EscalatingResilience_anti_bullet
(defrule r185
   (fact (name candidate_EscalatingResilience_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive pick_EscalatingResilience_anti_bullet ?c1 0.80 r185 "candidate_EscalatingResilience_anti_bullet" (+ 1 ?d1))
)

;   candidate_SlowingHex_anti_mobility -> pick_SlowingHex_anti_mobility
(defrule r186
   (fact (name candidate_SlowingHex_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SlowingHex_anti_mobility ?c1 0.80 r186 "candidate_SlowingHex_anti_mobility" (+ 1 ?d1))
)

;   candidate_SlowingHex_anti_dive -> pick_SlowingHex_anti_dive
(defrule r187
   (fact (name candidate_SlowingHex_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SlowingHex_anti_dive ?c1 0.80 r187 "candidate_SlowingHex_anti_dive" (+ 1 ?d1))
)

;   candidate_Sharpshooter_anti_poke -> pick_Sharpshooter_anti_poke
(defrule r188
   (fact (name candidate_Sharpshooter_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Sharpshooter_anti_poke ?c1 0.80 r188 "candidate_Sharpshooter_anti_poke" (+ 1 ?d1))
)

;   candidate_Sharpshooter_damage_core -> pick_Sharpshooter_damage_core
(defrule r189
   (fact (name candidate_Sharpshooter_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Sharpshooter_damage_core ?c1 0.80 r189 "candidate_Sharpshooter_damage_core" (+ 1 ?d1))
)

;   candidate_Inhibitor_anti_heal -> pick_Inhibitor_anti_heal
(defrule r190
   (fact (name candidate_Inhibitor_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Inhibitor_anti_heal ?c1 0.80 r190 "candidate_Inhibitor_anti_heal" (+ 1 ?d1))
)

;   candidate_Inhibitor_anti_mobility -> pick_Inhibitor_anti_mobility
(defrule r191
   (fact (name candidate_Inhibitor_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Inhibitor_anti_mobility ?c1 0.80 r191 "candidate_Inhibitor_anti_mobility" (+ 1 ?d1))
)

;   candidate_Inhibitor_anti_regen -> pick_Inhibitor_anti_regen
(defrule r192
   (fact (name candidate_Inhibitor_anti_regen) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Inhibitor_anti_regen ?c1 0.80 r192 "candidate_Inhibitor_anti_regen" (+ 1 ?d1))
)

;   candidate_CripplingHeadshot_anti_tank -> pick_CripplingHeadshot_anti_tank
(defrule r193
   (fact (name candidate_CripplingHeadshot_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive pick_CripplingHeadshot_anti_tank ?c1 0.80 r193 "candidate_CripplingHeadshot_anti_tank" (+ 1 ?d1))
)

;   candidate_HuntersAura_anti_tank -> pick_HuntersAura_anti_tank
(defrule r194
   (fact (name candidate_HuntersAura_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive pick_HuntersAura_anti_tank ?c1 0.80 r194 "candidate_HuntersAura_anti_tank" (+ 1 ?d1))
)

;   candidate_HuntersAura_anti_bullet -> pick_HuntersAura_anti_bullet
(defrule r195
   (fact (name candidate_HuntersAura_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive pick_HuntersAura_anti_bullet ?c1 0.80 r195 "candidate_HuntersAura_anti_bullet" (+ 1 ?d1))
)

;   candidate_Silencer_anti_channel -> pick_Silencer_anti_channel
(defrule r196
   (fact (name candidate_Silencer_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Silencer_anti_channel ?c1 0.80 r196 "candidate_Silencer_anti_channel" (+ 1 ?d1))
)

;   candidate_Silencer_anti_mobility -> pick_Silencer_anti_mobility
(defrule r197
   (fact (name candidate_Silencer_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Silencer_anti_mobility ?c1 0.80 r197 "candidate_Silencer_anti_mobility" (+ 1 ?d1))
)

;   candidate_SpiritualOverflow_damage_core -> pick_SpiritualOverflow_damage_core
(defrule r198
   (fact (name candidate_SpiritualOverflow_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SpiritualOverflow_damage_core ?c1 0.80 r198 "candidate_SpiritualOverflow_damage_core" (+ 1 ?d1))
)

;   candidate_ExtraStamina_anti_dive -> pick_ExtraStamina_anti_dive
(defrule r199
   (fact (name candidate_ExtraStamina_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ExtraStamina_anti_dive ?c1 0.80 r199 "candidate_ExtraStamina_anti_dive" (+ 1 ?d1))
)

;   candidate_HealingRite_sustain_self -> pick_HealingRite_sustain_self
(defrule r200
   (fact (name candidate_HealingRite_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive pick_HealingRite_sustain_self ?c1 0.80 r200 "candidate_HealingRite_sustain_self" (+ 1 ?d1))
)

;   candidate_BulletArmor_anti_bullet -> pick_BulletArmor_anti_bullet
(defrule r201
   (fact (name candidate_BulletArmor_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive pick_BulletArmor_anti_bullet ?c1 0.80 r201 "candidate_BulletArmor_anti_bullet" (+ 1 ?d1))
)

;   candidate_SpiritArmor_anti_magic -> pick_SpiritArmor_anti_magic
(defrule r202
   (fact (name candidate_SpiritArmor_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SpiritArmor_anti_magic ?c1 0.80 r202 "candidate_SpiritArmor_anti_magic" (+ 1 ?d1))
)

;   candidate_DebuffReducer_anti_dot -> pick_DebuffReducer_anti_dot
(defrule r203
   (fact (name candidate_DebuffReducer_anti_dot) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DebuffReducer_anti_dot ?c1 0.80 r203 "candidate_DebuffReducer_anti_dot" (+ 1 ?d1))
)

;   candidate_ReactiveBarrier_anti_cc -> pick_ReactiveBarrier_anti_cc
(defrule r204
   (fact (name candidate_ReactiveBarrier_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ReactiveBarrier_anti_cc ?c1 0.80 r204 "candidate_ReactiveBarrier_anti_cc" (+ 1 ?d1))
)

;   candidate_ReactiveBarrier_anti_magic -> pick_ReactiveBarrier_anti_magic
(defrule r205
   (fact (name candidate_ReactiveBarrier_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ReactiveBarrier_anti_magic ?c1 0.80 r205 "candidate_ReactiveBarrier_anti_magic" (+ 1 ?d1))
)

;   candidate_DivineBarrier_anti_magic -> pick_DivineBarrier_anti_magic
(defrule r206
   (fact (name candidate_DivineBarrier_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DivineBarrier_anti_magic ?c1 0.80 r206 "candidate_DivineBarrier_anti_magic" (+ 1 ?d1))
)

;   candidate_DivineBarrier_anti_execute -> pick_DivineBarrier_anti_execute
(defrule r207
   (fact (name candidate_DivineBarrier_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DivineBarrier_anti_execute ?c1 0.80 r207 "candidate_DivineBarrier_anti_execute" (+ 1 ?d1))
)

;   candidate_MetalSkin_anti_bullet -> pick_MetalSkin_anti_bullet
(defrule r208
   (fact (name candidate_MetalSkin_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive pick_MetalSkin_anti_bullet ?c1 0.80 r208 "candidate_MetalSkin_anti_bullet" (+ 1 ?d1))
)

;   candidate_MetalSkin_anti_channel -> pick_MetalSkin_anti_channel
(defrule r209
   (fact (name candidate_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_MetalSkin_anti_channel ?c1 0.80 r209 "candidate_MetalSkin_anti_channel" (+ 1 ?d1))
)

;   candidate_ReturnFire_anti_bullet -> pick_ReturnFire_anti_bullet
(defrule r210
   (fact (name candidate_ReturnFire_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive pick_ReturnFire_anti_bullet ?c1 0.80 r210 "candidate_ReturnFire_anti_bullet" (+ 1 ?d1))
)

;   candidate_SuperiorStamina_anti_dive -> pick_SuperiorStamina_anti_dive
(defrule r211
   (fact (name candidate_SuperiorStamina_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SuperiorStamina_anti_dive ?c1 0.80 r211 "candidate_SuperiorStamina_anti_dive" (+ 1 ?d1))
)

;   candidate_Fortitude_anti_poke -> pick_Fortitude_anti_poke
(defrule r212
   (fact (name candidate_Fortitude_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Fortitude_anti_poke ?c1 0.80 r212 "candidate_Fortitude_anti_poke" (+ 1 ?d1))
)

;   candidate_Fortitude_sustain_self -> pick_Fortitude_sustain_self
(defrule r213
   (fact (name candidate_Fortitude_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Fortitude_sustain_self ?c1 0.80 r213 "candidate_Fortitude_sustain_self" (+ 1 ?d1))
)

;   candidate_DebuffRemover_anti_dot -> pick_DebuffRemover_anti_dot
(defrule r214
   (fact (name candidate_DebuffRemover_anti_dot) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DebuffRemover_anti_dot ?c1 0.80 r214 "candidate_DebuffRemover_anti_dot" (+ 1 ?d1))
)

;   candidate_DebuffRemover_anti_cc -> pick_DebuffRemover_anti_cc
(defrule r215
   (fact (name candidate_DebuffRemover_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DebuffRemover_anti_cc ?c1 0.80 r215 "candidate_DebuffRemover_anti_cc" (+ 1 ?d1))
)

;   candidate_Colossus_anti_dive -> pick_Colossus_anti_dive
(defrule r216
   (fact (name candidate_Colossus_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Colossus_anti_dive ?c1 0.80 r216 "candidate_Colossus_anti_dive" (+ 1 ?d1))
)

;   candidate_Colossus_sustain_self -> pick_Colossus_sustain_self
(defrule r217
   (fact (name candidate_Colossus_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Colossus_sustain_self ?c1 0.80 r217 "candidate_Colossus_sustain_self" (+ 1 ?d1))
)

;   candidate_SoulRebirth_anti_execute -> pick_SoulRebirth_anti_execute
(defrule r218
   (fact (name candidate_SoulRebirth_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SoulRebirth_anti_execute ?c1 0.80 r218 "candidate_SoulRebirth_anti_execute" (+ 1 ?d1))
)

;   candidate_DivinersKevlar_anti_magic -> pick_DivinersKevlar_anti_magic
(defrule r219
   (fact (name candidate_DivinersKevlar_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DivinersKevlar_anti_magic ?c1 0.80 r219 "candidate_DivinersKevlar_anti_magic" (+ 1 ?d1))
)

;   candidate_DivinersKevlar_anti_cc -> pick_DivinersKevlar_anti_cc
(defrule r220
   (fact (name candidate_DivinersKevlar_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DivinersKevlar_anti_cc ?c1 0.80 r220 "candidate_DivinersKevlar_anti_cc" (+ 1 ?d1))
)

;   candidate_DivinersKevlar_anti_disable -> pick_DivinersKevlar_anti_disable
(defrule r221
   (fact (name candidate_DivinersKevlar_anti_disable) (cf ?c1) (depth ?d1))
   =>
   (derive pick_DivinersKevlar_anti_disable ?c1 0.80 r221 "candidate_DivinersKevlar_anti_disable" (+ 1 ?d1))
)

;   candidate_Leech_sustain_self -> pick_Leech_sustain_self
(defrule r222
   (fact (name candidate_Leech_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Leech_sustain_self ?c1 0.80 r222 "candidate_Leech_sustain_self" (+ 1 ?d1))
)

;   candidate_MysticBurst_damage_core -> pick_MysticBurst_damage_core
(defrule r223
   (fact (name candidate_MysticBurst_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_MysticBurst_damage_core ?c1 0.80 r223 "candidate_MysticBurst_damage_core" (+ 1 ?d1))
)

;   candidate_Knockdown_anti_channel -> pick_Knockdown_anti_channel
(defrule r224
   (fact (name candidate_Knockdown_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Knockdown_anti_channel ?c1 0.80 r224 "candidate_Knockdown_anti_channel" (+ 1 ?d1))
)

;   candidate_Knockdown_anti_poke -> pick_Knockdown_anti_poke
(defrule r225
   (fact (name candidate_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Knockdown_anti_poke ?c1 0.80 r225 "candidate_Knockdown_anti_poke" (+ 1 ?d1))
)

;   candidate_EtherealShift_anti_execute -> pick_EtherealShift_anti_execute
(defrule r226
   (fact (name candidate_EtherealShift_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive pick_EtherealShift_anti_execute ?c1 0.80 r226 "candidate_EtherealShift_anti_execute" (+ 1 ?d1))
)

;   candidate_EtherealShift_anti_zone -> pick_EtherealShift_anti_zone
(defrule r227
   (fact (name candidate_EtherealShift_anti_zone) (cf ?c1) (depth ?d1))
   =>
   (derive pick_EtherealShift_anti_zone ?c1 0.80 r227 "candidate_EtherealShift_anti_zone" (+ 1 ?d1))
)

;   candidate_WarpStone_anti_dive -> pick_WarpStone_anti_dive
(defrule r228
   (fact (name candidate_WarpStone_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive pick_WarpStone_anti_dive ?c1 0.80 r228 "candidate_WarpStone_anti_dive" (+ 1 ?d1))
)

;   candidate_WarpStone_anti_zone -> pick_WarpStone_anti_zone
(defrule r229
   (fact (name candidate_WarpStone_anti_zone) (cf ?c1) (depth ?d1))
   =>
   (derive pick_WarpStone_anti_zone ?c1 0.80 r229 "candidate_WarpStone_anti_zone" (+ 1 ?d1))
)

;   candidate_SilenceGlyph_anti_channel -> pick_SilenceGlyph_anti_channel
(defrule r230
   (fact (name candidate_SilenceGlyph_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SilenceGlyph_anti_channel ?c1 0.80 r230 "candidate_SilenceGlyph_anti_channel" (+ 1 ?d1))
)

;   candidate_SilenceGlyph_anti_mobility -> pick_SilenceGlyph_anti_mobility
(defrule r231
   (fact (name candidate_SilenceGlyph_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SilenceGlyph_anti_mobility ?c1 0.80 r231 "candidate_SilenceGlyph_anti_mobility" (+ 1 ?d1))
)

;   candidate_SilenceGlyph_anti_invis -> pick_SilenceGlyph_anti_invis
(defrule r232
   (fact (name candidate_SilenceGlyph_anti_invis) (cf ?c1) (depth ?d1))
   =>
   (derive pick_SilenceGlyph_anti_invis ?c1 0.80 r232 "candidate_SilenceGlyph_anti_invis" (+ 1 ?d1))
)

;   candidate_EscalatingExposure_anti_tank -> pick_EscalatingExposure_anti_tank
(defrule r233
   (fact (name candidate_EscalatingExposure_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive pick_EscalatingExposure_anti_tank ?c1 0.80 r233 "candidate_EscalatingExposure_anti_tank" (+ 1 ?d1))
)

;   candidate_Curse_anti_channel -> pick_Curse_anti_channel
(defrule r234
   (fact (name candidate_Curse_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Curse_anti_channel ?c1 0.80 r234 "candidate_Curse_anti_channel" (+ 1 ?d1))
)

;   candidate_Curse_anti_mobility -> pick_Curse_anti_mobility
(defrule r235
   (fact (name candidate_Curse_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Curse_anti_mobility ?c1 0.80 r235 "candidate_Curse_anti_mobility" (+ 1 ?d1))
)

;   candidate_Curse_anti_heal -> pick_Curse_anti_heal
(defrule r236
   (fact (name candidate_Curse_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Curse_anti_heal ?c1 0.80 r236 "candidate_Curse_anti_heal" (+ 1 ?d1))
)

;   candidate_Curse_anti_immunity -> pick_Curse_anti_immunity
(defrule r237
   (fact (name candidate_Curse_anti_immunity) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Curse_anti_immunity ?c1 0.80 r237 "candidate_Curse_anti_immunity" (+ 1 ?d1))
)

;   candidate_Curse_anti_purge -> pick_Curse_anti_purge
(defrule r238
   (fact (name candidate_Curse_anti_purge) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Curse_anti_purge ?c1 0.80 r238 "candidate_Curse_anti_purge" (+ 1 ?d1))
)

;   candidate_Unstoppable_anti_cc -> pick_Unstoppable_anti_cc
(defrule r239
   (fact (name candidate_Unstoppable_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Unstoppable_anti_cc ?c1 0.80 r239 "candidate_Unstoppable_anti_cc" (+ 1 ?d1))
)

;   candidate_Unstoppable_anti_silence -> pick_Unstoppable_anti_silence
(defrule r240
   (fact (name candidate_Unstoppable_anti_silence) (cf ?c1) (depth ?d1))
   =>
   (derive pick_Unstoppable_anti_silence ?c1 0.80 r240 "candidate_Unstoppable_anti_silence" (+ 1 ?d1))
)

;   candidate_BoundlessSpirit_damage_core -> pick_BoundlessSpirit_damage_core
(defrule r241
   (fact (name candidate_BoundlessSpirit_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive pick_BoundlessSpirit_damage_core ?c1 0.80 r241 "candidate_BoundlessSpirit_damage_core" (+ 1 ?d1))
)

;   candidate_BoundlessSpirit_sustain_self -> pick_BoundlessSpirit_sustain_self
(defrule r242
   (fact (name candidate_BoundlessSpirit_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive pick_BoundlessSpirit_sustain_self ?c1 0.80 r242 "candidate_BoundlessSpirit_sustain_self" (+ 1 ?d1))
)

;   pick_MysticShot_damage_core -> recommended_MysticShot_damage_core
(defrule r243
   (fact (name pick_MysticShot_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MysticShot_damage_core ?c1 0.75 r243 "pick_MysticShot_damage_core" (+ 1 ?d1))
)

;   pick_Berserker_damage_core -> recommended_Berserker_damage_core
(defrule r244
   (fact (name pick_Berserker_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Berserker_damage_core ?c1 0.75 r244 "pick_Berserker_damage_core" (+ 1 ?d1))
)

;   pick_ToxicBullets_anti_heal -> recommended_ToxicBullets_anti_heal
(defrule r245
   (fact (name pick_ToxicBullets_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ToxicBullets_anti_heal ?c1 0.75 r245 "pick_ToxicBullets_anti_heal" (+ 1 ?d1))
)

;   pick_ToxicBullets_anti_tank -> recommended_ToxicBullets_anti_tank
(defrule r246
   (fact (name pick_ToxicBullets_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ToxicBullets_anti_tank ?c1 0.75 r246 "pick_ToxicBullets_anti_tank" (+ 1 ?d1))
)

;   pick_Headhunter_anti_poke -> recommended_Headhunter_anti_poke
(defrule r247
   (fact (name pick_Headhunter_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Headhunter_anti_poke ?c1 0.75 r247 "pick_Headhunter_anti_poke" (+ 1 ?d1))
)

;   pick_Headhunter_damage_core -> recommended_Headhunter_damage_core
(defrule r248
   (fact (name pick_Headhunter_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Headhunter_damage_core ?c1 0.75 r248 "pick_Headhunter_damage_core" (+ 1 ?d1))
)

;   pick_EscalatingResilience_anti_bullet -> recommended_EscalatingResilience_anti_bullet
(defrule r249
   (fact (name pick_EscalatingResilience_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_EscalatingResilience_anti_bullet ?c1 0.75 r249 "pick_EscalatingResilience_anti_bullet" (+ 1 ?d1))
)

;   pick_SlowingHex_anti_mobility -> recommended_SlowingHex_anti_mobility
(defrule r250
   (fact (name pick_SlowingHex_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SlowingHex_anti_mobility ?c1 0.75 r250 "pick_SlowingHex_anti_mobility" (+ 1 ?d1))
)

;   pick_SlowingHex_anti_dive -> recommended_SlowingHex_anti_dive
(defrule r251
   (fact (name pick_SlowingHex_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SlowingHex_anti_dive ?c1 0.75 r251 "pick_SlowingHex_anti_dive" (+ 1 ?d1))
)

;   pick_Sharpshooter_anti_poke -> recommended_Sharpshooter_anti_poke
(defrule r252
   (fact (name pick_Sharpshooter_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Sharpshooter_anti_poke ?c1 0.75 r252 "pick_Sharpshooter_anti_poke" (+ 1 ?d1))
)

;   pick_Sharpshooter_damage_core -> recommended_Sharpshooter_damage_core
(defrule r253
   (fact (name pick_Sharpshooter_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Sharpshooter_damage_core ?c1 0.75 r253 "pick_Sharpshooter_damage_core" (+ 1 ?d1))
)

;   pick_Inhibitor_anti_heal -> recommended_Inhibitor_anti_heal
(defrule r254
   (fact (name pick_Inhibitor_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Inhibitor_anti_heal ?c1 0.75 r254 "pick_Inhibitor_anti_heal" (+ 1 ?d1))
)

;   pick_Inhibitor_anti_mobility -> recommended_Inhibitor_anti_mobility
(defrule r255
   (fact (name pick_Inhibitor_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Inhibitor_anti_mobility ?c1 0.75 r255 "pick_Inhibitor_anti_mobility" (+ 1 ?d1))
)

;   pick_Inhibitor_anti_regen -> recommended_Inhibitor_anti_regen
(defrule r256
   (fact (name pick_Inhibitor_anti_regen) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Inhibitor_anti_regen ?c1 0.75 r256 "pick_Inhibitor_anti_regen" (+ 1 ?d1))
)

;   pick_CripplingHeadshot_anti_tank -> recommended_CripplingHeadshot_anti_tank
(defrule r257
   (fact (name pick_CripplingHeadshot_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_CripplingHeadshot_anti_tank ?c1 0.75 r257 "pick_CripplingHeadshot_anti_tank" (+ 1 ?d1))
)

;   pick_HuntersAura_anti_tank -> recommended_HuntersAura_anti_tank
(defrule r258
   (fact (name pick_HuntersAura_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_HuntersAura_anti_tank ?c1 0.75 r258 "pick_HuntersAura_anti_tank" (+ 1 ?d1))
)

;   pick_HuntersAura_anti_bullet -> recommended_HuntersAura_anti_bullet
(defrule r259
   (fact (name pick_HuntersAura_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_HuntersAura_anti_bullet ?c1 0.75 r259 "pick_HuntersAura_anti_bullet" (+ 1 ?d1))
)

;   pick_Silencer_anti_channel -> recommended_Silencer_anti_channel
(defrule r260
   (fact (name pick_Silencer_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Silencer_anti_channel ?c1 0.75 r260 "pick_Silencer_anti_channel" (+ 1 ?d1))
)

;   pick_Silencer_anti_mobility -> recommended_Silencer_anti_mobility
(defrule r261
   (fact (name pick_Silencer_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Silencer_anti_mobility ?c1 0.75 r261 "pick_Silencer_anti_mobility" (+ 1 ?d1))
)

;   pick_SpiritualOverflow_damage_core -> recommended_SpiritualOverflow_damage_core
(defrule r262
   (fact (name pick_SpiritualOverflow_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SpiritualOverflow_damage_core ?c1 0.75 r262 "pick_SpiritualOverflow_damage_core" (+ 1 ?d1))
)

;   pick_ExtraStamina_anti_dive -> recommended_ExtraStamina_anti_dive
(defrule r263
   (fact (name pick_ExtraStamina_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ExtraStamina_anti_dive ?c1 0.75 r263 "pick_ExtraStamina_anti_dive" (+ 1 ?d1))
)

;   pick_HealingRite_sustain_self -> recommended_HealingRite_sustain_self
(defrule r264
   (fact (name pick_HealingRite_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_HealingRite_sustain_self ?c1 0.75 r264 "pick_HealingRite_sustain_self" (+ 1 ?d1))
)

;   pick_BulletArmor_anti_bullet -> recommended_BulletArmor_anti_bullet
(defrule r265
   (fact (name pick_BulletArmor_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_BulletArmor_anti_bullet ?c1 0.75 r265 "pick_BulletArmor_anti_bullet" (+ 1 ?d1))
)

;   pick_SpiritArmor_anti_magic -> recommended_SpiritArmor_anti_magic
(defrule r266
   (fact (name pick_SpiritArmor_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SpiritArmor_anti_magic ?c1 0.75 r266 "pick_SpiritArmor_anti_magic" (+ 1 ?d1))
)

;   pick_DebuffReducer_anti_dot -> recommended_DebuffReducer_anti_dot
(defrule r267
   (fact (name pick_DebuffReducer_anti_dot) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DebuffReducer_anti_dot ?c1 0.75 r267 "pick_DebuffReducer_anti_dot" (+ 1 ?d1))
)

;   pick_ReactiveBarrier_anti_cc -> recommended_ReactiveBarrier_anti_cc
(defrule r268
   (fact (name pick_ReactiveBarrier_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ReactiveBarrier_anti_cc ?c1 0.75 r268 "pick_ReactiveBarrier_anti_cc" (+ 1 ?d1))
)

;   pick_ReactiveBarrier_anti_magic -> recommended_ReactiveBarrier_anti_magic
(defrule r269
   (fact (name pick_ReactiveBarrier_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ReactiveBarrier_anti_magic ?c1 0.75 r269 "pick_ReactiveBarrier_anti_magic" (+ 1 ?d1))
)

;   pick_DivineBarrier_anti_magic -> recommended_DivineBarrier_anti_magic
(defrule r270
   (fact (name pick_DivineBarrier_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DivineBarrier_anti_magic ?c1 0.75 r270 "pick_DivineBarrier_anti_magic" (+ 1 ?d1))
)

;   pick_DivineBarrier_anti_execute -> recommended_DivineBarrier_anti_execute
(defrule r271
   (fact (name pick_DivineBarrier_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DivineBarrier_anti_execute ?c1 0.75 r271 "pick_DivineBarrier_anti_execute" (+ 1 ?d1))
)

;   pick_MetalSkin_anti_bullet -> recommended_MetalSkin_anti_bullet
(defrule r272
   (fact (name pick_MetalSkin_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MetalSkin_anti_bullet ?c1 0.75 r272 "pick_MetalSkin_anti_bullet" (+ 1 ?d1))
)

;   pick_MetalSkin_anti_channel -> recommended_MetalSkin_anti_channel
(defrule r273
   (fact (name pick_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MetalSkin_anti_channel ?c1 0.75 r273 "pick_MetalSkin_anti_channel" (+ 1 ?d1))
)

;   pick_ReturnFire_anti_bullet -> recommended_ReturnFire_anti_bullet
(defrule r274
   (fact (name pick_ReturnFire_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_ReturnFire_anti_bullet ?c1 0.75 r274 "pick_ReturnFire_anti_bullet" (+ 1 ?d1))
)

;   pick_SuperiorStamina_anti_dive -> recommended_SuperiorStamina_anti_dive
(defrule r275
   (fact (name pick_SuperiorStamina_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SuperiorStamina_anti_dive ?c1 0.75 r275 "pick_SuperiorStamina_anti_dive" (+ 1 ?d1))
)

;   pick_Fortitude_anti_poke -> recommended_Fortitude_anti_poke
(defrule r276
   (fact (name pick_Fortitude_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Fortitude_anti_poke ?c1 0.75 r276 "pick_Fortitude_anti_poke" (+ 1 ?d1))
)

;   pick_Fortitude_sustain_self -> recommended_Fortitude_sustain_self
(defrule r277
   (fact (name pick_Fortitude_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Fortitude_sustain_self ?c1 0.75 r277 "pick_Fortitude_sustain_self" (+ 1 ?d1))
)

;   pick_DebuffRemover_anti_dot -> recommended_DebuffRemover_anti_dot
(defrule r278
   (fact (name pick_DebuffRemover_anti_dot) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DebuffRemover_anti_dot ?c1 0.75 r278 "pick_DebuffRemover_anti_dot" (+ 1 ?d1))
)

;   pick_DebuffRemover_anti_cc -> recommended_DebuffRemover_anti_cc
(defrule r279
   (fact (name pick_DebuffRemover_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DebuffRemover_anti_cc ?c1 0.75 r279 "pick_DebuffRemover_anti_cc" (+ 1 ?d1))
)

;   pick_Colossus_anti_dive -> recommended_Colossus_anti_dive
(defrule r280
   (fact (name pick_Colossus_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Colossus_anti_dive ?c1 0.75 r280 "pick_Colossus_anti_dive" (+ 1 ?d1))
)

;   pick_Colossus_sustain_self -> recommended_Colossus_sustain_self
(defrule r281
   (fact (name pick_Colossus_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Colossus_sustain_self ?c1 0.75 r281 "pick_Colossus_sustain_self" (+ 1 ?d1))
)

;   pick_SoulRebirth_anti_execute -> recommended_SoulRebirth_anti_execute
(defrule r282
   (fact (name pick_SoulRebirth_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SoulRebirth_anti_execute ?c1 0.75 r282 "pick_SoulRebirth_anti_execute" (+ 1 ?d1))
)

;   pick_DivinersKevlar_anti_magic -> recommended_DivinersKevlar_anti_magic
(defrule r283
   (fact (name pick_DivinersKevlar_anti_magic) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DivinersKevlar_anti_magic ?c1 0.75 r283 "pick_DivinersKevlar_anti_magic" (+ 1 ?d1))
)

;   pick_DivinersKevlar_anti_cc -> recommended_DivinersKevlar_anti_cc
(defrule r284
   (fact (name pick_DivinersKevlar_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DivinersKevlar_anti_cc ?c1 0.75 r284 "pick_DivinersKevlar_anti_cc" (+ 1 ?d1))
)

;   pick_DivinersKevlar_anti_disable -> recommended_DivinersKevlar_anti_disable
(defrule r285
   (fact (name pick_DivinersKevlar_anti_disable) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_DivinersKevlar_anti_disable ?c1 0.75 r285 "pick_DivinersKevlar_anti_disable" (+ 1 ?d1))
)

;   pick_Leech_sustain_self -> recommended_Leech_sustain_self
(defrule r286
   (fact (name pick_Leech_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Leech_sustain_self ?c1 0.75 r286 "pick_Leech_sustain_self" (+ 1 ?d1))
)

;   pick_MysticBurst_damage_core -> recommended_MysticBurst_damage_core
(defrule r287
   (fact (name pick_MysticBurst_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_MysticBurst_damage_core ?c1 0.75 r287 "pick_MysticBurst_damage_core" (+ 1 ?d1))
)

;   pick_Knockdown_anti_channel -> recommended_Knockdown_anti_channel
(defrule r288
   (fact (name pick_Knockdown_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Knockdown_anti_channel ?c1 0.75 r288 "pick_Knockdown_anti_channel" (+ 1 ?d1))
)

;   pick_Knockdown_anti_poke -> recommended_Knockdown_anti_poke
(defrule r289
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Knockdown_anti_poke ?c1 0.75 r289 "pick_Knockdown_anti_poke" (+ 1 ?d1))
)

;   pick_EtherealShift_anti_execute -> recommended_EtherealShift_anti_execute
(defrule r290
   (fact (name pick_EtherealShift_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_EtherealShift_anti_execute ?c1 0.75 r290 "pick_EtherealShift_anti_execute" (+ 1 ?d1))
)

;   pick_EtherealShift_anti_zone -> recommended_EtherealShift_anti_zone
(defrule r291
   (fact (name pick_EtherealShift_anti_zone) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_EtherealShift_anti_zone ?c1 0.75 r291 "pick_EtherealShift_anti_zone" (+ 1 ?d1))
)

;   pick_WarpStone_anti_dive -> recommended_WarpStone_anti_dive
(defrule r292
   (fact (name pick_WarpStone_anti_dive) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_WarpStone_anti_dive ?c1 0.75 r292 "pick_WarpStone_anti_dive" (+ 1 ?d1))
)

;   pick_WarpStone_anti_zone -> recommended_WarpStone_anti_zone
(defrule r293
   (fact (name pick_WarpStone_anti_zone) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_WarpStone_anti_zone ?c1 0.75 r293 "pick_WarpStone_anti_zone" (+ 1 ?d1))
)

;   pick_SilenceGlyph_anti_channel -> recommended_SilenceGlyph_anti_channel
(defrule r294
   (fact (name pick_SilenceGlyph_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SilenceGlyph_anti_channel ?c1 0.75 r294 "pick_SilenceGlyph_anti_channel" (+ 1 ?d1))
)

;   pick_SilenceGlyph_anti_mobility -> recommended_SilenceGlyph_anti_mobility
(defrule r295
   (fact (name pick_SilenceGlyph_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SilenceGlyph_anti_mobility ?c1 0.75 r295 "pick_SilenceGlyph_anti_mobility" (+ 1 ?d1))
)

;   pick_SilenceGlyph_anti_invis -> recommended_SilenceGlyph_anti_invis
(defrule r296
   (fact (name pick_SilenceGlyph_anti_invis) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_SilenceGlyph_anti_invis ?c1 0.75 r296 "pick_SilenceGlyph_anti_invis" (+ 1 ?d1))
)

;   pick_EscalatingExposure_anti_tank -> recommended_EscalatingExposure_anti_tank
(defrule r297
   (fact (name pick_EscalatingExposure_anti_tank) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_EscalatingExposure_anti_tank ?c1 0.75 r297 "pick_EscalatingExposure_anti_tank" (+ 1 ?d1))
)

;   pick_Curse_anti_channel -> recommended_Curse_anti_channel
(defrule r298
   (fact (name pick_Curse_anti_channel) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Curse_anti_channel ?c1 0.75 r298 "pick_Curse_anti_channel" (+ 1 ?d1))
)

;   pick_Curse_anti_mobility -> recommended_Curse_anti_mobility
(defrule r299
   (fact (name pick_Curse_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Curse_anti_mobility ?c1 0.75 r299 "pick_Curse_anti_mobility" (+ 1 ?d1))
)

;   pick_Curse_anti_heal -> recommended_Curse_anti_heal
(defrule r300
   (fact (name pick_Curse_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Curse_anti_heal ?c1 0.75 r300 "pick_Curse_anti_heal" (+ 1 ?d1))
)

;   pick_Curse_anti_immunity -> recommended_Curse_anti_immunity
(defrule r301
   (fact (name pick_Curse_anti_immunity) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Curse_anti_immunity ?c1 0.75 r301 "pick_Curse_anti_immunity" (+ 1 ?d1))
)

;   pick_Curse_anti_purge -> recommended_Curse_anti_purge
(defrule r302
   (fact (name pick_Curse_anti_purge) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Curse_anti_purge ?c1 0.75 r302 "pick_Curse_anti_purge" (+ 1 ?d1))
)

;   pick_Unstoppable_anti_cc -> recommended_Unstoppable_anti_cc
(defrule r303
   (fact (name pick_Unstoppable_anti_cc) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Unstoppable_anti_cc ?c1 0.75 r303 "pick_Unstoppable_anti_cc" (+ 1 ?d1))
)

;   pick_Unstoppable_anti_silence -> recommended_Unstoppable_anti_silence
(defrule r304
   (fact (name pick_Unstoppable_anti_silence) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_Unstoppable_anti_silence ?c1 0.75 r304 "pick_Unstoppable_anti_silence" (+ 1 ?d1))
)

;   pick_BoundlessSpirit_damage_core -> recommended_BoundlessSpirit_damage_core
(defrule r305
   (fact (name pick_BoundlessSpirit_damage_core) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_BoundlessSpirit_damage_core ?c1 0.75 r305 "pick_BoundlessSpirit_damage_core" (+ 1 ?d1))
)

;   pick_BoundlessSpirit_sustain_self -> recommended_BoundlessSpirit_sustain_self
(defrule r306
   (fact (name pick_BoundlessSpirit_sustain_self) (cf ?c1) (depth ?d1))
   =>
   (derive recommended_BoundlessSpirit_sustain_self ?c1 0.75 r306 "pick_BoundlessSpirit_sustain_self" (+ 1 ?d1))
)

;   pick_SlowingHex_anti_mobility, item_active_SlowingHex -> active_pick_SlowingHex_anti_mobility
(defrule r307
   (fact (name pick_SlowingHex_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_active_SlowingHex) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_SlowingHex_anti_mobility (min ?c1 ?c2) 0.95 r307 "pick_SlowingHex_anti_mobility item_active_SlowingHex" (+ 1 (max ?d1 ?d2)))
)

;   pick_SlowingHex_anti_dive, item_active_SlowingHex -> active_pick_SlowingHex_anti_dive
(defrule r308
   (fact (name pick_SlowingHex_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_active_SlowingHex) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_SlowingHex_anti_dive (min ?c1 ?c2) 0.95 r308 "pick_SlowingHex_anti_dive item_active_SlowingHex" (+ 1 (max ?d1 ?d2)))
)

;   pick_Silencer_anti_channel, item_active_Silencer -> active_pick_Silencer_anti_channel
(defrule r309
   (fact (name pick_Silencer_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_active_Silencer) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Silencer_anti_channel (min ?c1 ?c2) 0.95 r309 "pick_Silencer_anti_channel item_active_Silencer" (+ 1 (max ?d1 ?d2)))
)

;   pick_Silencer_anti_mobility, item_active_Silencer -> active_pick_Silencer_anti_mobility
(defrule r310
   (fact (name pick_Silencer_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_active_Silencer) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Silencer_anti_mobility (min ?c1 ?c2) 0.95 r310 "pick_Silencer_anti_mobility item_active_Silencer" (+ 1 (max ?d1 ?d2)))
)

;   pick_HealingRite_sustain_self, item_active_HealingRite -> active_pick_HealingRite_sustain_self
(defrule r311
   (fact (name pick_HealingRite_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_active_HealingRite) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_HealingRite_sustain_self (min ?c1 ?c2) 0.95 r311 "pick_HealingRite_sustain_self item_active_HealingRite" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReactiveBarrier_anti_cc, item_active_ReactiveBarrier -> active_pick_ReactiveBarrier_anti_cc
(defrule r312
   (fact (name pick_ReactiveBarrier_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_active_ReactiveBarrier) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_ReactiveBarrier_anti_cc (min ?c1 ?c2) 0.95 r312 "pick_ReactiveBarrier_anti_cc item_active_ReactiveBarrier" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReactiveBarrier_anti_magic, item_active_ReactiveBarrier -> active_pick_ReactiveBarrier_anti_magic
(defrule r313
   (fact (name pick_ReactiveBarrier_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_active_ReactiveBarrier) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_ReactiveBarrier_anti_magic (min ?c1 ?c2) 0.95 r313 "pick_ReactiveBarrier_anti_magic item_active_ReactiveBarrier" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivineBarrier_anti_magic, item_active_DivineBarrier -> active_pick_DivineBarrier_anti_magic
(defrule r314
   (fact (name pick_DivineBarrier_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_active_DivineBarrier) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_DivineBarrier_anti_magic (min ?c1 ?c2) 0.95 r314 "pick_DivineBarrier_anti_magic item_active_DivineBarrier" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivineBarrier_anti_execute, item_active_DivineBarrier -> active_pick_DivineBarrier_anti_execute
(defrule r315
   (fact (name pick_DivineBarrier_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_active_DivineBarrier) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_DivineBarrier_anti_execute (min ?c1 ?c2) 0.95 r315 "pick_DivineBarrier_anti_execute item_active_DivineBarrier" (+ 1 (max ?d1 ?d2)))
)

;   pick_MetalSkin_anti_bullet, item_active_MetalSkin -> active_pick_MetalSkin_anti_bullet
(defrule r316
   (fact (name pick_MetalSkin_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_active_MetalSkin) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_MetalSkin_anti_bullet (min ?c1 ?c2) 0.95 r316 "pick_MetalSkin_anti_bullet item_active_MetalSkin" (+ 1 (max ?d1 ?d2)))
)

;   pick_MetalSkin_anti_channel, item_active_MetalSkin -> active_pick_MetalSkin_anti_channel
(defrule r317
   (fact (name pick_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_active_MetalSkin) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_MetalSkin_anti_channel (min ?c1 ?c2) 0.95 r317 "pick_MetalSkin_anti_channel item_active_MetalSkin" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReturnFire_anti_bullet, item_active_ReturnFire -> active_pick_ReturnFire_anti_bullet
(defrule r318
   (fact (name pick_ReturnFire_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_active_ReturnFire) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_ReturnFire_anti_bullet (min ?c1 ?c2) 0.95 r318 "pick_ReturnFire_anti_bullet item_active_ReturnFire" (+ 1 (max ?d1 ?d2)))
)

;   pick_DebuffRemover_anti_dot, item_active_DebuffRemover -> active_pick_DebuffRemover_anti_dot
(defrule r319
   (fact (name pick_DebuffRemover_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name item_active_DebuffRemover) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_DebuffRemover_anti_dot (min ?c1 ?c2) 0.95 r319 "pick_DebuffRemover_anti_dot item_active_DebuffRemover" (+ 1 (max ?d1 ?d2)))
)

;   pick_DebuffRemover_anti_cc, item_active_DebuffRemover -> active_pick_DebuffRemover_anti_cc
(defrule r320
   (fact (name pick_DebuffRemover_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_active_DebuffRemover) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_DebuffRemover_anti_cc (min ?c1 ?c2) 0.95 r320 "pick_DebuffRemover_anti_cc item_active_DebuffRemover" (+ 1 (max ?d1 ?d2)))
)

;   pick_Colossus_anti_dive, item_active_Colossus -> active_pick_Colossus_anti_dive
(defrule r321
   (fact (name pick_Colossus_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_active_Colossus) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Colossus_anti_dive (min ?c1 ?c2) 0.95 r321 "pick_Colossus_anti_dive item_active_Colossus" (+ 1 (max ?d1 ?d2)))
)

;   pick_Colossus_sustain_self, item_active_Colossus -> active_pick_Colossus_sustain_self
(defrule r322
   (fact (name pick_Colossus_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_active_Colossus) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Colossus_sustain_self (min ?c1 ?c2) 0.95 r322 "pick_Colossus_sustain_self item_active_Colossus" (+ 1 (max ?d1 ?d2)))
)

;   pick_Knockdown_anti_channel, item_active_Knockdown -> active_pick_Knockdown_anti_channel
(defrule r323
   (fact (name pick_Knockdown_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_active_Knockdown) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Knockdown_anti_channel (min ?c1 ?c2) 0.95 r323 "pick_Knockdown_anti_channel item_active_Knockdown" (+ 1 (max ?d1 ?d2)))
)

;   pick_Knockdown_anti_poke, item_active_Knockdown -> active_pick_Knockdown_anti_poke
(defrule r324
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_active_Knockdown) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Knockdown_anti_poke (min ?c1 ?c2) 0.95 r324 "pick_Knockdown_anti_poke item_active_Knockdown" (+ 1 (max ?d1 ?d2)))
)

;   pick_EtherealShift_anti_execute, item_active_EtherealShift -> active_pick_EtherealShift_anti_execute
(defrule r325
   (fact (name pick_EtherealShift_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_active_EtherealShift) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_EtherealShift_anti_execute (min ?c1 ?c2) 0.95 r325 "pick_EtherealShift_anti_execute item_active_EtherealShift" (+ 1 (max ?d1 ?d2)))
)

;   pick_EtherealShift_anti_zone, item_active_EtherealShift -> active_pick_EtherealShift_anti_zone
(defrule r326
   (fact (name pick_EtherealShift_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_active_EtherealShift) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_EtherealShift_anti_zone (min ?c1 ?c2) 0.95 r326 "pick_EtherealShift_anti_zone item_active_EtherealShift" (+ 1 (max ?d1 ?d2)))
)

;   pick_WarpStone_anti_dive, item_active_WarpStone -> active_pick_WarpStone_anti_dive
(defrule r327
   (fact (name pick_WarpStone_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_active_WarpStone) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_WarpStone_anti_dive (min ?c1 ?c2) 0.95 r327 "pick_WarpStone_anti_dive item_active_WarpStone" (+ 1 (max ?d1 ?d2)))
)

;   pick_WarpStone_anti_zone, item_active_WarpStone -> active_pick_WarpStone_anti_zone
(defrule r328
   (fact (name pick_WarpStone_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_active_WarpStone) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_WarpStone_anti_zone (min ?c1 ?c2) 0.95 r328 "pick_WarpStone_anti_zone item_active_WarpStone" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_channel, item_active_SilenceGlyph -> active_pick_SilenceGlyph_anti_channel
(defrule r329
   (fact (name pick_SilenceGlyph_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_active_SilenceGlyph) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_SilenceGlyph_anti_channel (min ?c1 ?c2) 0.95 r329 "pick_SilenceGlyph_anti_channel item_active_SilenceGlyph" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_mobility, item_active_SilenceGlyph -> active_pick_SilenceGlyph_anti_mobility
(defrule r330
   (fact (name pick_SilenceGlyph_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_active_SilenceGlyph) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_SilenceGlyph_anti_mobility (min ?c1 ?c2) 0.95 r330 "pick_SilenceGlyph_anti_mobility item_active_SilenceGlyph" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_invis, item_active_SilenceGlyph -> active_pick_SilenceGlyph_anti_invis
(defrule r331
   (fact (name pick_SilenceGlyph_anti_invis) (cf ?c1) (depth ?d1))
   (fact (name item_active_SilenceGlyph) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_SilenceGlyph_anti_invis (min ?c1 ?c2) 0.95 r331 "pick_SilenceGlyph_anti_invis item_active_SilenceGlyph" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_channel, item_active_Curse -> active_pick_Curse_anti_channel
(defrule r332
   (fact (name pick_Curse_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_active_Curse) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Curse_anti_channel (min ?c1 ?c2) 0.95 r332 "pick_Curse_anti_channel item_active_Curse" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_mobility, item_active_Curse -> active_pick_Curse_anti_mobility
(defrule r333
   (fact (name pick_Curse_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_active_Curse) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Curse_anti_mobility (min ?c1 ?c2) 0.95 r333 "pick_Curse_anti_mobility item_active_Curse" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_heal, item_active_Curse -> active_pick_Curse_anti_heal
(defrule r334
   (fact (name pick_Curse_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_active_Curse) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Curse_anti_heal (min ?c1 ?c2) 0.95 r334 "pick_Curse_anti_heal item_active_Curse" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_immunity, item_active_Curse -> active_pick_Curse_anti_immunity
(defrule r335
   (fact (name pick_Curse_anti_immunity) (cf ?c1) (depth ?d1))
   (fact (name item_active_Curse) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Curse_anti_immunity (min ?c1 ?c2) 0.95 r335 "pick_Curse_anti_immunity item_active_Curse" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_purge, item_active_Curse -> active_pick_Curse_anti_purge
(defrule r336
   (fact (name pick_Curse_anti_purge) (cf ?c1) (depth ?d1))
   (fact (name item_active_Curse) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Curse_anti_purge (min ?c1 ?c2) 0.95 r336 "pick_Curse_anti_purge item_active_Curse" (+ 1 (max ?d1 ?d2)))
)

;   pick_Unstoppable_anti_cc, item_active_Unstoppable -> active_pick_Unstoppable_anti_cc
(defrule r337
   (fact (name pick_Unstoppable_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_active_Unstoppable) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Unstoppable_anti_cc (min ?c1 ?c2) 0.95 r337 "pick_Unstoppable_anti_cc item_active_Unstoppable" (+ 1 (max ?d1 ?d2)))
)

;   pick_Unstoppable_anti_silence, item_active_Unstoppable -> active_pick_Unstoppable_anti_silence
(defrule r338
   (fact (name pick_Unstoppable_anti_silence) (cf ?c1) (depth ?d1))
   (fact (name item_active_Unstoppable) (cf ?c2) (depth ?d2))
   =>
   (derive active_pick_Unstoppable_anti_silence (min ?c1 ?c2) 0.95 r338 "pick_Unstoppable_anti_silence item_active_Unstoppable" (+ 1 (max ?d1 ?d2)))
)

;   pick_MysticShot_damage_core, item_tier_MysticShot_cheap -> cheap_pick_MysticShot_damage_core
(defrule r339
   (fact (name pick_MysticShot_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_MysticShot_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_MysticShot_damage_core (min ?c1 ?c2) 0.95 r339 "pick_MysticShot_damage_core item_tier_MysticShot_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_Berserker_damage_core, item_tier_Berserker_cheap -> cheap_pick_Berserker_damage_core
(defrule r340
   (fact (name pick_Berserker_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Berserker_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_Berserker_damage_core (min ?c1 ?c2) 0.95 r340 "pick_Berserker_damage_core item_tier_Berserker_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_ToxicBullets_anti_heal, item_tier_ToxicBullets_mid -> mid_pick_ToxicBullets_anti_heal
(defrule r341
   (fact (name pick_ToxicBullets_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ToxicBullets_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_ToxicBullets_anti_heal (min ?c1 ?c2) 0.95 r341 "pick_ToxicBullets_anti_heal item_tier_ToxicBullets_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_ToxicBullets_anti_tank, item_tier_ToxicBullets_mid -> mid_pick_ToxicBullets_anti_tank
(defrule r342
   (fact (name pick_ToxicBullets_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ToxicBullets_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_ToxicBullets_anti_tank (min ?c1 ?c2) 0.95 r342 "pick_ToxicBullets_anti_tank item_tier_ToxicBullets_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_Headhunter_anti_poke, item_tier_Headhunter_mid -> mid_pick_Headhunter_anti_poke
(defrule r343
   (fact (name pick_Headhunter_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Headhunter_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_Headhunter_anti_poke (min ?c1 ?c2) 0.95 r343 "pick_Headhunter_anti_poke item_tier_Headhunter_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_Headhunter_damage_core, item_tier_Headhunter_mid -> mid_pick_Headhunter_damage_core
(defrule r344
   (fact (name pick_Headhunter_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Headhunter_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_Headhunter_damage_core (min ?c1 ?c2) 0.95 r344 "pick_Headhunter_damage_core item_tier_Headhunter_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_EscalatingResilience_anti_bullet, item_tier_EscalatingResilience_mid -> mid_pick_EscalatingResilience_anti_bullet
(defrule r345
   (fact (name pick_EscalatingResilience_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_tier_EscalatingResilience_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_EscalatingResilience_anti_bullet (min ?c1 ?c2) 0.95 r345 "pick_EscalatingResilience_anti_bullet item_tier_EscalatingResilience_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SlowingHex_anti_mobility, item_tier_SlowingHex_mid -> mid_pick_SlowingHex_anti_mobility
(defrule r346
   (fact (name pick_SlowingHex_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SlowingHex_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_SlowingHex_anti_mobility (min ?c1 ?c2) 0.95 r346 "pick_SlowingHex_anti_mobility item_tier_SlowingHex_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SlowingHex_anti_dive, item_tier_SlowingHex_mid -> mid_pick_SlowingHex_anti_dive
(defrule r347
   (fact (name pick_SlowingHex_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SlowingHex_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_SlowingHex_anti_dive (min ?c1 ?c2) 0.95 r347 "pick_SlowingHex_anti_dive item_tier_SlowingHex_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_Sharpshooter_anti_poke, item_tier_Sharpshooter_expensive -> expensive_pick_Sharpshooter_anti_poke
(defrule r348
   (fact (name pick_Sharpshooter_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Sharpshooter_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Sharpshooter_anti_poke (min ?c1 ?c2) 0.95 r348 "pick_Sharpshooter_anti_poke item_tier_Sharpshooter_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Sharpshooter_damage_core, item_tier_Sharpshooter_expensive -> expensive_pick_Sharpshooter_damage_core
(defrule r349
   (fact (name pick_Sharpshooter_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Sharpshooter_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Sharpshooter_damage_core (min ?c1 ?c2) 0.95 r349 "pick_Sharpshooter_damage_core item_tier_Sharpshooter_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Inhibitor_anti_heal, item_tier_Inhibitor_expensive -> expensive_pick_Inhibitor_anti_heal
(defrule r350
   (fact (name pick_Inhibitor_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Inhibitor_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Inhibitor_anti_heal (min ?c1 ?c2) 0.95 r350 "pick_Inhibitor_anti_heal item_tier_Inhibitor_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Inhibitor_anti_mobility, item_tier_Inhibitor_expensive -> expensive_pick_Inhibitor_anti_mobility
(defrule r351
   (fact (name pick_Inhibitor_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Inhibitor_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Inhibitor_anti_mobility (min ?c1 ?c2) 0.95 r351 "pick_Inhibitor_anti_mobility item_tier_Inhibitor_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Inhibitor_anti_regen, item_tier_Inhibitor_expensive -> expensive_pick_Inhibitor_anti_regen
(defrule r352
   (fact (name pick_Inhibitor_anti_regen) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Inhibitor_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Inhibitor_anti_regen (min ?c1 ?c2) 0.95 r352 "pick_Inhibitor_anti_regen item_tier_Inhibitor_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_CripplingHeadshot_anti_tank, item_tier_CripplingHeadshot_expensive -> expensive_pick_CripplingHeadshot_anti_tank
(defrule r353
   (fact (name pick_CripplingHeadshot_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_tier_CripplingHeadshot_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_CripplingHeadshot_anti_tank (min ?c1 ?c2) 0.95 r353 "pick_CripplingHeadshot_anti_tank item_tier_CripplingHeadshot_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_HuntersAura_anti_tank, item_tier_HuntersAura_expensive -> expensive_pick_HuntersAura_anti_tank
(defrule r354
   (fact (name pick_HuntersAura_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_tier_HuntersAura_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_HuntersAura_anti_tank (min ?c1 ?c2) 0.95 r354 "pick_HuntersAura_anti_tank item_tier_HuntersAura_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_HuntersAura_anti_bullet, item_tier_HuntersAura_expensive -> expensive_pick_HuntersAura_anti_bullet
(defrule r355
   (fact (name pick_HuntersAura_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_tier_HuntersAura_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_HuntersAura_anti_bullet (min ?c1 ?c2) 0.95 r355 "pick_HuntersAura_anti_bullet item_tier_HuntersAura_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Silencer_anti_channel, item_tier_Silencer_expensive -> expensive_pick_Silencer_anti_channel
(defrule r356
   (fact (name pick_Silencer_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Silencer_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Silencer_anti_channel (min ?c1 ?c2) 0.95 r356 "pick_Silencer_anti_channel item_tier_Silencer_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Silencer_anti_mobility, item_tier_Silencer_expensive -> expensive_pick_Silencer_anti_mobility
(defrule r357
   (fact (name pick_Silencer_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Silencer_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Silencer_anti_mobility (min ?c1 ?c2) 0.95 r357 "pick_Silencer_anti_mobility item_tier_Silencer_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_SpiritualOverflow_damage_core, item_tier_SpiritualOverflow_expensive -> expensive_pick_SpiritualOverflow_damage_core
(defrule r358
   (fact (name pick_SpiritualOverflow_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SpiritualOverflow_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_SpiritualOverflow_damage_core (min ?c1 ?c2) 0.95 r358 "pick_SpiritualOverflow_damage_core item_tier_SpiritualOverflow_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_ExtraStamina_anti_dive, item_tier_ExtraStamina_cheap -> cheap_pick_ExtraStamina_anti_dive
(defrule r359
   (fact (name pick_ExtraStamina_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ExtraStamina_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_ExtraStamina_anti_dive (min ?c1 ?c2) 0.95 r359 "pick_ExtraStamina_anti_dive item_tier_ExtraStamina_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_HealingRite_sustain_self, item_tier_HealingRite_cheap -> cheap_pick_HealingRite_sustain_self
(defrule r360
   (fact (name pick_HealingRite_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_tier_HealingRite_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_HealingRite_sustain_self (min ?c1 ?c2) 0.95 r360 "pick_HealingRite_sustain_self item_tier_HealingRite_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_BulletArmor_anti_bullet, item_tier_BulletArmor_cheap -> cheap_pick_BulletArmor_anti_bullet
(defrule r361
   (fact (name pick_BulletArmor_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_tier_BulletArmor_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_BulletArmor_anti_bullet (min ?c1 ?c2) 0.95 r361 "pick_BulletArmor_anti_bullet item_tier_BulletArmor_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_SpiritArmor_anti_magic, item_tier_SpiritArmor_cheap -> cheap_pick_SpiritArmor_anti_magic
(defrule r362
   (fact (name pick_SpiritArmor_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SpiritArmor_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_SpiritArmor_anti_magic (min ?c1 ?c2) 0.95 r362 "pick_SpiritArmor_anti_magic item_tier_SpiritArmor_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_DebuffReducer_anti_dot, item_tier_DebuffReducer_cheap -> cheap_pick_DebuffReducer_anti_dot
(defrule r363
   (fact (name pick_DebuffReducer_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DebuffReducer_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_DebuffReducer_anti_dot (min ?c1 ?c2) 0.95 r363 "pick_DebuffReducer_anti_dot item_tier_DebuffReducer_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReactiveBarrier_anti_cc, item_tier_ReactiveBarrier_cheap -> cheap_pick_ReactiveBarrier_anti_cc
(defrule r364
   (fact (name pick_ReactiveBarrier_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ReactiveBarrier_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_ReactiveBarrier_anti_cc (min ?c1 ?c2) 0.95 r364 "pick_ReactiveBarrier_anti_cc item_tier_ReactiveBarrier_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReactiveBarrier_anti_magic, item_tier_ReactiveBarrier_cheap -> cheap_pick_ReactiveBarrier_anti_magic
(defrule r365
   (fact (name pick_ReactiveBarrier_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ReactiveBarrier_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_ReactiveBarrier_anti_magic (min ?c1 ?c2) 0.95 r365 "pick_ReactiveBarrier_anti_magic item_tier_ReactiveBarrier_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivineBarrier_anti_magic, item_tier_DivineBarrier_mid -> mid_pick_DivineBarrier_anti_magic
(defrule r366
   (fact (name pick_DivineBarrier_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DivineBarrier_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_DivineBarrier_anti_magic (min ?c1 ?c2) 0.95 r366 "pick_DivineBarrier_anti_magic item_tier_DivineBarrier_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivineBarrier_anti_execute, item_tier_DivineBarrier_mid -> mid_pick_DivineBarrier_anti_execute
(defrule r367
   (fact (name pick_DivineBarrier_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DivineBarrier_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_DivineBarrier_anti_execute (min ?c1 ?c2) 0.95 r367 "pick_DivineBarrier_anti_execute item_tier_DivineBarrier_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_MetalSkin_anti_bullet, item_tier_MetalSkin_mid -> mid_pick_MetalSkin_anti_bullet
(defrule r368
   (fact (name pick_MetalSkin_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_tier_MetalSkin_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_MetalSkin_anti_bullet (min ?c1 ?c2) 0.95 r368 "pick_MetalSkin_anti_bullet item_tier_MetalSkin_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_MetalSkin_anti_channel, item_tier_MetalSkin_mid -> mid_pick_MetalSkin_anti_channel
(defrule r369
   (fact (name pick_MetalSkin_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_tier_MetalSkin_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_MetalSkin_anti_channel (min ?c1 ?c2) 0.95 r369 "pick_MetalSkin_anti_channel item_tier_MetalSkin_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_ReturnFire_anti_bullet, item_tier_ReturnFire_mid -> mid_pick_ReturnFire_anti_bullet
(defrule r370
   (fact (name pick_ReturnFire_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name item_tier_ReturnFire_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_ReturnFire_anti_bullet (min ?c1 ?c2) 0.95 r370 "pick_ReturnFire_anti_bullet item_tier_ReturnFire_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SuperiorStamina_anti_dive, item_tier_SuperiorStamina_expensive -> expensive_pick_SuperiorStamina_anti_dive
(defrule r371
   (fact (name pick_SuperiorStamina_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SuperiorStamina_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_SuperiorStamina_anti_dive (min ?c1 ?c2) 0.95 r371 "pick_SuperiorStamina_anti_dive item_tier_SuperiorStamina_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Fortitude_anti_poke, item_tier_Fortitude_expensive -> expensive_pick_Fortitude_anti_poke
(defrule r372
   (fact (name pick_Fortitude_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Fortitude_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Fortitude_anti_poke (min ?c1 ?c2) 0.95 r372 "pick_Fortitude_anti_poke item_tier_Fortitude_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Fortitude_sustain_self, item_tier_Fortitude_expensive -> expensive_pick_Fortitude_sustain_self
(defrule r373
   (fact (name pick_Fortitude_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Fortitude_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Fortitude_sustain_self (min ?c1 ?c2) 0.95 r373 "pick_Fortitude_sustain_self item_tier_Fortitude_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_DebuffRemover_anti_dot, item_tier_DebuffRemover_expensive -> expensive_pick_DebuffRemover_anti_dot
(defrule r374
   (fact (name pick_DebuffRemover_anti_dot) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DebuffRemover_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_DebuffRemover_anti_dot (min ?c1 ?c2) 0.95 r374 "pick_DebuffRemover_anti_dot item_tier_DebuffRemover_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_DebuffRemover_anti_cc, item_tier_DebuffRemover_expensive -> expensive_pick_DebuffRemover_anti_cc
(defrule r375
   (fact (name pick_DebuffRemover_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DebuffRemover_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_DebuffRemover_anti_cc (min ?c1 ?c2) 0.95 r375 "pick_DebuffRemover_anti_cc item_tier_DebuffRemover_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Colossus_anti_dive, item_tier_Colossus_expensive -> expensive_pick_Colossus_anti_dive
(defrule r376
   (fact (name pick_Colossus_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Colossus_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Colossus_anti_dive (min ?c1 ?c2) 0.95 r376 "pick_Colossus_anti_dive item_tier_Colossus_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Colossus_sustain_self, item_tier_Colossus_expensive -> expensive_pick_Colossus_sustain_self
(defrule r377
   (fact (name pick_Colossus_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Colossus_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Colossus_sustain_self (min ?c1 ?c2) 0.95 r377 "pick_Colossus_sustain_self item_tier_Colossus_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_SoulRebirth_anti_execute, item_tier_SoulRebirth_expensive -> expensive_pick_SoulRebirth_anti_execute
(defrule r378
   (fact (name pick_SoulRebirth_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SoulRebirth_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_SoulRebirth_anti_execute (min ?c1 ?c2) 0.95 r378 "pick_SoulRebirth_anti_execute item_tier_SoulRebirth_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivinersKevlar_anti_magic, item_tier_DivinersKevlar_expensive -> expensive_pick_DivinersKevlar_anti_magic
(defrule r379
   (fact (name pick_DivinersKevlar_anti_magic) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DivinersKevlar_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_DivinersKevlar_anti_magic (min ?c1 ?c2) 0.95 r379 "pick_DivinersKevlar_anti_magic item_tier_DivinersKevlar_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivinersKevlar_anti_cc, item_tier_DivinersKevlar_expensive -> expensive_pick_DivinersKevlar_anti_cc
(defrule r380
   (fact (name pick_DivinersKevlar_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DivinersKevlar_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_DivinersKevlar_anti_cc (min ?c1 ?c2) 0.95 r380 "pick_DivinersKevlar_anti_cc item_tier_DivinersKevlar_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_DivinersKevlar_anti_disable, item_tier_DivinersKevlar_expensive -> expensive_pick_DivinersKevlar_anti_disable
(defrule r381
   (fact (name pick_DivinersKevlar_anti_disable) (cf ?c1) (depth ?d1))
   (fact (name item_tier_DivinersKevlar_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_DivinersKevlar_anti_disable (min ?c1 ?c2) 0.95 r381 "pick_DivinersKevlar_anti_disable item_tier_DivinersKevlar_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Leech_sustain_self, item_tier_Leech_expensive -> expensive_pick_Leech_sustain_self
(defrule r382
   (fact (name pick_Leech_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Leech_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Leech_sustain_self (min ?c1 ?c2) 0.95 r382 "pick_Leech_sustain_self item_tier_Leech_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_MysticBurst_damage_core, item_tier_MysticBurst_cheap -> cheap_pick_MysticBurst_damage_core
(defrule r383
   (fact (name pick_MysticBurst_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_MysticBurst_cheap) (cf ?c2) (depth ?d2))
   =>
   (derive cheap_pick_MysticBurst_damage_core (min ?c1 ?c2) 0.95 r383 "pick_MysticBurst_damage_core item_tier_MysticBurst_cheap" (+ 1 (max ?d1 ?d2)))
)

;   pick_Knockdown_anti_channel, item_tier_Knockdown_mid -> mid_pick_Knockdown_anti_channel
(defrule r384
   (fact (name pick_Knockdown_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Knockdown_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_Knockdown_anti_channel (min ?c1 ?c2) 0.95 r384 "pick_Knockdown_anti_channel item_tier_Knockdown_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_Knockdown_anti_poke, item_tier_Knockdown_mid -> mid_pick_Knockdown_anti_poke
(defrule r385
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Knockdown_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_Knockdown_anti_poke (min ?c1 ?c2) 0.95 r385 "pick_Knockdown_anti_poke item_tier_Knockdown_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_EtherealShift_anti_execute, item_tier_EtherealShift_mid -> mid_pick_EtherealShift_anti_execute
(defrule r386
   (fact (name pick_EtherealShift_anti_execute) (cf ?c1) (depth ?d1))
   (fact (name item_tier_EtherealShift_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_EtherealShift_anti_execute (min ?c1 ?c2) 0.95 r386 "pick_EtherealShift_anti_execute item_tier_EtherealShift_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_EtherealShift_anti_zone, item_tier_EtherealShift_mid -> mid_pick_EtherealShift_anti_zone
(defrule r387
   (fact (name pick_EtherealShift_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_tier_EtherealShift_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_EtherealShift_anti_zone (min ?c1 ?c2) 0.95 r387 "pick_EtherealShift_anti_zone item_tier_EtherealShift_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_WarpStone_anti_dive, item_tier_WarpStone_mid -> mid_pick_WarpStone_anti_dive
(defrule r388
   (fact (name pick_WarpStone_anti_dive) (cf ?c1) (depth ?d1))
   (fact (name item_tier_WarpStone_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_WarpStone_anti_dive (min ?c1 ?c2) 0.95 r388 "pick_WarpStone_anti_dive item_tier_WarpStone_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_WarpStone_anti_zone, item_tier_WarpStone_mid -> mid_pick_WarpStone_anti_zone
(defrule r389
   (fact (name pick_WarpStone_anti_zone) (cf ?c1) (depth ?d1))
   (fact (name item_tier_WarpStone_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_WarpStone_anti_zone (min ?c1 ?c2) 0.95 r389 "pick_WarpStone_anti_zone item_tier_WarpStone_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_channel, item_tier_SilenceGlyph_mid -> mid_pick_SilenceGlyph_anti_channel
(defrule r390
   (fact (name pick_SilenceGlyph_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SilenceGlyph_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_SilenceGlyph_anti_channel (min ?c1 ?c2) 0.95 r390 "pick_SilenceGlyph_anti_channel item_tier_SilenceGlyph_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_mobility, item_tier_SilenceGlyph_mid -> mid_pick_SilenceGlyph_anti_mobility
(defrule r391
   (fact (name pick_SilenceGlyph_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SilenceGlyph_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_SilenceGlyph_anti_mobility (min ?c1 ?c2) 0.95 r391 "pick_SilenceGlyph_anti_mobility item_tier_SilenceGlyph_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_SilenceGlyph_anti_invis, item_tier_SilenceGlyph_mid -> mid_pick_SilenceGlyph_anti_invis
(defrule r392
   (fact (name pick_SilenceGlyph_anti_invis) (cf ?c1) (depth ?d1))
   (fact (name item_tier_SilenceGlyph_mid) (cf ?c2) (depth ?d2))
   =>
   (derive mid_pick_SilenceGlyph_anti_invis (min ?c1 ?c2) 0.95 r392 "pick_SilenceGlyph_anti_invis item_tier_SilenceGlyph_mid" (+ 1 (max ?d1 ?d2)))
)

;   pick_EscalatingExposure_anti_tank, item_tier_EscalatingExposure_expensive -> expensive_pick_EscalatingExposure_anti_tank
(defrule r393
   (fact (name pick_EscalatingExposure_anti_tank) (cf ?c1) (depth ?d1))
   (fact (name item_tier_EscalatingExposure_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_EscalatingExposure_anti_tank (min ?c1 ?c2) 0.95 r393 "pick_EscalatingExposure_anti_tank item_tier_EscalatingExposure_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_channel, item_tier_Curse_expensive -> expensive_pick_Curse_anti_channel
(defrule r394
   (fact (name pick_Curse_anti_channel) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Curse_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Curse_anti_channel (min ?c1 ?c2) 0.95 r394 "pick_Curse_anti_channel item_tier_Curse_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_mobility, item_tier_Curse_expensive -> expensive_pick_Curse_anti_mobility
(defrule r395
   (fact (name pick_Curse_anti_mobility) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Curse_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Curse_anti_mobility (min ?c1 ?c2) 0.95 r395 "pick_Curse_anti_mobility item_tier_Curse_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_heal, item_tier_Curse_expensive -> expensive_pick_Curse_anti_heal
(defrule r396
   (fact (name pick_Curse_anti_heal) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Curse_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Curse_anti_heal (min ?c1 ?c2) 0.95 r396 "pick_Curse_anti_heal item_tier_Curse_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_immunity, item_tier_Curse_expensive -> expensive_pick_Curse_anti_immunity
(defrule r397
   (fact (name pick_Curse_anti_immunity) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Curse_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Curse_anti_immunity (min ?c1 ?c2) 0.95 r397 "pick_Curse_anti_immunity item_tier_Curse_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Curse_anti_purge, item_tier_Curse_expensive -> expensive_pick_Curse_anti_purge
(defrule r398
   (fact (name pick_Curse_anti_purge) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Curse_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Curse_anti_purge (min ?c1 ?c2) 0.95 r398 "pick_Curse_anti_purge item_tier_Curse_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Unstoppable_anti_cc, item_tier_Unstoppable_expensive -> expensive_pick_Unstoppable_anti_cc
(defrule r399
   (fact (name pick_Unstoppable_anti_cc) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Unstoppable_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Unstoppable_anti_cc (min ?c1 ?c2) 0.95 r399 "pick_Unstoppable_anti_cc item_tier_Unstoppable_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_Unstoppable_anti_silence, item_tier_Unstoppable_expensive -> expensive_pick_Unstoppable_anti_silence
(defrule r400
   (fact (name pick_Unstoppable_anti_silence) (cf ?c1) (depth ?d1))
   (fact (name item_tier_Unstoppable_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_Unstoppable_anti_silence (min ?c1 ?c2) 0.95 r400 "pick_Unstoppable_anti_silence item_tier_Unstoppable_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_BoundlessSpirit_damage_core, item_tier_BoundlessSpirit_expensive -> expensive_pick_BoundlessSpirit_damage_core
(defrule r401
   (fact (name pick_BoundlessSpirit_damage_core) (cf ?c1) (depth ?d1))
   (fact (name item_tier_BoundlessSpirit_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_BoundlessSpirit_damage_core (min ?c1 ?c2) 0.95 r401 "pick_BoundlessSpirit_damage_core item_tier_BoundlessSpirit_expensive" (+ 1 (max ?d1 ?d2)))
)

;   pick_BoundlessSpirit_sustain_self, item_tier_BoundlessSpirit_expensive -> expensive_pick_BoundlessSpirit_sustain_self
(defrule r402
   (fact (name pick_BoundlessSpirit_sustain_self) (cf ?c1) (depth ?d1))
   (fact (name item_tier_BoundlessSpirit_expensive) (cf ?c2) (depth ?d2))
   =>
   (derive expensive_pick_BoundlessSpirit_sustain_self (min ?c1 ?c2) 0.95 r402 "pick_BoundlessSpirit_sustain_self item_tier_BoundlessSpirit_expensive" (+ 1 (max ?d1 ?d2)))
)

; Storm Cloud накрывает точку — нужен срыв канала или выход из зоны.
;   hero_Seven, need_anti_channel -> warning_channel_ult_threat
(defrule r403
   (fact (name hero_Seven) (cf ?c1) (depth ?d1))
   (fact (name need_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive warning_channel_ult_threat (min ?c1 ?c2) 0.65 r403 "hero_Seven need_anti_channel" (+ 1 (max ?d1 ?d2)))
)

; Abrams живёт за счёт лечения: без сокращения лечения он не умирает.
;   hero_Abrams, need_anti_heal -> warning_sustain_tank
(defrule r404
   (fact (name hero_Abrams) (cf ?c1) (depth ?d1))
   (fact (name need_anti_heal) (cf ?c2) (depth ?d2))
   =>
   (derive warning_sustain_tank (min ?c1 ?c2) 0.65 r404 "hero_Abrams need_anti_heal" (+ 1 (max ?d1 ?d2)))
)

; Combo — самый длинный контроль в игре: нужен иммунитет.
;   hero_MoKrill, need_anti_cc -> warning_combo_lockdown
(defrule r405
   (fact (name hero_MoKrill) (cf ?c1) (depth ?d1))
   (fact (name need_anti_cc) (cf ?c2) (depth ?d2))
   =>
   (derive warning_combo_lockdown (min ?c1 ?c2) 0.65 r405 "hero_MoKrill need_anti_cc" (+ 1 (max ?d1 ?d2)))
)

; Bullet Dance убивает за одно окно: нужен Metal Skin или срыв.
;   hero_Haze, need_anti_channel -> warning_haze_ult_window
(defrule r406
   (fact (name hero_Haze) (cf ?c1) (depth ?d1))
   (fact (name need_anti_channel) (cf ?c2) (depth ?d2))
   =>
   (derive warning_haze_ult_window (min ?c1 ?c2) 0.65 r406 "hero_Haze need_anti_channel" (+ 1 (max ?d1 ?d2)))
)

; Assassinate добивает с дистанции: нужен предмет, переживающий казнь.
;   hero_Vindicta, need_anti_execute -> warning_execute_window
(defrule r407
   (fact (name hero_Vindicta) (cf ?c1) (depth ?d1))
   (fact (name need_anti_execute) (cf ?c2) (depth ?d2))
   =>
   (derive warning_execute_window (min ?c1 ?c2) 0.65 r407 "hero_Vindicta need_anti_execute" (+ 1 (max ?d1 ?d2)))
)

; Нужны две линии защиты одновременно — планируйте слоты и бюджет.
;   need_anti_bullet, need_anti_magic -> warning_two_defense_slots
(defrule r408
   (fact (name need_anti_bullet) (cf ?c1) (depth ?d1))
   (fact (name need_anti_magic) (cf ?c2) (depth ?d2))
   =>
   (derive warning_two_defense_slots (min ?c1 ?c2) 0.65 r408 "need_anti_bullet need_anti_magic" (+ 1 (max ?d1 ?d2)))
)

; Metal Skin — активный предмет: враг берёт Silence Glyph, чтобы сайленсить его активацию.
;   pick_MetalSkin_anti_bullet -> enemy_buys_SilenceGlyph
(defrule r409
   (fact (name pick_MetalSkin_anti_bullet) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_buys_SilenceGlyph ?c1 0.60 r409 "pick_MetalSkin_anti_bullet" (+ 1 ?d1))
)

; Против нашего сокращения лечения враг берёт Fortitude с регенерацией — нужен Inhibitor.
;   pick_ToxicBullets_anti_heal -> enemy_buys_Fortitude
(defrule r410
   (fact (name pick_ToxicBullets_anti_heal) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_buys_Fortitude ?c1 0.60 r410 "pick_ToxicBullets_anti_heal" (+ 1 ?d1))
)

; Unstoppable снимает наш Knockdown — отвечаем Curse, который отключает предметы.
;   pick_Knockdown_anti_poke -> enemy_buys_Unstoppable
(defrule r411
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_buys_Unstoppable ?c1 0.60 r411 "pick_Knockdown_anti_poke" (+ 1 ?d1))
)

; Debuff Remover снимает наше замедление — Curse не даёт применить очистку.
;   pick_SlowingHex_anti_mobility -> enemy_buys_DebuffRemover
(defrule r412
   (fact (name pick_SlowingHex_anti_mobility) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_buys_DebuffRemover ?c1 0.60 r412 "pick_SlowingHex_anti_mobility" (+ 1 ?d1))
)

; Curse отключает Ethereal Shift — пассивный Diviner's Kevlar продолжает работать.
;   pick_EtherealShift_anti_execute -> enemy_buys_Curse
(defrule r413
   (fact (name pick_EtherealShift_anti_execute) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_buys_Curse ?c1 0.60 r413 "pick_EtherealShift_anti_execute" (+ 1 ?d1))
)

; SilenceGlyph в руках врага — новая угроза: item_silence.
;   enemy_buys_SilenceGlyph -> threat_item_silence
(defrule r414
   (fact (name enemy_buys_SilenceGlyph) (cf ?c1) (depth ?d1))
   =>
   (derive threat_item_silence ?c1 0.90 r414 "enemy_buys_SilenceGlyph" (+ 1 ?d1))
)

; Fortitude в руках врага — новая угроза: item_regen.
;   enemy_buys_Fortitude -> threat_item_regen
(defrule r415
   (fact (name enemy_buys_Fortitude) (cf ?c1) (depth ?d1))
   =>
   (derive threat_item_regen ?c1 0.90 r415 "enemy_buys_Fortitude" (+ 1 ?d1))
)

; Unstoppable в руках врага — новая угроза: item_immunity.
;   enemy_buys_Unstoppable -> threat_item_immunity
(defrule r416
   (fact (name enemy_buys_Unstoppable) (cf ?c1) (depth ?d1))
   =>
   (derive threat_item_immunity ?c1 0.90 r416 "enemy_buys_Unstoppable" (+ 1 ?d1))
)

; DebuffRemover в руках врага — новая угроза: item_purge.
;   enemy_buys_DebuffRemover -> threat_item_purge
(defrule r417
   (fact (name enemy_buys_DebuffRemover) (cf ?c1) (depth ?d1))
   =>
   (derive threat_item_purge ?c1 0.90 r417 "enemy_buys_DebuffRemover" (+ 1 ?d1))
)

; Curse в руках врага — новая угроза: item_disable.
;   enemy_buys_Curse -> threat_item_disable
(defrule r418
   (fact (name enemy_buys_Curse) (cf ?c1) (depth ?d1))
   =>
   (derive threat_item_disable ?c1 0.90 r418 "enemy_buys_Curse" (+ 1 ?d1))
)

; Угроза item_silence закрывается потребностью anti_silence.
;   threat_item_silence -> need_anti_silence
(defrule r419
   (fact (name threat_item_silence) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_silence ?c1 0.85 r419 "threat_item_silence" (+ 1 ?d1))
)

; Угроза item_regen закрывается потребностью anti_regen.
;   threat_item_regen -> need_anti_regen
(defrule r420
   (fact (name threat_item_regen) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_regen ?c1 0.85 r420 "threat_item_regen" (+ 1 ?d1))
)

; Угроза item_immunity закрывается потребностью anti_immunity.
;   threat_item_immunity -> need_anti_immunity
(defrule r421
   (fact (name threat_item_immunity) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_immunity ?c1 0.85 r421 "threat_item_immunity" (+ 1 ?d1))
)

; Угроза item_purge закрывается потребностью anti_purge.
;   threat_item_purge -> need_anti_purge
(defrule r422
   (fact (name threat_item_purge) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_purge ?c1 0.85 r422 "threat_item_purge" (+ 1 ?d1))
)

; Угроза item_disable закрывается потребностью anti_disable.
;   threat_item_disable -> need_anti_disable
(defrule r423
   (fact (name threat_item_disable) (cf ?c1) (depth ?d1))
   =>
   (derive need_anti_disable ?c1 0.85 r423 "threat_item_disable" (+ 1 ?d1))
)

; Unstoppable — второй контр-предмет: закрывает anti_silence после ответа врага (SilenceGlyph).
;   need_anti_silence, item_provides_Unstoppable_anti_silence -> second_pick_Unstoppable_anti_silence
(defrule r424
   (fact (name need_anti_silence) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Unstoppable_anti_silence) (cf ?c2) (depth ?d2))
   =>
   (derive second_pick_Unstoppable_anti_silence (min ?c1 ?c2) 0.70 r424 "need_anti_silence item_provides_Unstoppable_anti_silence" (+ 1 (max ?d1 ?d2)))
)

; Inhibitor — второй контр-предмет: закрывает anti_regen после ответа врага (Fortitude).
;   need_anti_regen, item_provides_Inhibitor_anti_regen -> second_pick_Inhibitor_anti_regen
(defrule r425
   (fact (name need_anti_regen) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Inhibitor_anti_regen) (cf ?c2) (depth ?d2))
   =>
   (derive second_pick_Inhibitor_anti_regen (min ?c1 ?c2) 0.70 r425 "need_anti_regen item_provides_Inhibitor_anti_regen" (+ 1 (max ?d1 ?d2)))
)

; Curse — второй контр-предмет: закрывает anti_immunity после ответа врага (Unstoppable).
;   need_anti_immunity, item_provides_Curse_anti_immunity -> second_pick_Curse_anti_immunity
(defrule r426
   (fact (name need_anti_immunity) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_immunity) (cf ?c2) (depth ?d2))
   =>
   (derive second_pick_Curse_anti_immunity (min ?c1 ?c2) 0.70 r426 "need_anti_immunity item_provides_Curse_anti_immunity" (+ 1 (max ?d1 ?d2)))
)

; Curse — второй контр-предмет: закрывает anti_purge после ответа врага (DebuffRemover).
;   need_anti_purge, item_provides_Curse_anti_purge -> second_pick_Curse_anti_purge
(defrule r427
   (fact (name need_anti_purge) (cf ?c1) (depth ?d1))
   (fact (name item_provides_Curse_anti_purge) (cf ?c2) (depth ?d2))
   =>
   (derive second_pick_Curse_anti_purge (min ?c1 ?c2) 0.70 r427 "need_anti_purge item_provides_Curse_anti_purge" (+ 1 (max ?d1 ?d2)))
)

; DivinersKevlar — второй контр-предмет: закрывает anti_disable после ответа врага (Curse).
;   need_anti_disable, item_provides_DivinersKevlar_anti_disable -> second_pick_DivinersKevlar_anti_disable
(defrule r428
   (fact (name need_anti_disable) (cf ?c1) (depth ?d1))
   (fact (name item_provides_DivinersKevlar_anti_disable) (cf ?c2) (depth ?d2))
   =>
   (derive second_pick_DivinersKevlar_anti_disable (min ?c1 ?c2) 0.70 r428 "need_anti_disable item_provides_DivinersKevlar_anti_disable" (+ 1 (max ?d1 ?d2)))
)

; Враг ответил на Knockdown — нужен свой ответ.
;   enemy_answers_Knockdown -> i_answers_Knockdown
(defrule r429
   (fact (name enemy_answers_Knockdown) (cf ?c1) (depth ?d1))
   =>
   (derive i_answers_Knockdown ?c1 0.55 r429 "enemy_answers_Knockdown" (+ 1 ?d1))
)

; Свой ответ снова провоцирует ответ врага — цикл.
;   i_answers_Knockdown -> enemy_answers_Knockdown
(defrule r430
   (fact (name i_answers_Knockdown) (cf ?c1) (depth ?d1))
   =>
   (derive enemy_answers_Knockdown ?c1 0.60 r430 "i_answers_Knockdown" (+ 1 ?d1))
)

; Выход из цикла: Knockdown уже в сборке и активен — ответ на Knockdown врага есть.
;   pick_Knockdown_anti_poke, item_active_Knockdown -> i_answers_Knockdown
(defrule r431
   (fact (name pick_Knockdown_anti_poke) (cf ?c1) (depth ?d1))
   (fact (name item_active_Knockdown) (cf ?c2) (depth ?d2))
   =>
   (derive i_answers_Knockdown (min ?c1 ?c2) 0.55 r431 "pick_Knockdown_anti_poke item_active_Knockdown" (+ 1 (max ?d1 ?d2)))
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
