extends Node
## Original companion mod for the community Mod Loader 1.2.2.
## Reads game objects on the main thread; writes only user://batomon-helper/.
const OUTPUT_DIR := "user://batomon-helper"
const SHOP_SCRIPT := "res://game/states/shop_state.gd"
const BATTLE_SCRIPT := "res://game/states/battle_state.gd"
const STAT_NAMES := {1: "Damage", 2: "Heal", 3: "Shield", 4: "Burn", 5: "Poison", 6: "Shock", 7: "Multicast"}
const RARITIES := ["Common", "Uncommon", "Rare", "SuperRare", "Legendary"]
var _next_poll := 0
var _sequence := 0
var _last_error := ""
var _battle_player_id := 0
var _battle_snapshot := {}

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT_DIR))

func _process(_delta: float) -> void:
	var now := Time.get_ticks_msec()
	if now < _next_poll:
		return
	_next_poll = now + 500
	_publish()

func _publish() -> void:
	_sequence += 1
	var message := {"protocolVersion": 1, "sequence": _sequence,
		"updatedAt": Time.get_datetime_string_from_system(true) + "Z",
		"processId": OS.get_process_id(), "phase": "other", "board": null}
	var main = get_tree().current_scene
	var state = main.get("current_state") if _has(main, "current_state") else null
	if _script_is(state, SHOP_SCRIPT):
		_battle_player_id = 0
		message.phase = "shop"
		var run = get_node_or_null("/root/RunManager")
		run = run.get("data") if run != null else null
		var manager = state.get("manager") if _has(state, "manager") else null
		var view = state.get("view") if _has(state, "view") else null
		if run == null or manager == null or view == null or not view.is_node_ready():
			message.error = "Shop is initializing."
		elif not _compatible(run, manager, view):
			message.error = "Game fields changed; update BatomonHelper Bridge."
		else:
			message.board = read_board(run, manager, view)
			message.combat = read_combat(run)
	elif _script_is(state, BATTLE_SCRIPT):
		message.phase = "battle"
		# TEMPLATE copies are captured before start effects and remain stable throughout replays.
		var player = state.get("_replay_p1_data") if _has(state, "_replay_p1_data") else null
		var opponent = state.get("_replay_p2_data") if _has(state, "_replay_p2_data") else null
		if player == null or opponent == null:
			message.error = "Battle teams are initializing."
		else:
			if _battle_player_id != player.get_instance_id():
				_battle_snapshot = {"player": read_battle_side(player, int(player.current_round)),
					"opponent": read_battle_side(opponent, int(player.current_round), true)}
				_battle_player_id = player.get_instance_id()
			message.battle = _battle_snapshot.duplicate()
			message.battle.outcome = read_battle_outcome(player)
			# These are observations of the game's visual replay, never a second
			# simulation. They make captured runs useful for auditing simulator rules.
			var frame = read_battle_frame(state)
			if frame != null:
				message.battle.frame = frame
	var pending := OUTPUT_DIR.path_join("state.pending")
	var file := FileAccess.open(pending, FileAccess.WRITE)
	if file == null:
		_warn("Cannot write bridge state: " + error_string(FileAccess.get_open_error()))
		return
	file.store_string(JSON.stringify(message))
	file.close()
	var err := DirAccess.rename_absolute(ProjectSettings.globalize_path(pending), ProjectSettings.globalize_path(OUTPUT_DIR.path_join("state.json")))
	if err != OK:
		_warn("Cannot publish bridge state: " + error_string(err))

func read_board(run, manager, view) -> Dictionary:
	var weights: Dictionary = view.rank_ui.get("_last_weights")
	var total := 0.0
	for value in weights.values():
		total += float(value)
	var odds := []
	for i in range(5):
		odds.append({"rarity": RARITIES[i], "probability": float(weights.get(i + 1, 0)) / total if total > 0 else 0})
	var items: Array = manager.current_shop_items
	var item_counter: bool = view.item_uses_label.visible
	var free_rerolls: int = run.get_free_rerolls_available()
	# Read the game's counter text directly; it already includes berry overrides.
	var uses := str(view.item_uses_label.text).split(" ")[0].split("/") if item_counter else PackedStringArray()
	return {"trainerId": str(run.trainer_id), "day": int(run.current_round), "lives": int(run.lives),
		"wins": int(run.current_wins), "winTarget": int(str(view.wins_count_label.text).get_slice("/", 1)),
		"teamHp": int(run.player_hp), "gold": int(run.gold), "shopRank": int(run.shop_rank),
		"rarityOdds": odds, "rerollCost": null if free_rerolls > 0 else int(manager.reroll_cost),
		"freeRerolls": free_rerolls if free_rerolls > 0 else null,
		"itemUsesCounterVisible": item_counter,
		"itemUsesRemaining": int(uses[0]) if uses.size() == 2 else null,
		"itemUsesMaximum": int(uses[1]) if uses.size() == 2 else null,
		"shopLocked": bool(manager.is_shop_frozen), "regionFilter": read_region(run),
		# The game stores both grids from their visual bottom-right back to top-left.
		# Export screen order so slot 0 is always the top-left card in the helper,
		# training data and combat position rules.
		"team": read_slots(run.team, 6, false, int(run.gold), true),
		"bench": read_slots(run.bench, 4, false, int(run.gold), true),
		"shop": read_slots(items, 5, true, int(run.gold)),
		"capturedAt": Time.get_datetime_string_from_system(true) + "Z"}

func read_region(run) -> String:
	# Some builds expose the selected region directly.
	for key in ["region", "region_id", "current_region", "selected_region"]:
		if not _has(run, key):
			continue
		var value = run.get(key)
		var name := str(value).to_lower()
		if "jinto" in name:
			return "Jinto"
		if "pantra" in name:
			return "Pantra"
		# Region enums in the supported builds use Pantra = 0 and Jinto = 1.
		if value is int:
			if value == 0: return "Pantra"
			if value == 1: return "Jinto"
	# The current build stores the selection as the active species-set ID instead.
	# Starter is the game's Pantra pool; Oshima is the Jinto pool. Do not infer
	# a region from the offers, because effects can put foreign creatures in shop.
	if _has(run, "active_set_id"):
		match str(run.get("active_set_id")).to_lower():
			"starter", "pantra":
				return "Pantra"
			"oshima", "jinto":
				return "Jinto"
	push_warning("BatomonHelper Bridge: Cannot read the active run region or set.")
	return "All"

func read_slots(source: Array, count: int, shop: bool, gold: int, reverse_visual_order := false, opponent := false) -> Array:
	var slots := []
	for i in range(count):
		var source_index := count - 1 - i if reverse_visual_order else i
		if opponent and count == 6:
			# Opponent data is stored in the opponent's perspective. Flip columns
			# so exported slots use the player's screen coordinates.
			source_index = (i + 3) % 6
		var value = source[source_index] if source_index < source.size() else null
		var slot := {"index": i, "occupancy": "Empty", "stats": []}
		if value != null:
			if value.has_method("get_gold_cost") and _has(value, "current_stats"):
				slot.occupancy = "Creature"
				slot.definitionId = str(value.data.id)
				slot.variant = "Shiny" if value.is_shiny else "Normal"
				slot.level = int(value.level)
				for stat in STAT_NAMES:
					var amount := int(value.get_stat(stat))
					if amount != 0 and (stat != 7 or amount > 1):
						slot.stats.append({"kind": STAT_NAMES[stat], "value": amount})
				if shop:
					slot.price = int(value.get_gold_cost())
			elif _has(value, "id") and _has(value, "cost") and shop:
				slot.occupancy = "Item"
				slot.definitionId = str(value.id)
				slot.price = int(value.cost)
			else:
				slot.occupancy = "Unknown"
			if slot.has("price"):
				slot.affordable = slot.price <= gold
		slots.append(slot)
	return slots

func read_combat(run) -> Dictionary:
	return {"team": read_combat_units(run.team), "bench": read_combat_units(run.bench), "trinkets": run.trinket_ids}

func read_battle_side(run, day: int, opponent := false) -> Dictionary:
	# Replay templates omit passive caches. Rebuild them only on an isolated copy,
	# so player_hp includes the game's trainer/trinket HP without changing the run.
	run = run.duplicate_deep(RunData.CopyMode.BATTLE)
	return {"day": day, "teamHp": int(run.player_hp), "trainerId": str(run.trainer_id),
		"rank": int(run.shop_rank), "wins": int(run.current_wins),
		"team": read_slots(run.team, 6, false, int(run.gold), true, opponent),
		"combat": {"team": read_combat_units(run.team, opponent), "trinkets": run.trinket_ids}}

func read_battle_outcome(player):
	# The game's ghost simulation promotes the completed run before the visual
	# battle ends. Read its committed result; never rerun combat or vary seeds.
	var manager = get_node_or_null("/root/RunManager")
	var completed = manager.get("data") if manager != null else null
	if completed == null or completed.run_id != player.run_id or completed.current_round != player.current_round:
		return null
	if not completed.pending_round_start or not completed.pending_battle_opponent.is_empty():
		return null
	return {"playerWon": int(completed.current_wins) > int(player.current_wins)}

func read_battle_frame(state):
	var simulation = state.get("simulation") if _has(state, "simulation") else null
	if simulation == null or not _has(simulation, "data"):
		return null
	var data = simulation.get("data")
	if data == null or not _has(data, "time_elapsed") or not _has(data, "battle_units"):
		return null
	return {"elapsedSeconds": float(data.time_elapsed),
		"player": read_battle_frame_side(data, 0),
		"opponent": read_battle_frame_side(data, 1)}

func read_battle_frame_side(data, team_id: int) -> Dictionary:
	return {"health": float(data.player_health[team_id]), "maximumHealth": float(data.max_health[team_id]),
		"shield": float(data.team_shield[team_id]), "burn": int(data.team_burn[team_id]),
		"poison": int(data.team_poison[team_id]), "shock": int(data.team_shock[team_id]),
		"units": read_battle_frame_units(data.battle_units, team_id)}

func read_battle_frame_units(units: Array, team_id: int) -> Array:
	var output := []
	# The stat order intentionally matches read_combat_units / GameCombatUnit:
	# damage, heal, shield, burn, poison, shock, cooldown speed, multicast, sell.
	var stat_ids := [1, 2, 3, 4, 5, 6, 10, 7, "bonus_sell_value"]
	for unit in units:
		if unit == null or int(unit.team_id) != team_id:
			continue
		var stats := []
		for stat in stat_ids:
			stats.append(float(unit.get_stat(stat)))
		# Keep opponent frame slots in the same player-screen coordinates as the
		# battle-start template above.
		var slot := int(unit.slot_index)
		if team_id == 1:
			slot = (slot + 3) % 6
		output.append({"slot": slot, "alive": not bool(unit.is_dead),
			"casts": int(unit.cast_count), "stats": stats,
			"damageDealt": float(unit.damage_dealt), "healingDone": float(unit.healing_done),
			"shieldGranted": float(unit.shield_granted)})
	return output

func read_combat_units(source: Array, opponent := false) -> Array:
	var units := []
	# Simulator order: damage/heal/shield/burn/poison/shock/speed/multicast/sell.
	var stat_ids := [1, 2, 3, 4, 5, 6, 10, 7, "bonus_sell_value"]
	var multipliers := {1: 8, 2: 13, 3: 12, 4: 15, 5: 14, 6: 9}
	for i in range(source.size()):
		var monster = source[i]
		if monster == null: continue
		var stats := []
		for stat in stat_ids:
			var value: float = monster.get_base_permanent_stat(stat)
			if multipliers.has(stat):
				value = round(value * (1.0 + monster.get_base_permanent_stat(multipliers[stat])))
			stats.append(value)
		var cooldown = monster.get_base_cooldown()
		var slot := source.size() - 1 - i
		if opponent and source.size() == 6:
			# Opponent data is stored in the opponent's perspective. Flip columns
			# so exported slots use the player's screen coordinates.
			slot = (i + 3) % 6
		units.append({"slot": slot, "permanentStats": stats, "baseCooldown": cooldown if cooldown > 0 else null,
			"addedTypes": monster.get_extra_types()})
	return units

func _compatible(run, manager, view) -> bool:
	for key in ["trainer_id", "current_round", "lives", "current_wins", "player_hp", "gold", "shop_rank", "team", "bench"]:
		if not _has(run, key): return false
	for key in ["current_shop_items", "reroll_cost", "is_shop_frozen"]:
		if not _has(manager, key): return false
	if run.team.size() != 6 or run.bench.size() != 4 or manager.current_shop_items.size() != 5:
		return false
	return run.has_method("get_free_rerolls_available") and _has(view, "rank_ui") and view.rank_ui != null and _has(view.rank_ui, "_last_weights") and _has(view, "item_uses_label") and _has(view, "wins_count_label")

func _has(value, key: String) -> bool:
	if value == null or not is_instance_valid(value): return false
	for property in value.get_property_list():
		if str(property.name) == key: return true
	return false

func _script_is(value, path: String) -> bool:
	if value == null or not is_instance_valid(value): return false
	var script = value.get_script()
	while script != null:
		if str(script.resource_path) == path: return true
		script = script.get_base_script()
	return false

func _warn(message: String) -> void:
	if message != _last_error:
		push_warning("BatomonHelper Bridge: " + message)
		_last_error = message
