module Yession.Host.ManagerCli

// What `yession-manager` accepts, as a VALUE rather than as statements inside the bin.
//
// It lives here rather than in `Main.fs` because two things need it and they must not be
// able to disagree. `Main.fs` reads each option to build the Manager; `Retirements` names
// options as the fix for variables that moved, and a retirement pointing at an option this
// bin does not declare would send an operator to a flag the parser refuses. That is a rule
// over BOTH lists, so it needs a place where both are reachable — and the composition root
// is the one place a test cannot go.
//
// It cost a red build to learn: the check existed, but against a hand-copied list of the
// options, so the first retirement added afterwards broke it. A copy that must be updated in
// lockstep is not a check, it is a second list.

/// Every option, in the order `--help` prints them.
let authOption =
    Cli.value "auth" "rule" "how a request's subject is established: none, localhost, trusted-headers"

let secretsOption =
    Cli.value "secrets" "mode" "whether secrets persist across restarts: durable, ephemeral"

let portOption =
    Cli.value "port" "port" "the port the Manager listens on; 0 lets the OS choose (default 8321)"

let dataDirOption =
    Cli.value "data-dir" "path" "where this Manager keeps its state (default .yession)"

let idleTimeoutOption =
    Cli.value "idle-timeout" "window" "stop a session unused for this long: 90s, 30m, 2h (default never)"

let defaultSessionOption =
    Cli.value "default-session" "id" "the session ensured and launched at boot (default local-session)"

let spawnBinOption =
    Cli.value "spawn-bin" "command" "the command that runs a session (default this Node on the packaged entry)"

/// Repeatable, because endpoints are a SET rather than a choice: one per service, each with
/// its own rotation and signature scheme, and a separator inside a single option would be a
/// grammar this parser had to invent. `WebhookRelay.EndpointSpec` owns the one it does have.
let webhookOption =
    Cli.values "webhook" "name[@rotation][=header:encoding[:prefix]]" "serve a hook endpoint at /hooks/<name>"

/// Resolve everything and say what it came to, then stop — launching nothing, writing
/// nothing, binding no port.
///
/// It exists because the alternative is a boot per question. A setting that resolved to
/// something other than what an operator meant does not announce itself: the Manager starts,
/// serves, and behaves differently in a way that is only visible later, from a consequence.
/// Four of those cost this project weeks apiece — reaping that was off, promotions nothing
/// followed, sandboxes refused for want of resources — and each was found by noticing absent
/// BEHAVIOUR, then working back. This is the question asked directly.
///
/// It refuses nothing of its own. Everything it can refuse, the boot already refuses on the
/// way to here — a bad `--port`, an unknown `--auth`, a half-set address pair, a retired
/// variable — so `--check` reaching its report at all is itself the first answer.
let checkOption =
    Cli.flag "check" None "resolve the configuration, print what it came to, and exit"

/// `--version` and `--help` answer from this before any configuration is read — no data
/// directory, no ports, no sessions launched — and an unknown option or a missing value stops
/// the boot with the reason and the usage, rather than being ignored into a deny-everything
/// Manager.
let spec =
    Cli.spec
        "yession-manager"
        [ authOption; secretsOption; portOption; dataDirOption; idleTimeoutOption
          defaultSessionOption; spawnBinOption; webhookOption; checkOption ]

/// What `--check` reports: the RESOLVED configuration, as text that has already been through
/// every parser this bin has.
///
/// Strings rather than the domain types they came from, deliberately. This module is compiled
/// before the Manager's own, so it could not name them; and the report is a rendering, which
/// is the one job it should be possible to test without building a Manager. The boot resolves,
/// this prints.
type Report =
    { Version : string
      TrustRule : string
      Secrets : string
      Port : int
      DataDir : string
      DefaultSession : string
      IdleTimeout : string
      Spawn : string
      /// Label and value per line: one line for loopback, two when fronted.
      Addressing : (string * string) list
      /// Canonicalised by `WebhookRelay.EndpointSpec.encode`, so what is printed is what
      /// could be typed back.
      Webhooks : string list
      /// Every `YESSION_*` name in this process's environment, unread and unjudged. A child
      /// inherits the whole environment, so this is the list a session sees — and printing it
      /// is how a name nothing reads becomes visible (a typo, a variable from another
      /// version, one of the examples' own) without a bin having to refuse it. The examples
      /// use this prefix on purpose; refusing what a bin does not recognise would refuse
      /// them.
      Inherited : string list }

module Report =

    let private line (label: string) (value: string) = sprintf "  %-18s%s" label value

    /// A list, or a phrase saying it is empty — never a blank. An empty value beside a label
    /// reads as a rendering fault, which is the wrong thing for a report whose whole job is
    /// to be believed.
    let private listing (empty: string) (values: string list) =
        match values with
        | [] -> empty
        | _ -> String.concat ", " values

    let render (report: Report) : string =
        let head =
            [ sprintf "yession-manager %s" report.Version
              ""
              line "trust rule" report.TrustRule
              line "secrets" report.Secrets
              line "port" (string report.Port)
              line "data dir" report.DataDir
              line "default session" report.DefaultSession
              line "idle timeout" report.IdleTimeout
              line "spawn" report.Spawn ]
        let addressing = report.Addressing |> List.map (fun (label, value) -> line label value)
        let tail =
            [ line "webhooks" (listing "none declared" report.Webhooks)
              ""
              line "inherited" (listing "none" report.Inherited)
              "  (every session inherits these; this bin reads only some of them)" ]
        head @ addressing @ tail |> String.concat "\n"
