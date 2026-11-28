# Card schema provenance

`adaptive-card-1.5.schema.json` is the unmodified Microsoft Adaptive Cards
1.5.0 schema from `schemas/1.5.0/adaptive-card.json` at commit
`628020ec85172bdba792ecef8b02fd950d75ee63` (tag `adaptivecards@2.11.0`).
Its SHA-256 is
`32c91fb285180a266a55b3475dd0396e63fe079311714d3a14963b41f5e17920`.
The upstream MIT license is included in `AdaptiveCards-LICENSE.txt`.

AgentCard 3.1 reuses the repository's existing vendored schema and provenance
in `config/agentcards/ac3.1`; this website does not change the native contract.

`flipcard.schema.json` defines the website's documentation envelope. It
contains a typed human-readable front, an Adaptive Card 1.5 payload, an
AgentCard 3.1 documentation descriptor, bounded Git source history and
framework references. It is not an installed agent or an execution grant.
Schema files and per-layer JSON are published by the website build.
