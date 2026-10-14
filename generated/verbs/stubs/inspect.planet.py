def inspect(what: Literal['planet'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
) -> dict:  # main list: r['gases']; iterating or indexing it walks every page, in order
    """The planet's atmosphere: gases, pressure, temperature, day range, sea, ice caps.
    No options.
    Reply, one of:
    (PlanetNotLoaded)
      terraforming_mod: {version: str | None, state: str | None, temperature_adjustment_k: float | None} | None
    (Planet)
      loaded: bool
      world_time_s: float
      day_length_s: float
      volume_l: float
      gas_volume_l: float
      cells: float
      size_ratio: float | None
      gas_mol: float
      liquid_mol: float
      liquid_l: float
      sea: {liquid_l: float, threshold_l: float, shown: bool}
      pressure_kpa: float
      game_pressure_kpa: float
      temperature_k: float
      temperature_parts: {sun_angle_k: float, sun_distance_k: float, greenhouse_k: float, density_k: float, weather_k: float, latent_k: float,
          external_k: float}
      sun_angle_deg: float
      solar_energy_percent: float
      range: {today_min_k: float, today_min_sun_angle_deg: float, today_max_k: float, today_max_sun_angle_deg: float, orbit_min_k: float, orbit_max_k:
          float}
      storm: {id: str | None} | None
      oxygen_kpa: float
      toxins_kpa: float
      fuel_mol: float
      oxidiser_mol: float
      carbon_dioxide_ratio: float
      gases: list[{gas: str, display_name: str | None, state: str, amount_mol: float, per_cell_mol: float, partial_kpa: float | None, freezes_below_k:
          float | None, min_liquid_pressure_kpa: float | None, condenses_below_today_coldest_k: float | None, condenses_below_orbit_coldest_k: float |
          None}]
      reservoirs: {liquid_clouds: {volume_l: float, liquid_l: float, amount_mol: float, contents: list[{gas: str, amount_mol: float}]} | None,
          ice_clouds: {volume_l: float, liquid_l: float, amount_mol: float, contents: list[{gas: str, amount_mol: float}]} | None, ice_caps:
          {volume_l: float, liquid_l: float, amount_mol: float, contents: list[{gas: str, amount_mol: float}]} | None}
      starting_air: {per_cell_mol: float, mol: float, gases: list[{gas: str, amount_mol: float}]}
      terraforming_mod: {version: str | None, state: str | None, temperature_adjustment_k: float | None} | None
    Example: sg.inspect('planet')
    """
