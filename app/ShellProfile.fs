module Yession.Host.ShellProfile

// The `shell_profile` query (Plan 25): where a shell opened in each sandbox starts.
//
// Here rather than beside the projection, for the reason `Repos.query` and
// `WorkSandboxes.query` are where they are: `QueryRegistration` is a Host-layer type, and
// the Session Process — which owns the answer — cannot see it. What lives here is the
// declaration and the projection into rows; the state itself stays with the terminal
// manager, which is the only thing that can change it.
//
// One registration, two audiences: the agent gets a `readOnlyHint` MCP tool, and the
// people get a section on the generated settings surface. Nobody writes a panel.

open Yession.Domain
open Yession.SessionProcess

let queryName : QueryName =
    match QueryName.create "shell_profile" with
    | Ok name -> name
    | Error e -> failwithf "shell profile query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "shell profile"
      Description =
        "Where a terminal opened in each work sandbox starts. Only sandboxes that have been \
         given one appear; the rest start wherever the sandbox itself puts them."
      Shape =
        Rows
            [ QueryColumn.create "sandbox" "sandbox"
              QueryColumn.create "cwd" "starts in" ] }

/// Register the profiles as a query. It takes the terminal MANAGER rather than a snapshot:
/// a profile changes mid-session, and a value read at composition time would be the one
/// that existed before anybody could set one.
let query (terminals: unit -> SessionTerminals.SessionTerminals) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        (terminals ()).Profiles ()
                        |> ShellProfileProjection.listed
                        |> List.map (fun (sandbox, profile) ->
                            [ "sandbox", CellText (SandboxRef.render sandbox)
                              "cwd",
                              (match profile.WorkingDirectory with
                               | Some cwd -> CellText cwd
                               | None -> CellAbsent) ])))
            } }
