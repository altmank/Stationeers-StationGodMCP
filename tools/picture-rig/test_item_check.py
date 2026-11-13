"""item_check's pure logic, offline: row reading, template-versus-live compare, the general-flag rule, the verdicts.

    py -3.12 -m pytest tools/picture-rig/test_item_check.py
"""
import item_check as c

TEMPLATE_REPLY = {"slots": [
    {"index": 0, "display_name": "Slot", "slot_class": {"id": 0, "name": "None"}, "general": True},
    {"index": 1, "display_name": "Battery", "slot_class": {"id": 5, "name": "Battery"}, "general": False}],
    "general_slots": 1}
LIVE_REPLY = {"results": [{"slots": [
    {"index": 0, "display_name": "Slot", "slot_class": "None"},
    {"index": 1, "display_name": "Battery", "slot_class": "Battery"}]}]}


def rows():
    return c.template_rows(TEMPLATE_REPLY), c.live_rows(LIVE_REPLY)


def test_matching_rows_pass():
    template, live = rows()
    assert c.slot_failures("suit", template, live) == []


def test_template_class_is_read_from_its_name():
    assert c.template_rows(TEMPLATE_REPLY)[1]["cls"] == "Battery"


def test_prefab_without_slots_reads_no_rows():
    assert c.template_rows({"prefab_name": "ItemIronIngot"}) == []


def test_differing_name_fails():
    template, live = rows()
    live[0]["name"] = "Pocket"
    assert any("name" in f for f in c.compare_rows(template, live))


def test_differing_count_fails():
    template, live = rows()
    assert c.compare_rows(template, live[:1])


def test_special_slot_reading_general_fails():
    template, live = rows()
    template[1]["general"] = True
    assert any("special" in f for f in c.general_failures(template, live))


def test_open_slot_reading_not_general_fails():
    template, live = rows()
    template[0]["general"] = False
    assert any("takes any item" in f for f in c.general_failures(template, live))


def test_missing_live_rows_fail():
    template, _ = rows()
    assert c.slot_failures("suit", template, [])


def test_gone_is_thing_not_found():
    assert c.gone({"error": {"code": "thing_not_found"}})
    assert not c.gone({"results": []})


def test_reference_ids_at_any_depth():
    assert c.reference_ids({"a": [{"reference_id": 7, "slots": [{"reference_id": "8"}]}]}) == {"7", "8"}


NOT_FOUND = {"error": {"code": "thing_not_found"}}
REFUSED = {"error": {"code": c.STRUCTURE_REFUSAL, "message": "a structure: remove structure takes it down"}}
NO_REFUSAL = {"error": {"code": c.STRUCTURE_REFUSAL, "message": "carried"}}
OK = {"reference_id": "1"}


def passing_remove():
    return {
        "loose": {"report": OK, "confirm": OK, "after": NOT_FOUND},
        "container": {"item_id": "2", "confirm": OK, "after": NOT_FOUND, "holder_after": {"results": []}},
        "carried": {"item_id": "3", "refused": NO_REFUSAL, "allowed": OK, "after": NOT_FOUND, "player_after": {}},
        "contents": {"refused": NO_REFUSAL, "allowed": OK, "after": {"1": NOT_FOUND, "2": NOT_FOUND, "3": NOT_FOUND}},
        "structure": {"reply": REFUSED},
        "script": {"reply": {"ran": True}, "after": NOT_FOUND},
    }


def test_all_remove_cases_pass():
    assert c.remove_failures(passing_remove()) == []


def test_a_case_not_run_fails():
    evidence = passing_remove()
    del evidence["script"]
    assert c.remove_failures(evidence) == ["remove script: not run"]


def test_item_still_there_fails():
    evidence = passing_remove()
    evidence["loose"]["after"] = {"results": []}
    assert any("still there" in f for f in c.remove_failures(evidence))


def test_slot_still_holding_the_item_fails():
    evidence = passing_remove()
    evidence["container"]["holder_after"] = {"results": [{"slots": [{"reference_id": "2"}]}]}
    assert any("slot still holds" in f for f in c.remove_failures(evidence))


def test_carried_deleted_without_allow_fails():
    evidence = passing_remove()
    evidence["carried"]["refused"] = OK
    assert any("without allow" in f for f in c.remove_failures(evidence))


def test_structure_refusal_must_name_remove_structure():
    evidence = passing_remove()
    evidence["structure"]["reply"] = NO_REFUSAL
    assert any("remove structure" in f for f in c.remove_failures(evidence))


def test_script_untargeted_write_fails():
    evidence = passing_remove()
    evidence["script"]["reply"] = {"stop": {"rule": "untargeted_write"}}
    assert any("untargeted_write" in f for f in c.remove_failures(evidence))


def whole():
    template, live = rows()
    entry = {"prefab": "x", "template": template, "live": live, "general_slots": 1}
    return {"slots": {"suit": entry, "backpack": entry, "tool_belt": entry}, "no_slots": {"prefab_name": "ItemIronIngot"},
            "remove": passing_remove()}


def test_whole_run_passes():
    assert c.verdict(whole())[0] == []


def test_missing_slots_group_fails():
    evidence = whole()
    del evidence["slots"]["backpack"]
    assert c.verdict(evidence)[0] == ["backpack: no slots evidence"]


def test_ingot_with_slots_fails():
    evidence = whole()
    evidence["no_slots"] = TEMPLATE_REPLY
    assert c.verdict(evidence)[0]


def test_placed_ids_are_the_reply_strings():
    assert c.placed_ids({"placed": ["12", "13"], "items": [{"reference_id": "12"}]}) == ["12", "13"]
    assert c.placed_ids({"error": {"code": "no_room"}}) == []


def test_a_one_id_answer_in_results_form_reads_its_error():
    reply = {"results": [{"index": 0, "ok": False, "error": {"code": "thing_not_found", "message": "No thing"}}]}
    assert c.gone(reply) and c.code_of(reply) == "thing_not_found"
    assert not c.gone({"results": [{"index": 0, "ok": True, "reference_id": "5"}]})


def test_pick_backpack_prefers_a_plain_pack_with_slots():
    listing = {"prefabs": [{"prefab_name": "ItemHardBackpack", "slots": [{}]}, {"prefab_name": "ItemMiningBackPack", "slots": [{}]},
                           {"prefab_name": "ItemPackNoSlots"}]}
    assert c.pick_backpack(listing) == "ItemMiningBackPack"
    assert c.pick_backpack({"prefabs": [{"prefab_name": "ItemHardBackpack", "slots": [{}]}]}) == "ItemHardBackpack"
    assert c.pick_backpack({"error": {"code": "invalid_prefab"}}) is None
