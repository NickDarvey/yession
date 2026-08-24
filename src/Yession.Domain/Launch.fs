namespace Yession.Domain

/// Session launch vocabulary (Step 10). The Session Manager owns process launch: a
/// Session Process is started by the Manager, never directly, establishing the authority
/// boundary later steps delegate scoped capabilities across (docs/design.md §3).

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

[<RequireQualifiedAccess>]
type SessionLaunchRequest =
    { SessionId : SessionId }

/// The bootstrap URI is a string for Fable portability; it is a plain
/// `http://127.0.0.1:<port>/` local URI in Phase 2.
type SessionLaunchResult =
    { SessionId : SessionId
      ProcessId : string
      LocalBootstrapUri : string }

type StartSession = SessionLaunchRequest -> Async<SessionLaunchResult>

// --- The spawn contract ------------------------------------------------------------------
//
// What the Manager mints for ONE launch, as one value rather than six environment
// variables. The six were minted at a single site and read back with five independent
// `envOr` fallbacks, and the FALLBACKS were the defect: an unset session id fabricated
// one, so a Session Process nobody launched was indistinguishable from one whose launch
// forgot to say who it was. The url and the secret were separately re-matched into a pair
// at three call sites, which is a product type spelled as two strings.

/// The control leg back to the Manager: supervision reports, secrets custody, and this
/// launch's OAuth client registration all authenticate with the one per-launch secret.
///
/// A PAIR, because it has only ever been used as one — a url with no secret cannot be
/// called, and a secret with no url has nowhere to go.
type LaunchControl =
    { Url : string
      Secret : string }

/// Everything the Manager mints for one launch, decoded once at the process boundary and
/// passed downward from there. Nothing reaches back up to the environment for a piece of it.
type Launch =
    { Session : SessionId
      /// Where this session's durable state lives, as the Manager named it. Made absolute
      /// at the boundary that uses it: a relative path works for whatever resolves it once
      /// and silently breaks whatever resolves it twice.
      DataDir : string
      /// The port to listen on. `0` — OS-assigned — is what the Manager always mints, set
      /// explicitly rather than omitted so an operator's stray value cannot reach a child
      /// and pin every session to one port.
      Port : int
      /// `None` = nobody to report to. A bare `yession-session` run is unsupervised, and
      /// its HTTP surface is ungated.
      Control : LaunchControl option
      /// Watch stdin and exit when it closes. The Manager's death closes it — the kernel
      /// does this even on SIGKILL — which is how a session never outlives its Manager.
      ParentGuard : bool }

module Launch =

    /// The one variable the Manager mints and the Session Process decodes.
    ///
    /// MINTED, NEVER AUTHORED. Anything that could set this could claim to be a session the
    /// Manager launched, holding that launch's control secret — which is custody of the
    /// session's secrets and the authority to register as an OIDC client. `yession.yaml`
    /// refuses the whole `YESSION_` prefix for exactly this reason, and the host baseline
    /// is an allowlist, so this never reaches a sandboxed command either.
    [<Literal>]
    let Variable = "YESSION_LAUNCH"

    /// A Session Process nobody launched: `yession-session` run by hand, or a test harness
    /// driving one directly. ONE value, so "there is no Manager" is a thing to point at
    /// rather than five defaults that can disagree about it.
    let unlaunched : Launch =
        { Session = SessionId.local
          DataDir = sprintf ".yession/sessions/%s" (SessionId.value SessionId.local)
          Port = 0
          Control = None
          ParentGuard = false }

    let private controlDecoder : Decoder<LaunchControl> =
        Decode.object (fun get ->
            { Url = get.Required.Field "url" Decode.string
              Secret = get.Required.Field "secret" Decode.string })

    let private decoder : Decoder<Launch> =
        Decode.object (fun get ->
            { Session =
                get.Required.Field
                    "session"
                    (Decode.string
                     |> Decode.andThen (fun raw ->
                         match SessionId.create raw with
                         | Ok id -> Decode.succeed id
                         | Error e -> Decode.fail e))
              DataDir = get.Required.Field "dataDir" Decode.string
              Port = get.Required.Field "port" Decode.int
              Control = get.Optional.Field "control" controlDecoder
              ParentGuard = get.Optional.Field "parentGuard" Decode.bool |> Option.defaultValue false })

    let private encoder (launch: Launch) =
        let control =
            match launch.Control with
            | Some c ->
                [ "control", Encode.object [ "url", Encode.string c.Url; "secret", Encode.string c.Secret ] ]
            | None -> []
        Encode.object
            ([ "session", Encode.string (SessionId.value launch.Session)
               "dataDir", Encode.string launch.DataDir
               "port", Encode.int launch.Port
               "parentGuard", Encode.bool launch.ParentGuard ]
             @ control)

    /// What the Manager puts in the variable.
    let encode (launch: Launch) : string = encoder launch |> Encode.toString 0

    /// What the Session Process reads back.
    ///
    /// Blank or absent is the UNLAUNCHED case, not an error — `yession-session` run by hand
    /// still runs. Anything else that fails to decode IS an error, and fails the boot: a
    /// malformed envelope means the Manager and the session disagree about the contract, and
    /// carrying on under a fabricated identity is how that disagreement goes silent.
    let parse (raw: string) : Result<Launch, string> =
        if isNull (box raw) || raw.Trim() = "" then Ok unlaunched
        else Decode.fromString decoder raw |> Result.mapError (sprintf "%s: %s" Variable)
