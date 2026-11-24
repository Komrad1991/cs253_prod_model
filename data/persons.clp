;========================================================================
; Экспертная система «Тиндер» — пример программы для режима CLIPS.
;
; Что доработано относительно исходного файла примера:
;   * заполнены правила с пометкой «Дописать!»:
;       set-output-and-halt, set-output-and-proceed,
;       append-output-and-proceed;
;   * исправлено print-messages — список сообщений печатается (implode$);
;   * complex-eye-hair-match помечает найденную пару фактом (pair-found),
;     поэтому match-pair-for-user срабатывает только когда пары нет;
;   * приветствию выдана салиентность 100, чтобы оно печаталось первым.
;========================================================================

;------------------------------------------------------------------------
; Блок обмена информацией с графической оболочкой
;------------------------------------------------------------------------

(deftemplate ioproxy  ; шаблон факта-посредника для обмена с GUI
	(slot fact-id)        ; id факта для изменения
	(multislot answers)   ; возможные ответы
	(multislot messages)  ; исходящие сообщения
	(slot reaction)       ; возможные ответы пользователя
	(slot value)          ; выбор пользователя
	(slot restore)        ; служебное поле
)

(deffacts proxy-fact
	(ioproxy
		(fact-id 0112)
		(value none)
		(messages)
	)
)

(defrule clear-messages
	(declare (salience 90))
	?clear-msg-flg <- (clearmessage)
	?proxy <- (ioproxy)
	=>
	(modify ?proxy (messages))
	(retract ?clear-msg-flg)
	(printout t "Messages cleared ..." crlf)
)

(defrule set-output-and-halt
	(declare (salience 99))
	?current-message <- (setmessagehalt $?new-msg)
	?proxy <- (ioproxy)
	=>
	(printout t "Messages replaced : " (implode$ $?new-msg) " ... halting ..." crlf)
	(modify ?proxy (messages $?new-msg))
	(retract ?current-message)
	(halt)
)

(defrule append-output-and-halt
	(declare (salience 99))
	?current-message <- (appendmessagehalt $?new-msg)
	?proxy <- (ioproxy (messages $?msg-list))
	=>
	(printout t "Messages appended : " (implode$ $?new-msg) " ... halting ..." crlf)
	(modify ?proxy (messages $?msg-list $?new-msg))
	(retract ?current-message)
	(halt)
)

(defrule set-output-and-proceed
	(declare (salience 99))
	?current-message <- (setmessage $?new-msg)
	?proxy <- (ioproxy)
	=>
	(modify ?proxy (messages $?new-msg))
	(retract ?current-message)
)

(defrule append-output-and-proceed
	(declare (salience 99))
	?current-message <- (appendmessage $?new-msg)
	?proxy <- (ioproxy (messages $?msg-list))
	=>
	(modify ?proxy (messages $?msg-list $?new-msg))
	(retract ?current-message)
)

(defrule print-messages
	(declare (salience 99))
	?proxy <- (ioproxy (messages $?msg-list))
	?update-key <- (updated True)
	=>
	(retract ?update-key)
	(printout t "Messages received : " (implode$ ?msg-list) crlf)
)

;------------------------------------------------------------------------
; Предметная область: подбор пары по цвету глаз, волос и возрасту
;------------------------------------------------------------------------

(deftemplate person
	(slot name)
	(slot eyes)
	(slot age)
	(slot hair)
)

(deffacts some-of-candidates
	(person (name Джон) (eyes Black) (age 30) (hair Black))
	(person (name Jane) (eyes Violet) (age 20) (hair Red))
	(person (name Джек) (eyes Green) (age 27) (hair Red))
	(person (name Jennifer) (eyes Brown) (age 50) (hair Brown))
)

(defrule greeting
	(declare (salience 100))
	=>
	(printout t "Hello! " crlf)
)

(defrule complex-eye-hair-match
	(declare (salience 40))
	?p1 <- (person (name ?name1)
		(eyes ?eyes1)
		(age ?age1&:(< ?age1 30))
		(hair ?hair1)
	)
	?p2 <-	(person (name ?name2&~?name1)
		(eyes ?eyes2&~eyes1)
		(age ?age2)
		(hair ?hair2&red|?hair1)
	)
	(test (< (abs (- ?age1 ?age2)) 10))
	=>
	(printout t "----------------------" crlf)
	(printout t "  " ?name1 " has " ?eyes1 " eyes and " ?hair1 " hair." crlf)
	(printout t "  " ?name2 " has " ?eyes2 " eyes and " ?hair2 " hair." crlf)
	(assert (pair-found ?name1 ?name2))
	(assert (appendmessagehalt (str-cat "У нас есть пара! " ?name1 " и " ?name2)))
)

(defrule match-pair-for-user
	(declare (salience 10))
	(not (pair-found $?))
	=>
	(assert (appendmessagehalt "Вам пары не досталось, но вы там держитесь!"))
)
