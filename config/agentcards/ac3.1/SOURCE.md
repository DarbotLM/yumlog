# Vendored AgentCard 3.1 schema

`agent-card.schema.json` is a byte-for-byte copy of
`agentcards/copilot-studio/agent-card.schema.json` from
[DarbotLM/ac3.1](https://github.com/DarbotLM/ac3.1) at commit
`b000993fb1e5a152eac6f99e31281052aa071588`.

Yumlog embeds it (`Yumlog.AgentCard31.Schema.json`) and reads the card `kind`,
the schema version and the allowed fields from it at run time, so Studio's
semantic decimation emits and validates agent cards against the schema itself.
To move to a newer AgentCard schema, replace the file with the upstream bytes,
update the commit above, and run the toolbox gate.
