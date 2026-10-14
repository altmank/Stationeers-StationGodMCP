def find(what: Literal['food'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:  # main list: r['food']; iterating or indexing it walks every page, in order
    """Every food and drink: nutrition, hydration, decay, totals.
    No options.
    Reply:
      food: list[{reference_id: str, prefab_name: str | None, display_name: str | None, quantity: float, max_quantity: float | None, location: str,
          carried_by: str | None, held_in: list[{reference_id: str, prefab_name: str | None, display_name: str | None, slot_index: int, slot_name: str
          | None}], position: Vec3, distance_m: float | None, nutrition_each: float, nutrition: float, food_quality: str, decay_time_left_s: int |
          None, in_package: str | None, package_name: str | None}]
      count: int
      total: int
      offset: int
      has_more: bool
      drinks: list[{reference_id: str, prefab_name: str | None, display_name: str | None, quantity: float, max_quantity: float | None, location: str,
          carried_by: str | None, held_in: list[{reference_id: str, prefab_name: str | None, display_name: str | None, slot_index: int, slot_name: str
          | None}], position: Vec3, distance_m: float | None, liquid_l: float, hydration: float, in_package: str | None, package_name: str | None}]
      packages: list[{reference_id: str, prefab_name: str | None, display_name: str | None, location: str, carried_by: str | None, held_in:
          list[{reference_id: str, prefab_name: str | None, display_name: str | None, slot_index: int, slot_name: str | None}], position: Vec3,
          distance_m: float | None, runtime_type: str, contents: list[{prefab_name: str, display_name: str | None, items: int, quantity: float}],
          nutrition: float, liquid_l: float, hydration: float}]
      not_counted: list[{prefab_name: str, display_name: str | None, reason: str, items: int, quantity: float}]
      totals: {nutrition: float, nutrition_in_packages: float, liquid_l: float, liquid_in_packages_l: float, hydration: float, hydration_in_packages:
          float, food_items: int, drink_items: int}
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('food')
    """
