def inspect(what: Literal['player'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:
    """The player's hunger and thirst (nutrition, hydration, drain, time left) and worn gear (suit, helmet, jetpack, suit speed).
    No options.
    Reply:
      reference_id: str
      display_name: str | None
      nutrition: float
      nutrition_capacity: float
      hydration: float
      hydration_capacity: float
      food_quality: float
      food_quality_multiplier: float
      mood: float
      sleeping: bool
      brain_online: bool
      robot: bool
      life_suspended: bool
      dead: bool
      suit_worn: bool
      helmet_closed: bool | None
      thirst_temperature: {temperature_k: float | None, source: str}
      difficulty: {id: str | None, hunger_rate: float, hydration_rate: float, offline_metabolism: float}
      hunger: {per_second: float, per_hour: float, awake_per_second: float, awake_per_hour: float, time_left_s: float | None, time_left_awake_s: float
          | None, factors: Any}
      thirst: {per_second: float, per_hour: float, awake_per_second: float, awake_per_hour: float, time_left_s: float | None, time_left_awake_s: float
          | None, factors: Any} | None
      warnings: {nutrition_warning: float, nutrition_critical: float, hydration_warning: float, hydration_critical: float}
      gear: {suit: Thing | None, helmet: Thing | None, jetpack: {reference_id: str, prefab_name: str | None, display_name: str | None, stabilizer:
          bool, active: bool} | None, suit_movement: {speed_multiplier: float, storm_speed_multiplier: float | None, slot_count: int} | None}
      game_tick_s: float
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.inspect('player')
    """
