def inspect(what: Literal['vault'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One Ingot or Remote Vault; default every vault.
) -> dict:  # main list: r['vaults']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """What each Ingot Vault stores, and every Remote Vault's link.
    Reply:
      vaults: list[{vault: Thing, position: Vec3, on_off: bool, powered: bool, stock: list[{kind: str, prefab_name: str | None, display_name: str |
          None, reagent: str | None, max_stack: float | None, quantity: float}], pending_vends: int}]
      remote_vaults: list[{remote: Thing, vault_id: str | None, connection: str}]
      count: int
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('vault')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
