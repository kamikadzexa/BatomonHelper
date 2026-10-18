extends Node
## Original companion mod for the community Mod Loader 1.2.2.
## Reads game objects on the main thread; writes only user://batomon-helper/.
const OUTPUT_DIR := "user://batomon-helper"
const SHOP_SCRIPT := "res://game/states/shop_state.gd"
const STAT_NAMES := {1: "Damage", 2: "Heal", 3: "Shield", 4: "Burn", 5: "Poison", 6: "Shock", 7: "Multicast"}
const RARITIES := ["Common", "Uncommon", "Rare", "SuperRare", "Legendary"]
var _next_poll := 0
var _sequence := 0
var _last_error := ""

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
		"team": read_slots(run.team, 6, false, int(run.gold)),
		"bench": read_slots(run.bench, 4, false, int(run.gold)),
		"shop": read_slots(items, 5, true, int(run.gold)),
		"capturedAt": Time.get_datetime_string_from_system(true) + "Z"}

func read_region(run) -> String:
	# The run model has used both region and region_id across game builds.
	# Export the active run region, never infer it from the current shop (which can contain foreign offers).
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
	push_warning("BatomonHelper Bridge: Cannot read the active run region.")
	return "All"

func read_slots(source: Array, count: int, shop: bool, gold: int) -> Array:
	var slots := []
	for i in range(count):
		var value = source[i] if i < source.size() else null
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
	var units := []
	# Simulator order: damage/heal/shield/burn/poison/shock/speed/multicast/sell.
	var stat_ids := [1, 2, 3, 4, 5, 6, 10, 7, "bonus_sell_value"]
	var multipliers := {1: 8, 2: 13, 3: 12, 4: 15, 5: 14, 6: 9}
	for i in range(run.team.size()):
		var monster = run.team[i]
		if monster == null: continue
		var stats := []
		for stat in stat_ids:
			var value: float = monster.get_base_permanent_stat(stat)
			if multipliers.has(stat):
				value = round(value * (1.0 + monster.get_base_permanent_stat(multipliers[stat])))
			stats.append(value)
		var cooldown = monster.get_base_cooldown()
		units.append({"slot": i, "permanentStats": stats, "baseCooldown": cooldown if cooldown > 0 else null,
			"addedTypes": monster.get_extra_types()})
	return {"team": units, "trinkets": run.trinket_ids}

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
