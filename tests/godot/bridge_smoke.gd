extends SceneTree

class FakeUnit extends RefCounted:
	var team_id := 0
	var slot_index := 1
	var is_dead := false
	var cast_count := 3
	var damage_dealt := 12.0
	var healing_done := 4.0
	var shield_granted := 2.0
	func get_stat(_stat):
		return 5.0

class FakeData extends RefCounted:
	var time_elapsed := 1.5
	var battle_units: Array = []
	var player_health := [95.0, 80.0]
	var max_health := [100.0, 100.0]
	var team_shield := [2.0, 3.0]
	var team_burn := [1, 2]
	var team_poison := [3, 4]
	var team_shock := [5, 6]

class FakeSimulation extends RefCounted:
	var data = FakeData.new()

class FakeState extends RefCounted:
	var simulation = FakeSimulation.new()

func _init():
	var script = load("res://bridge.gd")
	if script == null or not script.can_instantiate():
		push_error("Bridge script failed to compile")
		quit(1)
		return
	var bridge = script.new()
	var state = FakeState.new()
	var player = FakeUnit.new()
	var opponent = FakeUnit.new()
	opponent.team_id = 1
	opponent.slot_index = 0
	state.simulation.data.battle_units = [player, null, opponent]
	var frame = bridge.read_battle_frame(state)
	var empty_state = Node.new()
	var valid = frame != null and frame.elapsedSeconds == 1.5 \
		and frame.player.health == 95.0 and frame.opponent.shock == 6 \
		and frame.player.units.size() == 1 and frame.opponent.units.size() == 1 \
		and frame.player.units[0].slot == 1 and frame.opponent.units[0].slot == 3 \
		and frame.player.units[0].casts == 3 and frame.player.units[0].stats.size() == 9 \
		and frame.player.units[0].damageDealt == 12.0 \
		and bridge.read_battle_frame(empty_state) == null
	empty_state.free()
	bridge.free()
	if not valid:
		push_error("Battle frame smoke test failed")
		quit(1)
		return
	print("BRIDGE_SMOKE_OK")
	quit(0)
