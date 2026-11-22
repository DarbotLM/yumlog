namespace Yumlog.Native

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// AgentCard 3.1 (DarbotLM/ac3.1 `CopilotStudioAgentCard`) output for semantic decimation.
///
/// Nothing about the card format is hardcoded here. The card `kind`, the schema version
/// and every allowed field come from the vendored schema
/// (config/agentcards/ac3.1/agent-card.schema.json, embedded as Yumlog.AgentCard31.Schema.json).
/// Ids, names, requirements and topic/knowledge kinds come from the decimation profile
/// (config/agentcards/decimation-profile.json, embedded as Yumlog.AgentCard31.Profile.json).
/// Every card is validated against the schema before it is written.
module AgentCard31 =
    let SchemaResource = "Yumlog.AgentCard31.Schema.json"
    let ProfileResource = "Yumlog.AgentCard31.Profile.json"

    type Schema =
        { Id: string
          Kind: string
          Version: string
          Document: JsonObject }

    type Profile =
        { FileName: string
          IdPrefix: string
          IdHashLength: int
          Language: string
          NamePrefix: string
          Requires: (string * string) array
          KnowledgeKind: string
          TopicKind: string
          TopicTriggerKind: string
          TopicActionKind: string
          MaxPhrasesPerTopic: int
          Instructions: string }

    /// One file the card's knowledge points at.
    type KnowledgeFile = { Name: string; Path: string; Sha256: string; Description: string }

    /// One topic per indexed modality, with its lexical tags as trigger phrases.
    type TopicSeed = { Modality: string; CardCount: int; Phrases: string array }

    let private readResource (name: string) =
        let assembly = typeof<Schema>.Assembly
        match assembly.GetManifestResourceStream(name) with
        | null -> invalidOp $"Embedded resource {name} is missing from {assembly.GetName().Name}."
        | stream ->
            use stream = stream
            use reader = new StreamReader(stream, Encoding.UTF8)
            reader.ReadToEnd()

    let private asObject (context: string) (node: JsonNode | null) : JsonObject =
        match node with
        | :? JsonObject as value -> value
        | _ -> invalidOp $"{context} must be a JSON object."

    let private child (context: string) (key: string) (parent: JsonObject) : JsonNode =
        match parent[key] with
        | null -> invalidOp $"{context} is missing '{key}'."
        | value -> value

    /// The string a JSON node holds, or None for any other node.
    let private stringOf (node: JsonNode | null) : string option =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
            Some(value.GetValue<string>())
        | _ -> None

    let private text (context: string) (key: string) (parent: JsonObject) : string =
        match stringOf (child context key parent) with
        | Some s when not (String.IsNullOrWhiteSpace(s)) -> s
        | _ -> invalidOp $"{context}.{key} must be a non-empty string."

    let private integer (context: string) (key: string) (parent: JsonObject) =
        match child context key parent with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number && value.GetValue<decimal>() >= 1m
                                     && Decimal.Truncate(value.GetValue<decimal>()) = value.GetValue<decimal>() ->
            int (value.GetValue<decimal>())
        | _ -> invalidOp $"{context}.{key} must be a positive integer."

    /// Reads kind, version and $id from the schema itself. The version is the
    /// `/agent-card/<version>/` segment of `$id`, and it must agree with the
    /// schemaVersion example, so a mismatched upstream file fails loudly.
    let parseSchema (json: string) : Schema =
        let document = asObject "AgentCard schema" (JsonNode.Parse(json))
        let id = text "AgentCard schema" "$id" document
        let properties = asObject "AgentCard schema properties" (child "AgentCard schema" "properties" document)
        let kind = text "AgentCard schema properties.kind" "const" (asObject "kind" (child "properties" "kind" properties))
        let versionNode = asObject "schemaVersion" (child "properties" "schemaVersion" properties)
        let example =
            match child "schemaVersion" "examples" versionNode with
            | :? JsonArray as values when values.Count > 0 -> defaultArg (stringOf values[0]) ""
            | _ -> ""
        let fromId = Regex.Match(id, @"/agent-card/(?<v>\d+(?:\.\d+)+)/")
        if not fromId.Success then invalidOp $"AgentCard schema $id '{id}' does not carry a version segment."
        let version = fromId.Groups["v"].Value
        if example <> version then
            invalidOp $"AgentCard schema version mismatch: $id says {version}, schemaVersion example says '{example}'."
        { Id = id; Kind = kind; Version = version; Document = document }

    let parseProfile (json: string) : Profile =
        let p = asObject "Decimation profile" (JsonNode.Parse(json))
        let ctx = "Decimation profile"
        let requires =
            asObject $"{ctx}.requires" (child ctx "requires" p)
            |> Seq.map (fun pair ->
                match stringOf pair.Value with
                | Some s -> pair.Key, s
                | None -> invalidOp $"{ctx}.requires.{pair.Key} must be a string.")
            |> Seq.toArray
        { FileName = text ctx "fileName" p
          IdPrefix = text ctx "idPrefix" p
          IdHashLength = min 64 (integer ctx "idHashLength" p)
          Language = text ctx "language" p
          NamePrefix = text ctx "namePrefix" p
          Requires = requires
          KnowledgeKind = text ctx "knowledgeKind" p
          TopicKind = text ctx "topicKind" p
          TopicTriggerKind = text ctx "topicTriggerKind" p
          TopicActionKind = text ctx "topicActionKind" p
          MaxPhrasesPerTopic = integer ctx "maxPhrasesPerTopic" p
          Instructions = text ctx "instructions" p }

    let private embeddedSchema = lazy (parseSchema (readResource SchemaResource))
    let private embeddedProfile = lazy (parseProfile (readResource ProfileResource))
    let schema () = embeddedSchema.Value
    let profile () = embeddedProfile.Value

    // ---- Validation: a draft-07 subset driven entirely by the schema document ----

    let private typeName (node: JsonNode | null) =
        match node with
        | null -> "null"
        | :? JsonObject -> "object"
        | :? JsonArray -> "array"
        | :? JsonValue as value ->
            match value.GetValueKind() with
            | JsonValueKind.String -> "string"
            | JsonValueKind.Number -> "number"
            | JsonValueKind.True | JsonValueKind.False -> "boolean"
            | _ -> "null"
        | _ -> "unknown"

    let private resolve (root: JsonObject) (reference: string) =
        if not (reference.StartsWith("#/", StringComparison.Ordinal)) then
            invalidOp $"Only local $ref values are supported: {reference}"
        reference.Substring(2).Split('/')
        |> Array.fold (fun (node: JsonObject) segment -> asObject reference (child reference segment node)) root

    let rec private check (root: JsonObject) (schemaNode: JsonObject) (path: string) (value: JsonNode | null) (errors: List<string>) =
        let schemaNode =
            match schemaNode["$ref"] with
            | null -> schemaNode
            | reference ->
                match stringOf reference with
                | Some target -> resolve root target
                | None -> invalidOp $"{path}: $ref must be a string."
        let actual = typeName value
        match schemaNode["type"] with
        | null -> ()
        | expected ->
            let wanted = defaultArg (stringOf expected) ""
            let ok = wanted = actual || (wanted = "integer" && actual = "number")
            if not ok then errors.Add($"{path}: expected {wanted}, found {actual}")
        match schemaNode["const"], value with
        | null, _ -> ()
        | expected, actualValue ->
            if not (JsonNode.DeepEquals(expected, actualValue)) then
                errors.Add($"{path}: must equal {expected.ToJsonString()}")
        match schemaNode["enum"], value with
        | :? JsonArray as allowed, actualValue ->
            if not (allowed |> Seq.exists (fun option -> JsonNode.DeepEquals(option, actualValue))) then
                errors.Add($"{path}: is not one of {allowed.ToJsonString()}")
        | _ -> ()
        match value with
        | :? JsonObject as instance ->
            match schemaNode["required"] with
            | :? JsonArray as required ->
                for name in required do
                    match stringOf name with
                    | Some key when not (instance.ContainsKey(key)) -> errors.Add($"{path}: missing required '{key}'")
                    | _ -> ()
            | _ -> ()
            let properties =
                match schemaNode["properties"] with
                | :? JsonObject as props -> props
                | _ -> JsonObject()
            for pair in instance do
                let childPath = $"{path}/{pair.Key}"
                match properties[pair.Key] with
                | :? JsonObject as propertySchema -> check root propertySchema childPath pair.Value errors
                | _ ->
                    match schemaNode["additionalProperties"] with
                    | :? JsonObject as extra -> check root extra childPath pair.Value errors
                    | :? JsonValue as flag when flag.GetValueKind() = JsonValueKind.False ->
                        errors.Add($"{path}: '{pair.Key}' is not allowed by the schema")
                    | _ -> ()
        | :? JsonArray as items ->
            match schemaNode["items"] with
            | :? JsonObject as itemSchema ->
                items |> Seq.iteri (fun index item -> check root itemSchema $"{path}/{index}" item errors)
            | _ -> ()
        | _ -> ()

    /// Validates a card against a schema document. Returns the list of violations (empty = valid).
    let validateWith (schemaInfo: Schema) (card: JsonNode) : string array =
        let errors = List<string>()
        check schemaInfo.Document schemaInfo.Document "" card errors
        errors.ToArray()

    let validate (card: JsonNode) = validateWith (schema ()) card

    // ---- Emission ----

    let private fileUri (path: string) = Uri(Path.GetFullPath(path)).AbsoluteUri

    let private slug (value: string) =
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-')

    /// Builds the AgentCard for one decimation run. `facts` become string metadata
    /// (the schema requires string values). The card id is deterministic: profile
    /// prefix plus a prefix of the source SHA-256.
    let build (schemaInfo: Schema) (profileInfo: Profile) (sourcePath: string) (sourceSha256: string)
              (knowledge: KnowledgeFile array) (topics: TopicSeed array) (facts: (string * string) array) : JsonObject =
        if not (Regex.IsMatch(sourceSha256, "^[0-9a-fA-F]{64}$")) then invalidArg "sourceSha256" "A SHA-256 hex digest is required."
        let fileName = Path.GetFileName(sourcePath)
        let card = JsonObject()
        card["kind"] <- JsonValue.Create(schemaInfo.Kind)
        card["schemaVersion"] <- JsonValue.Create(schemaInfo.Version)
        card["id"] <- JsonValue.Create($"{profileInfo.IdPrefix}.{sourceSha256.Substring(0, profileInfo.IdHashLength).ToLowerInvariant()}")
        card["name"] <- JsonValue.Create($"{profileInfo.NamePrefix}: {fileName}")
        card["description"] <- JsonValue.Create($"Evidence-linked semantic Library for {fileName}, produced by Yumlog Studio semantic decimation.")
        card["language"] <- JsonValue.Create(profileInfo.Language)
        card["instructions"] <- JsonValue.Create(profileInfo.Instructions)
        let topicArray = JsonArray()
        for seed in topics do
            let topic = JsonObject()
            topic["kind"] <- JsonValue.Create(profileInfo.TopicKind)
            topic["id"] <- JsonValue.Create($"decimation-{slug seed.Modality}")
            topic["displayName"] <- JsonValue.Create($"{seed.Modality} cards ({seed.CardCount})")
            topic["isEnabled"] <- JsonValue.Create(seed.CardCount > 0)
            let trigger = JsonObject()
            trigger["kind"] <- JsonValue.Create(profileInfo.TopicTriggerKind)
            trigger["intent"] <- JsonValue.Create($"decimation.{slug seed.Modality}")
            let phrases = JsonArray()
            for phrase in seed.Phrases |> Array.distinct |> Array.truncate profileInfo.MaxPhrasesPerTopic do
                phrases.Add(JsonValue.Create(phrase))
            trigger["phrases"] <- phrases
            topic["triggers"] <- JsonArray(trigger :> JsonNode)
            let action = JsonObject()
            action["kind"] <- JsonValue.Create(profileInfo.TopicActionKind)
            action["id"] <- JsonValue.Create($"decimation-{slug seed.Modality}-answer")
            action["text"] <- JsonValue.Create($"Search the {seed.Modality} cards in the Library and cite card ids and time ranges.")
            topic["actions"] <- JsonArray(action :> JsonNode)
            topicArray.Add(topic)
        card["topics"] <- topicArray
        card["tools"] <- JsonArray()
        let knowledgeArray = JsonArray()
        for file in knowledge do
            let source = JsonObject()
            source["kind"] <- JsonValue.Create(profileInfo.KnowledgeKind)
            source["name"] <- JsonValue.Create(file.Name)
            source["url"] <- JsonValue.Create(fileUri file.Path)
            source["description"] <- JsonValue.Create($"{file.Description} sha256={file.Sha256}")
            source["isEnabled"] <- JsonValue.Create(true)
            knowledgeArray.Add(source)
        card["knowledge"] <- knowledgeArray
        card["triggers"] <- JsonArray()
        let requires = JsonObject()
        for name, version in profileInfo.Requires do requires[name] <- JsonValue.Create(version)
        card["requires"] <- requires
        let metadata = JsonObject()
        metadata["schemaId"] <- JsonValue.Create(schemaInfo.Id)
        metadata["sourcePath"] <- JsonValue.Create(Path.GetFullPath(sourcePath))
        metadata["sourceSha256"] <- JsonValue.Create(sourceSha256.ToLowerInvariant())
        for key, value in facts do metadata[key] <- JsonValue.Create(value)
        card["metadata"] <- metadata
        card

    /// Validates, then writes the card. Throws with every violation when the card is invalid.
    let write (schemaInfo: Schema) (path: string) (card: JsonObject) =
        match validateWith schemaInfo card with
        | [||] ->
            let options = JsonSerializerOptions(WriteIndented = true)
            File.WriteAllText(path, card.ToJsonString(options))
        | errors -> invalidOp ("AgentCard failed schema validation: " + String.Join("; ", errors))

    /// The SHA-256 hex digest the card cites for a knowledge file.
    let sha256File (path: string) =
        use stream = File.OpenRead(path)
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
