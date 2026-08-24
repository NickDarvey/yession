namespace Yession.Domain.Tools

open Yession.Domain

// The query vocabulary (Plan 15): the READ half of the session's imperative API,
// declared once so that one declaration reaches two audiences — the agent (as an MCP
// tool carrying `readOnlyHint`) and the human (as a generated read-only settings
// surface). Nobody writes a panel for a new query; registering it is what puts it on
// the screen.
//
// Three properties make that possible, and all three are enforced here rather than by
// convention:
//
// - **Nullary.** A query takes no arguments. A parameterised read needs a form, a form
//   is an input, and an input on a read-only surface is a command in disguise
//   (`repo_status octo/hello` is a fine agent tool and deliberately NOT a query).
// - **Read-only.** Declared, not inferred — the MCP annotation is the spec's own marker,
//   so identifying queries in a THIRD-PARTY server needs no yession-specific convention.
// - **Shape-declared.** A query says whether it answers with rows, fields, or a single
//   value, so one renderer can draw all of them and the accessibility floor is
//   implemented once instead of once per panel.

open System

/// A query's stable identifier: the MCP tool name, the URL segment, and the key on the
/// live stream, all at once. Constrained to what all three can carry without escaping —
/// lowercase, digits, underscore — which is also the MCP tool-naming convention.
type QueryName = private QueryName of string

module QueryName =

    let create (raw: string) : Result<QueryName, string> =
        let name = (defaultArg (Option.ofObj raw) "").Trim ()
        let charOk c = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'
        if name = "" then Error "query name cannot be empty"
        elif name.Length > 64 then Error (sprintf "'%s' is too long for a query name" name)
        elif name.StartsWith "_" || name.EndsWith "_" then
            Error (sprintf "'%s' is not a query name (no leading or trailing underscore)" name)
        elif name |> Seq.forall charOk then Ok (QueryName name)
        else Error (sprintf "'%s' is not a query name (lowercase, digits and underscore only)" name)

    let value (QueryName name) = name

/// One column of a row or field of a record: the wire key, and the words a human reads.
/// Both are carried because they genuinely differ — `started_at` addresses the datum and
/// "started" labels it — and a renderer that had only one of them would either show
/// snake_case to people or make the stream's keys presentational.
type QueryColumn =
    { Key : string
      Label : string }

module QueryColumn =

    let create (key: string) (label: string) : QueryColumn = { Key = key; Label = label }

/// What shape a query answers with — the whole vocabulary, deliberately three cases wide.
/// Anything a session wants to show is a table, a labelled record, or one value; a fourth
/// case would be a renderer nobody has written.
type QueryShape =
    /// A single value: the session's backend, a count, a state word.
    | Value
    /// A record — one subject, several named facts about it.
    | Fields of QueryColumn list
    /// Zero or more rows over the same columns: repos, sandboxes, leases.
    | Rows of QueryColumn list

/// One datum inside a query's answer. Text and flags are separated because a flag RENDERS
/// differently (a human reads "yes"/"no"; the agent reads `true`) and because a client
/// that had only strings would have to guess which "false" was a boolean.
type QueryCell =
    | CellText of string
    | CellFlag of bool
    /// The column exists for this query but this row has nothing for it — distinct from
    /// the empty string, which is a value someone chose.
    | CellAbsent

module QueryCell =

    /// The human rendering, also what the agent's text form prints.
    let describe (cell: QueryCell) : string =
        match cell with
        | CellText text -> text
        | CellFlag true -> "yes"
        | CellFlag false -> "no"
        | CellAbsent -> "—"

/// A query's answer, in its declared shape. Constructed by the capability that owns the
/// state, rendered by whoever is asking.
type QueryValue =
    | ValueOf of QueryCell
    | FieldsOf of (string * QueryCell) list
    | RowsOf of (string * QueryCell) list list

/// A query as it is declared: what it is called, what it is for, and what shape it
/// answers with. The description is not decoration — it is what the agent reads to decide
/// whether to call the tool, and (until the in-process MCP transport carries an
/// `outputSchema`) it is where the shape is spelled for the model.
type QueryDef =
    { Name : QueryName
      /// The words above the section in settings.
      Title : string
      /// One sentence: what this answers, for the agent's tool description.
      Description : string
      Shape : QueryShape }

/// What crosses the wire to a browser. ONE multiplexed stream carries every query — the
/// declarations once, then a value per query, then a value again whenever one changes —
/// rather than a stream per query (a connection per registered query, growing with the
/// registry) or a fetch beside a stream (two mechanisms for one requirement, where only
/// one would ever be exercised).
///
/// A subscriber therefore needs nothing else: it connects, learns what exists, and is
/// told what each one says. There is no snapshot request to race against an update.
type QueryFrame =
    /// What this session has, declared. Sent first on every connection.
    | QueriesDeclared of QueryDef list
    /// What one query says now — the opening snapshot for each, and every later change.
    | QueryValued of QueryName * QueryValue

module QueryShape =

    let columns (shape: QueryShape) : QueryColumn list =
        match shape with
        | Value -> []
        | Fields columns
        | Rows columns -> columns

module QueryValue =

    let private cellsOf (row: (string * QueryCell) list) (columns: QueryColumn list) =
        columns
        |> List.map (fun column ->
            column,
            row |> List.tryFind (fun (key, _) -> key = column.Key) |> Option.map snd |> Option.defaultValue CellAbsent)

    /// The value rendered for the agent: the same facts the panel shows, as text a model
    /// reads without a parser. Rows come out one line each, `label: value` joined — a
    /// format that survives being pasted into a sentence, which is what the model does
    /// with it.
    let describe (shape: QueryShape) (value: QueryValue) : string =
        let renderRow row =
            cellsOf row (QueryShape.columns shape)
            |> List.map (fun (column, cell) -> sprintf "%s: %s" column.Label (QueryCell.describe cell))
            |> String.concat ", "
        match value with
        | ValueOf cell -> QueryCell.describe cell
        | FieldsOf fields -> renderRow fields
        | RowsOf [] -> "(none)"
        | RowsOf rows -> rows |> List.map renderRow |> String.concat "\n"

    /// Does this answer fit the shape it was declared with? A capability that returns rows
    /// for a `Value` query is a bug in the capability, and the registry says so rather
    /// than shipping a value no renderer can draw.
    let fits (shape: QueryShape) (value: QueryValue) : bool =
        match shape, value with
        | Value, ValueOf _
        | Fields _, FieldsOf _
        | Rows _, RowsOf _ -> true
        | _ -> false

module QueryDef =

    /// The MCP tool description: the declared sentence, plus what the answer looks like.
    /// The shape belongs in the description because the in-process SDK tool has no
    /// `outputSchema` slot — where a transport does carry one, it is emitted there too
    /// and this text is merely redundant, never wrong.
    let toolDescription (def: QueryDef) : string =
        let shape =
            match def.Shape with
            | Value -> "Answers with a single value."
            | Fields columns ->
                sprintf "Answers with one record: %s." (columns |> List.map (fun c -> c.Label) |> String.concat ", ")
            | Rows columns ->
                sprintf "Answers with zero or more rows of: %s." (columns |> List.map (fun c -> c.Label) |> String.concat ", ")
        sprintf "%s %s Read-only." def.Description shape
