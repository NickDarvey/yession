module Yession.Host.Retirements

// Variables a bin USED to read, and what replaced each.
//
// A setting that moves and leaves nothing behind fails in the worst direction: the
// deployment keeps setting it, the bin no longer looks, and the behaviour it bought is
// simply gone. Four of those landed on the deployment this project runs on — a renamed port
// (masked, because the default matched), a renamed idle timeout (reaping silently off for
// weeks), a renamed spawn path (promotions moved a symlink nothing followed), and a
// replaced sandbox-resource pair (the host granted nothing and every repo was refused).
// Each was found by noticing missing BEHAVIOUR, which is the most expensive way to find
// anything.
//
// So a retirement is data, and a boot that finds one refuses. The environment is the only
// place the old name can still be, and this is a pure function over a lookup so the cheap
// tier can reach it — the boot passes `Interop.env`.

/// One retired variable: what it was called, and what to write instead.
type Retirement = { Was : string; Now : string }

let private retired (was: string) (now: string) = { Was = was; Now = now }

/// What `yession-manager` no longer reads, because the operator now says it on the command
/// line. Each of these named a decision about THIS process that nothing downstream inherits,
/// which is what makes an argument the honest home for it: the parser refuses a name it does
/// not know, `--help` lists what there is, and the unit that starts the Manager says what
/// the deployment IS rather than carrying it in an environment block.
let manager : Retirement list =
    [ retired "YESSION_PORT" "--port"
      retired "YESSION_DATA_DIR" "--data-dir"
      retired "YESSION_IDLE_TIMEOUT" "--idle-timeout"
      retired "YESSION_DEFAULT_SESSION" "--default-session"
      retired "YESSION_SPAWN_BIN" "--spawn-bin" ]

/// Every retirement `lookup` still finds a value for. Reported together, not one per boot:
/// a deployment that moved one of these moved all of them at the same time, and finding out
/// about the next one only after fixing this one is a boot cycle spent per variable.
let found (retirements: Retirement list) (lookup: string -> string) : Retirement list =
    retirements |> List.filter (fun r -> (lookup r.Was).Trim () <> "")

/// What to say about them. One line per retirement, so the fix is a transcription.
let complaint (found: Retirement list) : string =
    let lines = found |> List.map (fun r -> sprintf "  %s is now %s" r.Was r.Now) |> String.concat "\n"
    sprintf
        "the environment sets %d variable(s) this bin no longer reads:\n%s\n\nSet the option instead. Refused rather than ignored, because a setting that is read by nobody is the behaviour you asked for silently not happening."
        (List.length found)
        lines
