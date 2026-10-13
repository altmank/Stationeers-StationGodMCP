def help(*,
) -> dict:
    """StationGod's help, from the MCP server: help {} lists verbs and topics; {verb} a verb's whats and options; {verb, what} one what in full; {topic} a shared topic.
    Reply:
      verb: str | None
      what: str | None
      lang: str | None
      topic: str | None
      subtopic: str | None
      text: str
    Example: sg.help()
    """
