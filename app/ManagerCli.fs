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

/// `--version` and `--help` answer from this before any configuration is read — no data
/// directory, no ports, no sessions launched — and an unknown option or a missing value stops
/// the boot with the reason and the usage, rather than being ignored into a deny-everything
/// Manager.
let spec =
    Cli.spec
        "yession-manager"
        [ authOption; secretsOption; portOption; dataDirOption; idleTimeoutOption
          defaultSessionOption; spawnBinOption; webhookOption ]
