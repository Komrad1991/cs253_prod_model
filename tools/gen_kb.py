#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Генератор базы знаний «Deadlock: контр-предметы».

Зачем скрипт: база большая (≈280 фактов и ≈140 правил), и вручную легко
рассинхронизировать словарь тегов (threat/need) и списки контр-предметов.
Скрипт печатает обычный текстовый файл БЗ (data/deadlock_kb.txt), который
дальше читает движок. Файл самодостаточен: его можно править и без скрипта.

Запуск:  python tools/gen_kb.py
"""

from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "data" / "deadlock_kb.txt"

# ---------------------------------------------------------------------------
#  Словарь предметной области
# ---------------------------------------------------------------------------

SLOTS = ["weapon", "vitality", "spirit"]

THREATS = [
    "bullet_dps", "burst_magic", "dot_pressure", "hard_cc", "tank_frontline",
    "dive", "poke", "invisibility", "aoe_channel", "execute", "zone_control",
    "mobility", "sustain",
    # угрозы, которые создаёт предмет, купленный противником в ответ на наш контр-предмет
    "item_silence", "item_regen", "item_immunity", "item_purge", "item_disable",
]

NEEDS = [
    "anti_bullet", "anti_magic", "anti_dot", "anti_cc", "anti_tank", "anti_dive",
    "anti_poke", "anti_invis", "anti_channel", "anti_execute", "anti_zone",
    "anti_mobility", "anti_heal", "sustain_self", "damage_core",
    # ответы на предметы противника (второй контр-предмет)
    "anti_silence", "anti_regen", "anti_immunity", "anti_purge", "anti_disable",
]

# герой: (роль для отчёта, [(угроза, cf, пояснение), ...])
HEROES = {
    "Abrams": ("танк", [
        ("tank_frontline", 0.90, "Abrams играет от передней линии: Infernal Resilience возвращает часть урона в здоровье."),
        ("dive", 0.85, "Shoulder Charge врывается в строй и разбрасывает его."),
        ("hard_cc", 0.80, "Таран и Seismic Impact — жёсткий контроль зоны."),
    ]),
    "Bebop": ("инициатор", [
        ("hard_cc", 0.90, "Hook вытаскивает цель из позиции — жёсткий контроль на дистанции."),
        ("burst_magic", 0.85, "Hyper Beam и Sticky Bomb дают магический берст."),
        ("poke", 0.70, "Держит дистанцию и наказывает за выход вперёд."),
    ]),
    "Dynamo": ("танк-инициатор", [
        ("aoe_channel", 0.90, "Singularity собирает команду в одну точку — канальный AoE-ультимейт."),
        ("hard_cc", 0.85, "Kinetic Pulse и Singularity держат цель."),
        ("tank_frontline", 0.80, "Dynamo играет впереди и имеет большой запас здоровья."),
    ]),
    "GreyTalon": ("снайпер", [
        ("poke", 0.90, "Charged Shot и Rain of Arrows наказывают с дистанции."),
        ("burst_magic", 0.80, "Большой магический залп по площади."),
        ("execute", 0.70, "Guided Owl добивает убегающих."),
    ]),
    "Haze": ("керри", [
        ("bullet_dps", 0.95, "Fixation и пулемётный урон: постоянное пулевое давление."),
        ("invisibility", 0.85, "Smoke Bomb даёт невидимость и уход из фокуса."),
        ("aoe_channel", 0.90, "Bullet Dance — канальный AoE по всем в радиусе."),
    ]),
    "Infernus": ("керри", [
        ("dot_pressure", 0.90, "Catalyst и Afterburn жгут цель длительным уроном."),
        ("aoe_channel", 0.85, "Concussive Combustion — канальный AoE-взрыв."),
        ("zone_control", 0.80, "Flame Dash и огонь держат зону."),
    ]),
    "Ivy": ("саппорт", [
        ("dive", 0.85, "Kudzu и Air Drop залетают в тыл."),
        ("hard_cc", 0.80, "Опутывание лишает мобильности."),
        ("mobility", 0.80, "Быстро меняет позицию и уходит."),
    ]),
    "Kelvin": ("саппорт", [
        ("zone_control", 0.90, "Ice Path и Arctic Beam контролируют пространство."),
        ("aoe_channel", 0.80, "Frozen Shelter — канальный купол, меняющий бой."),
        ("sustain", 0.80, "Лечит союзников и себя."),
    ]),
    "LadyGeist": ("маг", [
        ("burst_magic", 0.85, "Essence Bomb и Malice дают магический берст."),
        ("execute", 0.85, "Soul Exchange меняет здоровье местами — казнь для раненого."),
        ("sustain", 0.75, "Life Drain восстанавливает здоровье."),
    ]),
    "Lash": ("ассасин", [
        ("dive", 0.90, "Death Slam и прыжки — агрессивный заход в тыл."),
        ("hard_cc", 0.85, "Подброс и удар держат цель в воздухе."),
        ("aoe_channel", 0.80, "Death Slam накрывает площадь."),
    ]),
    "McGinnis": ("инженер", [
        ("zone_control", 0.90, "Турели и стена запирают зону."),
        ("bullet_dps", 0.85, "Турели и пулемёт дают устойчивый пулевой урон."),
        ("aoe_channel", 0.80, "Heavy Barrage — канальный обстрел площади."),
    ]),
    "MoKrill": ("танк-инициатор", [
        ("hard_cc", 0.95, "Combo хватает и держит цель — самый жёсткий контроль."),
        ("tank_frontline", 0.85, "Много здоровья, играет впереди."),
        ("dive", 0.85, "Burrow заходит под землёй в тыл."),
    ]),
    "Paradox": ("ассасин", [
        ("burst_magic", 0.85, "Pulse Grenade и Carbine — магический берст."),
        ("hard_cc", 0.80, "Time Wall и Swap ломают позицию."),
        ("mobility", 0.75, "Быстро меняет позицию."),
    ]),
    "Pocket": ("ассасин", [
        ("burst_magic", 0.90, "Barrage и Affliction — сильный магический урон."),
        ("aoe_channel", 0.80, "Affliction накрывает площадь."),
        ("mobility", 0.80, "Satchel и Cloak дают уход."),
    ]),
    "Seven": ("керри", [
        ("aoe_channel", 0.95, "Storm Cloud — канальный AoE, выжигающий зону."),
        ("zone_control", 0.85, "Lightning Ball и Storm держат территорию."),
        ("poke", 0.70, "Static Charge наказывает за кучность."),
    ]),
    "Shiv": ("ассасин", [
        ("dive", 0.90, "Slice and Dice залетает в тыл."),
        ("execute", 0.85, "Killing Blow казнит раненых."),
        ("sustain", 0.85, "Bloodletting откладывает урон и лечится."),
    ]),
    "Vindicta": ("снайпер", [
        ("poke", 0.95, "Снайпер: Assassinate с дистанции."),
        ("execute", 0.85, "Assassinate добивает с большого расстояния."),
        ("mobility", 0.80, "Полёт и уход из-под удара."),
    ]),
    "Viscous": ("танк", [
        ("tank_frontline", 0.85, "The Cube и Goo Ball — живучесть."),
        ("dive", 0.85, "Goo Ball влетает в строй."),
        ("aoe_channel", 0.75, "Puddle Punch и Goo Ball накрывают площадь."),
    ]),
    "Wraith": ("керри", [
        ("bullet_dps", 0.85, "Full Auto — пулевой берст."),
        ("hard_cc", 0.85, "Telekinesis поднимает и держит."),
        ("mobility", 0.70, "Project Mind — телепорт."),
    ]),
    "Yamato": ("ассасин", [
        ("dive", 0.90, "Flying Strike влетает в цель."),
        ("execute", 0.85, "Shadow Transformation и Crimson Slash добивают."),
        ("burst_magic", 0.80, "Power Slash — сильный магический удар."),
    ]),
    "Warden": ("танк", [
        ("tank_frontline", 0.85, "Играет от передней линии."),
        ("zone_control", 0.90, "Ультимейт запирает зону."),
        ("bullet_dps", 0.80, "Пулевое давление с передовой."),
    ]),
    "Mirage": ("стрелок", [
        ("poke", 0.80, "Держит дистанцию и наказывает."),
        ("burst_magic", 0.80, "Fire Beetles и метка дают берст."),
        ("mobility", 0.80, "Телепорт и уход."),
    ]),
    "Holliday": ("инициатор", [
        ("hard_cc", 0.85, "Лассо вытаскивает цель."),
        ("mobility", 0.80, "Быстро перемещается по карте."),
        ("bullet_dps", 0.75, "Пулевой урон вблизи."),
    ]),
    "Sinclair": ("маг", [
        ("burst_magic", 0.85, "Магический берст по площади."),
        ("hard_cc", 0.80, "Контроль и кража ультимейта."),
        ("mobility", 0.70, "Уход и перестановка."),
    ]),
}

# предмет: (слот, цена, активный?, [что закрывает])
ITEMS = {
    # --- оружие ---
    "MysticShot": ("weapon", 500, False, ["damage_core"]),
    "Berserker": ("weapon", 1250, False, ["damage_core"]),
    "ToxicBullets": ("weapon", 3200, False, ["anti_heal", "anti_tank"]),
    "Headhunter": ("weapon", 3500, False, ["anti_poke", "damage_core"]),
    "EscalatingResilience": ("weapon", 3200, False, ["anti_bullet"]),
    "SlowingHex": ("weapon", 1750, True, ["anti_mobility", "anti_dive"]),
    "Sharpshooter": ("weapon", 4250, False, ["anti_poke", "damage_core"]),
    "Inhibitor": ("weapon", 6400, False, ["anti_heal", "anti_mobility", "anti_regen"]),
    "CripplingHeadshot": ("weapon", 6400, False, ["anti_tank"]),
    "HuntersAura": ("weapon", 6400, False, ["anti_tank", "anti_bullet"]),
    "Silencer": ("weapon", 6400, True, ["anti_channel", "anti_mobility"]),
    "SpiritualOverflow": ("weapon", 6400, False, ["damage_core"]),
    # --- живучесть ---
    "ExtraStamina": ("vitality", 500, False, ["anti_dive"]),
    "HealingRite": ("vitality", 500, True, ["sustain_self"]),
    "BulletArmor": ("vitality", 1250, False, ["anti_bullet"]),
    "SpiritArmor": ("vitality", 1250, False, ["anti_magic"]),
    "DebuffReducer": ("vitality", 1250, False, ["anti_dot"]),
    "ReactiveBarrier": ("vitality", 1250, True, ["anti_cc", "anti_magic"]),
    "DivineBarrier": ("vitality", 3000, True, ["anti_magic", "anti_execute"]),
    "MetalSkin": ("vitality", 3000, True, ["anti_bullet", "anti_channel"]),
    "ReturnFire": ("vitality", 3000, True, ["anti_bullet"]),
    "SuperiorStamina": ("vitality", 4250, False, ["anti_dive"]),
    "Fortitude": ("vitality", 4250, False, ["anti_poke", "sustain_self"]),
    "DebuffRemover": ("vitality", 4250, True, ["anti_dot", "anti_cc"]),
    "Colossus": ("vitality", 6400, True, ["anti_dive", "sustain_self"]),
    "SoulRebirth": ("vitality", 6400, False, ["anti_execute"]),
    "DivinersKevlar": ("vitality", 6400, False, ["anti_magic", "anti_cc", "anti_disable"]),
    "Leech": ("vitality", 6400, False, ["sustain_self"]),
    # --- дух ---
    "MysticBurst": ("spirit", 500, False, ["damage_core"]),
    "Knockdown": ("spirit", 3000, True, ["anti_channel", "anti_poke"]),
    "EtherealShift": ("spirit", 3000, True, ["anti_execute", "anti_zone"]),
    "WarpStone": ("spirit", 3000, True, ["anti_dive", "anti_zone"]),
    "SilenceGlyph": ("spirit", 3000, True, ["anti_channel", "anti_mobility", "anti_invis"]),
    "EscalatingExposure": ("spirit", 4250, False, ["anti_tank"]),
    "Curse": ("spirit", 6400, True, ["anti_channel", "anti_mobility", "anti_heal", "anti_immunity", "anti_purge"]),
    "Unstoppable": ("spirit", 6400, True, ["anti_cc", "anti_silence"]),
    "BoundlessSpirit": ("spirit", 6400, False, ["damage_core", "sustain_self"]),
}

# угроза -> потребность (правило с собственным cf и объяснением)
NEED_RULES = [
    ("bullet_dps", "anti_bullet", 0.85, "Пулевое давление снимается бронёй от пуль и предметами, блокирующими пули."),
    ("burst_magic", "anti_magic", 0.85, "Магический берст требует сопротивления духу или барьера."),
    ("dot_pressure", "anti_dot", 0.85, "Длительный урон снимается очищением дебаффов."),
    ("hard_cc", "anti_cc", 0.90, "Жёсткий контроль требует иммунитета или сокращения длительности."),
    ("tank_frontline", "anti_tank", 0.80, "Танка нельзя пробить без снижения брони и процентов здоровья."),
    ("dive", "anti_dive", 0.85, "Заход в тыл требует мобильности и живучести под фокусом."),
    ("poke", "anti_poke", 0.80, "Дальний урон снимается регенерацией и дистанцией."),
    ("invisibility", "anti_invis", 0.80, "Невидимость наказывается сайленсом и замедлением."),
    ("aoe_channel", "anti_channel", 0.90, "Канальный AoE срывается сайленсом, станом или иммунитетом к пулям."),
    ("execute", "anti_execute", 0.85, "Казнь предотвращается неуязвимостью, воскрешением и щитами."),
    ("zone_control", "anti_zone", 0.75, "Зону контроля нужно покидать телепортом или переживать."),
    ("mobility", "anti_mobility", 0.70, "Мобильную цель нужно замедлять и лишать рывков."),
    ("sustain", "anti_heal", 0.85, "Лечение и вампиризм снимаются сокращением лечения."),
]

EXTRA_NEED_RULES = [
    ("poke", "sustain_self", 0.70, "Против дальнего урона помогает собственный регенерация и восстановление."),
    ("dot_pressure", "sustain_self", 0.65, "Против длительного урона нужен собственный запас восстановления."),
    ("tank_frontline", "damage_core", 0.70, "Против танка нужен собственный устойчивый урон, иначе его не пробить."),
]

# Основная потребность для каждой угрозы — используется в обратном запросе
# «выбранный предмет -> какие враги остаются для него опасными».
PRIMARY_NEED: dict[str, str] = {}
for _threat, _need, _cf, _why in NEED_RULES + EXTRA_NEED_RULES:
    PRIMARY_NEED.setdefault(_threat, _need)

# Угрозы, которые создаёт герой (для обратного запроса нужны как факты).
HERO_TAGS = sorted({tag for _, (_, traits) in HEROES.items() for tag, _, _ in traits})

COMBOS = [
    ("bullet_dps", "invisibility", "anti_invis", 0.90, "Невидимость плюс пулевой урон: без контроля невидимости пулевое давление не остановить."),
    ("aoe_channel", "hard_cc", "anti_cc", 0.90, "Канал под прикрытием контроля: нужен иммунитет или срыв канала."),
    ("execute", "poke", "anti_execute", 0.88, "Добивающий урон с дистанции: нужен предмет, переживающий казнь."),
    ("dive", "tank_frontline", "anti_dive", 0.85, "Танк врывается в тыл: нужна мобильность и живучесть."),
    ("dot_pressure", "sustain", "anti_heal", 0.88, "Урон по времени плюс лечение: без сокращения лечения цель не убить."),
    ("burst_magic", "execute", "anti_magic", 0.80, "Магический берст добивает: нужен барьер от духа."),
    ("bullet_dps", "tank_frontline", "anti_tank", 0.80, "Пулевой танк: нужно снижение брони и процентов здоровья."),
    ("zone_control", "aoe_channel", "anti_zone", 0.85, "Зона плюс канал: из зоны нужно выходить, а канал срывать."),
]

# жёсткие контр-предметы: предмет закрывает потребность «целиком»
HARD_COUNTERS = [
    ("MetalSkin", "anti_bullet"),
    ("MetalSkin", "anti_channel"),
    ("BulletArmor", "anti_bullet"),
    ("ReturnFire", "anti_bullet"),
    ("EscalatingResilience", "anti_bullet"),
    ("HuntersAura", "anti_bullet"),
    ("SpiritArmor", "anti_magic"),
    ("DivineBarrier", "anti_magic"),
    ("DivinersKevlar", "anti_magic"),
    ("ReactiveBarrier", "anti_cc"),
    ("Unstoppable", "anti_cc"),
    ("DebuffRemover", "anti_dot"),
    ("DebuffRemover", "anti_cc"),
    ("DebuffReducer", "anti_dot"),
    ("EtherealShift", "anti_execute"),
    ("SoulRebirth", "anti_execute"),
    ("Knockdown", "anti_channel"),
    ("Curse", "anti_channel"),
    ("SilenceGlyph", "anti_channel"),
    ("ToxicBullets", "anti_heal"),
    ("Inhibitor", "anti_heal"),
    ("Curse", "anti_heal"),
    ("CripplingHeadshot", "anti_tank"),
    ("EscalatingExposure", "anti_tank"),
    ("SlowingHex", "anti_mobility"),
    ("Silencer", "anti_mobility"),
    ("SilenceGlyph", "anti_mobility"),
    ("Colossus", "anti_dive"),
    ("WarpStone", "anti_dive"),
    ("Fortitude", "anti_poke"),
    ("Knockdown", "anti_poke"),
    ("HealingRite", "sustain_self"),
    ("Leech", "sustain_self"),
    ("Sharpshooter", "anti_poke"),
    # второй контр-предмет: ответ на предмет, который купил противник
    ("Unstoppable", "anti_silence"),
    ("Inhibitor", "anti_regen"),
    ("Curse", "anti_immunity"),
    ("Curse", "anti_purge"),
    ("DivinersKevlar", "anti_disable"),
]

# Цепочка «наш предмет -> ответ врага -> наша вторая потребность -> второй контр-предмет».
# (наш предмет, наша потребность, предмет врага, угроза от него, вторая потребность,
#  второй контр-предмет, пояснение)
ANSWER_CHAINS = [
    ("MetalSkin", "anti_bullet", "SilenceGlyph", "item_silence", "anti_silence", "Unstoppable",
     "Metal Skin — активный предмет: враг берёт Silence Glyph, чтобы сайленсить его активацию."),
    ("ToxicBullets", "anti_heal", "Fortitude", "item_regen", "anti_regen", "Inhibitor",
     "Против нашего сокращения лечения враг берёт Fortitude с регенерацией — нужен Inhibitor."),
    ("Knockdown", "anti_poke", "Unstoppable", "item_immunity", "anti_immunity", "Curse",
     "Unstoppable снимает наш Knockdown — отвечаем Curse, который отключает предметы."),
    ("SlowingHex", "anti_mobility", "DebuffRemover", "item_purge", "anti_purge", "Curse",
     "Debuff Remover снимает наше замедление — Curse не даёт применить очистку."),
    ("EtherealShift", "anti_execute", "Curse", "item_disable", "anti_disable", "DivinersKevlar",
     "Curse отключает Ethereal Shift — пассивный Diviner's Kevlar продолжает работать."),
]

# ситуативные правила «герой + потребность -> конкретный предмет»
SITUATIONAL = [
    ("Haze", "anti_channel", "MetalSkin", 0.97, "Metal Skin перекрывает окно Bullet Dance: пули не попадают."),
    ("Seven", "anti_channel", "Curse", 0.95, "Curse снимает канал Storm Cloud и запрещает предметы."),
    ("Vindicta", "anti_poke", "Knockdown", 0.93, "Knockdown сбивает летящую Vindicta и срывает позицию."),
    ("MoKrill", "anti_cc", "Unstoppable", 0.95, "Unstoppable не даёт Combo удержать цель."),
    ("Infernus", "anti_dot", "DebuffRemover", 0.94, "Debuff Remover снимает Catalyst и Afterburn."),
    ("LadyGeist", "anti_execute", "EtherealShift", 0.92, "Ethereal Shift переживает Soul Exchange."),
    ("Abrams", "anti_heal", "ToxicBullets", 0.90, "Toxic Bullets режут лечение Abrams и его регенерацию."),
    ("Wraith", "anti_cc", "ReactiveBarrier", 0.88, "Reactive Barrier гасит Telekinesis и даёт щит."),
    ("Yamato", "anti_execute", "SoulRebirth", 0.92, "Soul Rebirth возвращает в бой после Killing Blow."),
    ("Bebop", "anti_magic", "ReactiveBarrier", 0.90, "Reactive Barrier съедает Hook-комбо и магический берст."),
    ("Pocket", "anti_magic", "DivineBarrier", 0.88, "Divine Barrier перекрывает Affliction."),
    ("Shiv", "anti_dive", "Colossus", 0.90, "Colossus переживает заход Shiv и даёт контроль."),
    ("Lash", "anti_dive", "WarpStone", 0.88, "Warp Stone выводит из-под Death Slam."),
    ("Kelvin", "anti_zone", "EtherealShift", 0.85, "Ethereal Shift переживает купол и обстрел."),
    ("McGinnis", "anti_channel", "SilenceGlyph", 0.90, "Silence Glyph срывает Heavy Barrage."),
    ("Dynamo", "anti_channel", "Curse", 0.93, "Curse срывает Singularity до сбора команды."),
    ("GreyTalon", "anti_poke", "Sharpshooter", 0.80, "Sharpshooter отвечает на дальний урон."),
    ("Viscous", "anti_dive", "WarpStone", 0.82, "Warp Stone уводит из-под Goo Ball."),
]

WARNINGS = [
    ("Seven", "aoe_channel", "channel_ult_threat", 0.95, "Storm Cloud накрывает точку — нужен срыв канала или выход из зоны."),
    ("Abrams", "sustain", "sustain_tank", 0.90, "Abrams живёт за счёт лечения: без сокращения лечения он не умирает."),
    ("MoKrill", "hard_cc", "combo_lockdown", 0.92, "Combo — самый длинный контроль в игре: нужен иммунитет."),
    ("Haze", "aoe_channel", "haze_ult_window", 0.90, "Bullet Dance убивает за одно окно: нужен Metal Skin или срыв."),
    ("Vindicta", "execute", "execute_window", 0.85, "Assassinate добивает с дистанции: нужен предмет, переживающий казнь."),
]

ENEMIES = [("Haze", 0.95), ("Seven", 0.90), ("Abrams", 0.85), ("Vindicta", 0.80), ("MoKrill", 0.75)]
MY_HERO = "Infernus"


def tier(cost: int) -> str:
    if cost < 1500:
        return "cheap"
    if cost < 4000:
        return "mid"
    return "expensive"


def check() -> None:
    for hero, (_, traits) in HEROES.items():
        for tag, cf, _ in traits:
            assert tag in THREATS, f"{hero}: неизвестная угроза {tag}"
            assert 0.0 <= cf <= 1.0
    for item, (slot, cost, active, provides) in ITEMS.items():
        assert slot in SLOTS, item
        for need in provides:
            assert need in NEEDS, f"{item}: неизвестная потребность {need}"
    covered = {need for _, _, _, provides in ITEMS.values() for need in provides}
    missing = [n for n in NEEDS if n not in covered]
    assert not missing, f"потребности без предметов: {missing}"
    for item, need in HARD_COUNTERS:
        assert item in ITEMS, item
        assert need in ITEMS[item][3], f"{item} не закрывает {need}"
    for hero, need, item, cf, _ in SITUATIONAL:
        assert hero in HEROES, hero
        assert item in ITEMS, item
        assert need in ITEMS[item][3], f"{item} не закрывает {need}"
    threats = {t for _, (_, traits) in HEROES.items() for t, _, _ in traits}
    assert threats <= set(THREATS)
    assert threats <= set(PRIMARY_NEED), f"нет потребности для угроз: {threats - set(PRIMARY_NEED)}"
    for our_item, our_need, enemy_item, threat_tag, second_need, second_item, _ in ANSWER_CHAINS:
        assert our_item in ITEMS, our_item
        assert enemy_item in ITEMS, enemy_item
        assert second_item in ITEMS, second_item
        assert our_need in ITEMS[our_item][3], f"{our_item} не закрывает {our_need}"
        assert threat_tag in THREATS, threat_tag
        assert second_need in NEEDS, second_need
        assert second_need in ITEMS[second_item][3], f"{second_item} не закрывает {second_need}"
        assert (second_item, second_need) in HARD_COUNTERS, (second_item, second_need)


def main() -> None:
    check()
    lines: list[str] = []
    add = lines.append

    add("# ============================================================================")
    add("#  Deadlock: контр-предметы — база знаний продукционной модели")
    add("#")
    add("#  Предметная область: герои Deadlock, предметы и контр-предметы.")
    add("#  Цепочка вывода: герой -> угроза -> наш контр-предмет -> предмет врага")
    add("#  в ответ -> второй контр-предмет.")
    add("#  Формат: обычный текстовый файл (.txt); генератор — tools/gen_kb.py.")
    add("#")
    add("#  Слои вывода: 0 состав -> 1 угрозы -> 2 потребности -> 3 кандидаты")
    add("#               -> 4 шортлист -> 5 выбор -> 6 предмет врага в ответ")
    add("#               -> 7 его угроза -> 8 вторая потребность -> 9 второй контр")
    add("# ============================================================================")
    add("")
    add('kb "Deadlock: контр-предметы под состав противника"')
    add('description "Продукционная модель: герои -> угрозы -> потребности -> контр-предметы. '
        'Коэффициенты уверенности отражают силу угрозы и надёжность ответа."')
    add("")

    add("# --- сорта (домены для перебора целей с переменными) ---")
    add("sort hero = " + ", ".join(HEROES))
    add("sort item = " + ", ".join(ITEMS))
    add("sort threat = " + ", ".join(THREATS))
    add("sort need = " + ", ".join(NEEDS))
    add("sort slot = " + ", ".join(SLOTS))
    add("")

    add("# --- факты: герои ---")
    for hero, (role, _) in HEROES.items():
        add(f"fact hero({hero})")
    add("")
    add("# --- факты: профили героев (какие угрозы создаёт герой) ---")
    for hero, (_, traits) in HEROES.items():
        for tag, cf, _ in traits:
            add(f"fact hero_tag({hero}, {tag}) cf={cf:.2f}")
    add("")

    add("# --- факты: предметы ---")
    for item, (slot, cost, active, provides) in ITEMS.items():
        add(f"fact item({item})")
        add(f"fact item_slot({item}, {slot})")
        add(f"fact item_cost({item}, {cost})")
        add(f"fact item_tier({item}, {tier(cost)})")
        if active:
            add(f"fact item_active({item})")
        for need in provides:
            add(f"fact item_provides({item}, {need})")
        add("")

    add("# --- факты: жёсткие контр-предметы (закрывают потребность целиком) ---")
    for item, need in HARD_COUNTERS:
        add(f"fact hard_counter({item}, {need})")
    add("")

    add("# --- факты: чем противник отвечает на наш контр-предмет ---")
    for our_item, _, enemy_item, _, _, _, _ in ANSWER_CHAINS:
        add(f"fact enemy_answers({our_item}, {enemy_item})")
    add("")

    add("# Входные данные (состав противника) сюда не входят: это отдельные файлы")
    add("# data/input/*.txt, где только строки fact enemy(...).")
    add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: слой 1 — состав противника превращается в угрозы")
    add("# ============================================================================")
    add("")
    for hero, (role, traits) in HEROES.items():
        for tag, cf, why in traits:
            rid = f"THR_{hero.upper()}_{tag.upper()}"
            add(f"rule {rid} cf={cf:.2f}")
            add(f"  if enemy({hero})")
            add(f"  then threat({tag})")
            add(f'  why "{why}"')
            add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: слой 2 — угрозы превращаются в потребности сборки")
    add("# ============================================================================")
    add("")
    for threat, need, cf, why in NEED_RULES + EXTRA_NEED_RULES:
        rid = f"NEED_{threat.upper()}_{need.upper()}"
        add(f"rule {rid} cf={cf:.2f}")
        add(f"  if threat({threat})")
        add(f"  then need({need})")
        add(f'  why "{why}"')
        add("")

    add("# --- комбинации угроз: две угрозы вместе требуют отдельного ответа ---")
    for t1, t2, need, cf, why in COMBOS:
        rid = f"COMBO_{t1.upper()}_{t2.upper()}"
        add(f"rule {rid} cf={cf:.2f}")
        add(f"  if threat({t1}) and threat({t2})")
        add(f"  then need({need})")
        add(f'  why "{why}"')
        add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: слой 3 — потребность + предмет -> кандидат в сборку")
    add("# ============================================================================")
    add("")
    add("rule CAND_GENERIC cf=0.75")
    add("  if need(?n) and item_provides(?i, ?n)")
    add("  then candidate(?i, ?n)")
    add('  why "Любой предмет, который закрывает потребность, становится кандидатом."')
    add("")
    for item, need in HARD_COUNTERS:
        rid = f"CNT_{item.upper()}_{need.upper()}"
        add(f"rule {rid} cf=0.93")
        add(f"  if need({need}) and hard_counter({item}, {need})")
        add(f"  then candidate({item}, {need})")
        add(f'  why "{item} закрывает потребность {need} целиком, а не частично."')
        add("")
    for hero, need, item, cf, why in SITUATIONAL:
        rid = f"SIT_{hero.upper()}_{item.upper()}"
        add(f"rule {rid} cf={cf:.2f}")
        add(f"  if need({need}) and enemy({hero})")
        add(f"  then candidate({item}, {need})")
        add(f'  why "{why}"')
        add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: слои 4-6 — шортлист, выбор, рекомендация")
    add("# ============================================================================")
    add("")
    add("rule BUILD_SHORTLIST cf=1.00")
    add("  if candidate(?i, ?n)")
    add("  then shortlist(?i, ?n)")
    add('  why "Кандидат проходит в шортлист сборки."')
    add("")
    add("rule BUILD_PICK cf=0.95")
    add("  if shortlist(?i, ?n)")
    add("  then pick(?i, ?n)")
    add('  why "Шортлист превращается в конкретный выбор предмета."')
    add("")
    add("rule BUILD_RECOMMEND cf=1.00")
    add("  if pick(?i, ?n)")
    add("  then recommended(?i, ?n)")
    add('  why "Выбранный предмет становится рекомендацией с обоснованием."')
    add("")
    add("rule BUILD_ACTIVE cf=1.00")
    add("  if pick(?i, ?n) and item_active(?i)")
    add("  then active_pick(?i, ?n)")
    add('  why "Активные предметы занимают отдельный слот актива, их число ограничено."')
    add("")
    add("rule BUILD_EXPENSIVE cf=1.00")
    add("  if pick(?i, ?n) and item_tier(?i, expensive)")
    add("  then expensive_pick(?i, ?n)")
    add('  why "Дорогой предмет требует отдельного бюджетного решения."')
    add("")
    add("rule BUILD_CHEAP cf=1.00")
    add("  if pick(?i, ?n) and item_tier(?i, cheap)")
    add("  then cheap_pick(?i, ?n)")
    add('  why "Дешёвый предмет можно взять раньше, не дожидаясь полного бюджета."')
    add("")
    add("rule PRIO_HAZE_ANTIBULLET cf=0.95")
    add("  if enemy(Haze) and threat(bullet_dps)")
    add("  then need(anti_bullet)")
    add('  why "Haze — главный источник пулевого давления в этом составе: защита от пуль обязательна."')
    add("")
    add("rule PRIO_SEVEN_ANTICHANNEL cf=0.93")
    add("  if enemy(Seven) and threat(aoe_channel)")
    add("  then need(anti_channel)")
    add('  why "Storm Cloud Seven — решающий канал боя: нужен срыв канала, а не просто защита."')
    add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: предупреждения о ключевых угрозах")
    add("# ============================================================================")
    add("")
    for hero, need, warn, cf, why in WARNINGS:
        rid = f"WARN_{hero.upper()}_{need.upper()}"
        add(f"rule {rid} cf={cf:.2f}")
        add(f"  if enemy({hero}) and need({need})")
        add(f"  then warning({warn})")
        add(f'  why "{why}"')
        add("")
    add("rule WARN_TWO_DEFENSES cf=0.80")
    add("  if need(anti_bullet) and need(anti_magic)")
    add("  then warning(two_defense_slots)")
    add('  why "Нужны две линии защиты одновременно — планируйте слоты и бюджет."')
    add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: ответ врага. Противник видит наш контр-предмет и покупает")
    add("#  свой — под него нужен ВТОРОЙ контр-предмет.")
    add("# ============================================================================")
    add("")
    for our_item, our_need, enemy_item, _, _, _, why in ANSWER_CHAINS:
        rid = f"ANS_{our_item.upper()}_{enemy_item.upper()}"
        add(f"rule {rid} cf=0.92")
        add(f"  if pick({our_item}, {our_need})")
        add(f"  then enemy_buys({enemy_item})")
        add(f'  why "{why}"')
        add("")

    seen_items: set[str] = set()
    for _, _, enemy_item, threat_tag, _, _, _ in ANSWER_CHAINS:
        if enemy_item in seen_items:
            continue
        seen_items.add(enemy_item)
        rid = f"ANS_THREAT_{enemy_item.upper()}"
        add(f"rule {rid} cf=0.93")
        add(f"  if enemy_buys({enemy_item})")
        add(f"  then threat({threat_tag})")
        add(f'  why "{enemy_item} в руках врага — новая угроза: {threat_tag}."')
        add("")

    seen_threats: set[str] = set()
    for _, _, _, threat_tag, second_need, _, _ in ANSWER_CHAINS:
        if threat_tag in seen_threats:
            continue
        seen_threats.add(threat_tag)
        rid = f"ANS_NEED_{threat_tag.upper()}"
        add(f"rule {rid} cf=0.90")
        add(f"  if threat({threat_tag})")
        add(f"  then need({second_need})")
        add(f'  why "Угроза {threat_tag} закрывается потребностью {second_need}."')
        add("")

    seen_pairs: set[tuple[str, str]] = set()
    for _, _, enemy_item, _, second_need, second_item, _ in ANSWER_CHAINS:
        if (second_item, second_need) in seen_pairs:
            continue
        seen_pairs.add((second_item, second_need))
        rid = f"SEC_{second_item.upper()}_{second_need.upper()}"
        add(f"rule {rid} cf=0.95")
        add(f"  if need({second_need}) and hard_counter({second_item}, {second_need})")
        add(f"  then second_pick({second_item}, {second_need})")
        add(f'  why "{second_item} — второй контр-предмет: закрывает {second_need} '
            f'после ответа врага ({enemy_item})."')
        add("")

    add("# ============================================================================")
    add("#  ПРАВИЛА: обратный запрос «выбранный предмет -> какие враги опасны»")
    add("#  uncovered(предмет, герой, угроза): предмет не закрывает эту угрозу героя.")
    add("# ============================================================================")
    add("")
    for tag in HERO_TAGS:
        need = PRIMARY_NEED[tag]
        rid = f"REV_{tag.upper()}"
        add(f"rule {rid} cf=0.90")
        add(f"  if our_item(?i)")
        add(f"  and hero_tag(?h, {tag})")
        add(f"  and not item_provides(?i, {need})")
        add(f"  then uncovered(?i, ?h, {tag})")
        add(f'  why "{tag}: предмет не даёт ответа на потребность {need} — герой с такой угрозой остаётся опасным."')
        add("")

    add("# ============================================================================")
    add("#  ЦЕЛИ. Входные данные (состав противника) задаются отдельно — файлами")
    add("#  data/input/*.txt, в которых только строки fact enemy(...).")
    add("# ============================================================================")
    add("")
    add("# все рекомендации: перебор по сортам item x need")
    add("goal recommended(?i:item, ?n:need)")
    add("")
    add("# вторые контр-предметы (ответ на предмет, который возьмёт враг)")
    add("goal second_pick(?i:item, ?n:need)")
    add("")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines), encoding="utf-8")
    rules = sum(1 for line in lines if line.startswith("rule "))
    facts = sum(1 for line in lines if line.startswith("fact "))
    print(f"Записано: {OUT}")
    print(f"  фактов: {facts}, правил: {rules}, строк: {len(lines)}")


if __name__ == "__main__":
    main()
