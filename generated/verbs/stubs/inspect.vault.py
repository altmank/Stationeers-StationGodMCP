def inspect(what: Literal['vault'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # One Ingot or Remote Vault; default every vault.
) -> dict:  # main list: r['vaults']; iterating or indexing it walks every page, in order
    """What each Ingot Vault stores, and every Remote Vault's link.
    Reply:
      vaults: list[{vault: Thing, position: Vec3, on_off: bool, powered: bool, stock: list[{kind: str, prefab_name: str | None, display_name: str |
          None, reagent: str | None, max_stack: float | None, quantity: float}], pending_vends: int}]
      remote_vaults: list[{remote: Thing, vault_id: str | None, connection: str}]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.inspect('vault')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
