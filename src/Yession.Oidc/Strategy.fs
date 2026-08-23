namespace Yession.Oidc

/// How the provider decides WHO the human at /authorize is. A strategy is a value, not
/// a config flag: the provider core never knows which one is plugged in, so a BYO
/// integration is a second value of this type, not a change to the provider. This slice
/// ships three: deny-everything (`none`, the default), trust-localhost, and
/// trusted-headers (an operator-run authenticating proxy asserts the user in canonical
/// `x-yession-*` headers).

/// The slice of the /authorize request a strategy may inspect.
type AuthenticationContext =
    { /// The TCP peer address of the request (`socket.remoteAddress`).
      RemoteAddress : string option
      /// Query-parameter access, for strategies that carry credentials in the request.
      Query : string -> string option
      /// Request-header access (Node lowercases names), for proxy-asserted identity.
      Header : string -> string option }

/// The claims a strategy verified about the human. Subject is the only required claim;
/// Extra carries strategy-specific claims opaquely (e.g. a capabilities document) for
/// audit and future policy — nothing interprets them yet.
type UserClaims =
    { Subject : string
      DisplayName : string option
      Email : string option
      Picture : string option
      Extra : (string * string) list }

type AuthenticationOutcome =
    /// A real, durable user identity: events may be attributed to it.
    | Attributed of UserClaims
    /// A shared access grant with no attributable user behind it (trust-localhost).
    /// Access, not identity: downstream attribution falls back to the peer connection.
    | Unattributed of subject: string
    | Denied of reason: string

/// The identity a code grant carries from /authorize to /token: the subject plus the
/// claims behind it when the strategy attributed a real user (None = unattributed).
type GrantedIdentity =
    { Subject : string
      Claims : UserClaims option }

module GrantedIdentity =
    /// The grant an outcome earns; None when the outcome is a denial.
    let ofOutcome (outcome: AuthenticationOutcome) : GrantedIdentity option =
        match outcome with
        | Attributed claims -> Some { Subject = claims.Subject; Claims = Some claims }
        | Unattributed subject -> Some { Subject = subject; Claims = None }
        | Denied _ -> None

type AuthenticationStrategy =
    { Name : string
      Authenticate : AuthenticationContext -> Async<AuthenticationOutcome> }

module Strategy =

    /// Deny-everything: the default when the operator configured no strategy. Nothing
    /// authenticates until a strategy is chosen explicitly (`--auth`), so an exposed
    /// endpoint can never fall back to an unintended trust rule.
    let none : AuthenticationStrategy =
        { Name = "none"
          Authenticate = fun _ -> async { return Denied "no authentication strategy configured (start the Manager with --auth)" } }

    let private loopback = Set.ofList [ "127.0.0.1"; "::1"; "::ffff:127.0.0.1" ]

    /// Trust-localhost: any request arriving over the loopback interface IS the single
    /// local user (`sub = "local"`) — unattributed, because everyone on this machine
    /// shares that subject. Locality is the security boundary, made explicit here.
    let localhost : AuthenticationStrategy =
        { Name = "localhost"
          Authenticate =
            fun context ->
                async {
                    match context.RemoteAddress with
                    | Some address when Set.contains address loopback ->
                        return Unattributed "local"
                    | _ ->
                        return Denied "the localhost strategy only authenticates loopback requests"
                } }

    /// The canonical identity headers an authenticating proxy asserts.
    /// The proxy in front of the Manager translates whatever its authenticator gives it
    /// (e.g. Tailscale serve identity headers) into these, and MUST strip them from
    /// inbound client requests — anything that reaches this strategy is trusted verbatim.
    [<Literal>]
    let SubjectHeader = "x-yession-user"
    [<Literal>]
    let NameHeader = "x-yession-user-name"
    [<Literal>]
    let EmailHeader = "x-yession-user-email"
    [<Literal>]
    let PictureHeader = "x-yession-user-picture"
    /// A JSON object of additional claims, carried verbatim into `UserClaims.Extra`
    /// (e.g. Tailscale app capabilities) — opaque to the Manager today.
    [<Literal>]
    let ClaimsHeader = "x-yession-user-claims"

    let private nonBlank (value: string option) : string option =
        value |> Option.bind (fun v -> let t = v.Trim () in if t = "" then None else Some t)

    /// Trusted-headers: the operator's proxy authenticated the human and asserts the
    /// identity in `x-yession-*` headers. Replaces (never composes with) `localhost` —
    /// behind a loopback-terminating proxy every request is loopback, so composing
    /// would authenticate header-less requests as the local user.
    let trustedHeaders : AuthenticationStrategy =
        { Name = "trusted-headers"
          Authenticate =
            fun context ->
                async {
                    match nonBlank (context.Header SubjectHeader) with
                    | None ->
                        return Denied "no x-yession-user identity header; is the authenticating proxy in front of this port?"
                    | Some subject ->
                        return
                            Attributed
                                { Subject = subject
                                  DisplayName = nonBlank (context.Header NameHeader)
                                  Email = nonBlank (context.Header EmailHeader)
                                  Picture = nonBlank (context.Header PictureHeader)
                                  Extra =
                                    match nonBlank (context.Header ClaimsHeader) with
                                    | Some claims -> [ ClaimsHeader, claims ]
                                    | None -> [] }
                } }

    /// Resolve a `--auth` argument to a strategy. None (no argument) is deny-everything;
    /// an unknown name is an error the boot must fail loudly on, never a silent default.
    let ofName (name: string option) : Result<AuthenticationStrategy, string> =
        match name with
        | None -> Ok none
        | Some "none" -> Ok none
        | Some "localhost" -> Ok localhost
        | Some "trusted-headers" -> Ok trustedHeaders
        | Some other -> Error (sprintf "unknown authentication strategy '%s' (expected none, localhost or trusted-headers)" other)
