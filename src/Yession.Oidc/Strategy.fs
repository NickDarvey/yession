namespace Yession.Oidc

/// How the provider decides WHO the human at /authorize is. A strategy is a value, not
/// a config flag: the provider core never knows which one is plugged in, so an upstream
/// OIDC (or any other) strategy is a second value of this type, not a change to the
/// provider. This slice ships exactly one: trust-localhost.

/// The slice of the /authorize request a strategy may inspect.
type AuthenticationContext =
    { /// The TCP peer address of the request (`socket.remoteAddress`).
      RemoteAddress : string option
      /// Query-parameter access, for strategies that carry credentials in the request.
      Query : string -> string option }

type AuthenticationOutcome =
    | Authenticated of subject: string
    | Denied of reason: string

type AuthenticationStrategy =
    { Name : string
      Authenticate : AuthenticationContext -> Async<AuthenticationOutcome> }

module Strategy =

    let private loopback = Set.ofList [ "127.0.0.1"; "::1"; "::ffff:127.0.0.1" ]

    /// Trust-localhost: any request arriving over the loopback interface IS the single
    /// local user (`sub = "local"`). This matches the deployment (every listener binds
    /// 127.0.0.1; locality is already the security boundary) — the strategy makes that
    /// boundary explicit at the one place a future upstream-OIDC strategy will replace.
    let localhost : AuthenticationStrategy =
        { Name = "localhost"
          Authenticate =
            fun context ->
                async {
                    match context.RemoteAddress with
                    | Some address when Set.contains address loopback ->
                        return Authenticated "local"
                    | _ ->
                        return Denied "the localhost strategy only authenticates loopback requests"
                } }
