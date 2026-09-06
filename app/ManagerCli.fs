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

/// Only meaningful beside `--check`, and refused without it: a flag that silently did
/// nothing would be indistinguishable from one that had no effect to have.
let detailedOption =
    Cli.flag "detailed" None "with --check, also say what each setting and state means"

/// `--version` and `--help` answer from this before any configuration is read — no data
/// directory, no ports, no sessions launched — and an unknown option or a missing value stops
/// the boot with the reason and the usage, rather than being ignored into a deny-everything
/// Manager.
let spec =
    Cli.spec
        "yession-manager"
        [ authOption; secretsOption; portOption; dataDirOption; idleTimeoutOption
          defaultSessionOption; spawnBinOption; webhookOption; checkOption; detailedOption ]

/// What `--check` reports: the RESOLVED configuration, as text that has already been through
/// every parser this bin has.
///
/// Strings rather than the domain types they came from, deliberately. This module is compiled
/// before the Manager's own, so it could not name them; and the report is a rendering, which
/// is the one job it should be possible to test without building a Manager. The boot resolves,
/// this prints.
/// Where a value came from.
///
/// This is the question `--check` exists to answer. An operator reads a report to find out
/// whether the setting they wrote took effect, and a value alone cannot say: `8321` is the
/// same text whether it was chosen or defaulted to.
type Origin =
    /// The operator gave this on the command line.
    | Chosen
    /// The operator gave nothing. This is the value the bin uses when nobody chooses one.
    | Default
    /// The feature is not enabled, and the value describes what that means.
    | Off

module Origin =

    let describe =
        function
        | Chosen -> "set"
        | Default -> "default"
        | Off -> "off"

    /// What each state means, in the order a reader meets them.
    let meanings =
        [ "set", "You gave this value on the command line."
          "default", "You gave no value. The Manager uses this one."
          "off", "The feature is not enabled." ]

/// Where this deployment answers. A case rather than a list of rows, so the labels, the
/// descriptions and the origins live here with every other piece of the report's prose —
/// the caller supplies the two addresses and nothing else.
[<RequireQualifiedAccess>]
type Addressing =
    /// Nothing was configured.
    | OnLoopback
    /// Both addresses were configured.
    | Fronted of manager: string * sessions: string

/// One line of the report: a value, where it came from, and what it does.
type Setting =
    { Label : string
      Value : string
      Origin : Origin
      /// What this setting does, in one sentence. Printed by `--detailed`.
      Detail : string }

/// The resolved configuration, as text that has already been through every parser this bin
/// has, paired with where each value came from.
///
/// Strings rather than the domain types they came from, deliberately. This module is compiled
/// before the Manager's own, so it could not name them; and the report is a rendering, which
/// is the one job it should be possible to test without building a Manager. The boot
/// resolves, this prints.
type Report =
    { Version : string
      TrustRule : string * Origin
      Secrets : string * Origin
      Port : string * Origin
      DataDir : string * Origin
      DefaultSession : string * Origin
      IdleTimeout : string * Origin
      Spawn : string * Origin
      Addressing : Addressing
      /// Canonicalised by `WebhookRelay.EndpointSpec.encode`, so what is printed is what
      /// could be typed back.
      Webhooks : string list
      /// Every `YESSION_*` name in this process's environment. A child inherits the whole
      /// environment, so this is the list a session sees. Names only: this bin does not read
      /// most of them, and the examples use the same prefix for their own
      /// (`YESSION_PROXY_PORT`, `YESSION_SERIAL_PORT`), so a bin that refused what it did not
      /// recognise would refuse them.
      Inherited : string list }

module Report =

    [<Literal>]
    let private ValueColumn = 52

    /// A row. The state is padded to a column where it can be, and separated by two spaces
    /// where it cannot: a store path is longer than any column worth keeping, and a value
    /// that ran into its own state read as one word (`…SessionMain.jsdefault`).
    let private line (label: string) (value: string) (origin: Origin) =
        let padded = if value.Length >= ValueColumn then value + "  " else value.PadRight ValueColumn
        sprintf "  %-18s%s%s" label padded (Origin.describe origin)

    /// A list, or a phrase saying it is empty — never a blank. A label with nothing after it
    /// reads as a rendering fault, and a report has to be believed.
    let private listing (empty: string) (values: string list) =
        match values with
        | [] -> empty
        | _ -> String.concat ", " values

    /// Every line of the report, as data: the label, the value, where it came from, and what
    /// it does. One sentence each, subject verb object.
    ///
    /// Public because it is what the report IS. `render` is one way to print this list, and a
    /// test that asserted against the printing would go red on a column moving — which is not
    /// a fault. Descriptions live here rather than at the call site for the same reason: the
    /// composition root is the one place a test cannot reach.
    let settings (report: Report) : Setting list =
        let setting label (value, origin) detail =
            { Label = label; Value = value; Origin = origin; Detail = detail }
        let head =
            [ setting "trust rule" report.TrustRule
                "The Manager identifies the user behind each request with this rule."
              setting "secrets" report.Secrets
                "The Manager stores connected credentials this way. It uses the OS credential manager where this host has one."
              setting "port" report.Port
                "The Manager listens on this port."
              setting "data dir" report.DataDir
                "The Manager writes its state to this directory."
              setting "default session" report.DefaultSession
                "The Manager creates and launches this session at boot."
              setting "idle timeout" report.IdleTimeout
                "The Manager stops a session after this long without use."
              setting "spawn" report.Spawn
                "The Manager runs this command to start a session." ]
        // Two addresses, two facts. They shared a description until a report of a real
        // deployment printed the same sentence twice, which reads as padding.
        let addressing =
            match report.Addressing with
            | Addressing.OnLoopback ->
                [ setting "addressing" ("loopback (only this machine)", Default)
                    "Nothing outside this machine reaches this deployment." ]
            | Addressing.Fronted (manager, sessions) ->
                [ setting "manager at" (manager, Chosen)
                    "A browser reaches the Manager here. Every session sends its users here to sign in."
                  setting "sessions at" (sessions, Chosen)
                    "The Manager builds each session's own address from this template." ]
        let webhooks =
            [ setting
                "webhooks"
                (listing "none declared" report.Webhooks, (if report.Webhooks.IsEmpty then Off else Chosen))
                "The Manager serves these endpoints and gives each delivery to the sessions that asked for it." ]
        head @ addressing @ webhooks

    /// The report. `detailed` adds what every setting and every state means.
    let render (detailed: bool) (report: Report) : string =
        let resolved = settings report
        let rows = resolved |> List.map (fun s -> line s.Label s.Value s.Origin)
        let inherited =
            [ ""
              sprintf "  %-18s%s" "inherited" (listing "none" report.Inherited)
              "  Every session inherits these variables. This bin reads only some of them." ]
        let details =
            if not detailed then
                []
            else
                [ ""; "settings" ]
                @ (resolved |> List.map (fun s -> sprintf "  %-18s%s" s.Label s.Detail))
                @ [ ""; "states" ]
                @ (Origin.meanings |> List.map (fun (state, meaning) -> sprintf "  %-18s%s" state meaning))
        [ sprintf "yession-manager %s" report.Version; "" ] @ rows @ inherited @ details
        |> String.concat "\n"
