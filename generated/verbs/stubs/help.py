def help(*,
) -> dict:
    """StationGod's help, from the MCP server: help {} lists verbs and topics; {verb} a verb's whats and options; {verb, what} one what in full; {topic} a shared topic.
    Reply:
      tool: str | None
      topic: str | None
      subtopic: str | None
      text: str
      reply_keys: str | None
      topics: list[str] | None
      shared_topics: list[str] | None
      subtopics: list[str] | None
    Example: sg.help()
    """
