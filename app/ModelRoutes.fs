module Yession.Host.ModelRoutes

// The browser-facing `/models` route: which models this session can run a turn on.
//
// Provider-neutral by construction. It is handed a `ListModels` and a mount, and it knows
// nothing about who answers it, what the endpoint is, or how a credential is presented —
// which is the whole point of the seam. A second provider is a different `ListModels` and
// no change here, and none in the picker either.
//
// Cookie-gated like the connection panels beside it: the catalogue is a fact about this
// session, so the people in the session may read it and nobody else may.

open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Agent
open Yession.SessionProcess
open Yession.App
open Yession.Host.Interop

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

let private respondJson (res: ServerResponse) (json: string) =
    // `no-store`, like every other gated session surface: the answer is per-credential, and
    // the session already keeps the one copy worth keeping (`ModelCatalogue.cached`). A
    // second copy in the HTTP cache would be the redundant spare.
    res.writeHead (200, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

/// The party a browser request acts as, so the lookup runs on the credential this person
/// could actually use.
///
/// `ActorRef.System` where the deployment attributes nobody: an unattributed browser IS the
/// deployment asking, and it reaches exactly the credentials a deployment may — the
/// session's own and the local one — because that is what the turn-target precedence
/// resolves it to. There is no separate rule here to keep in step.
let private actorOf (identity: CookieIdentity) : ActorRef =
    match identity.Attribution with
    | AttributedUser user -> UserRef user
    | UnattributedAccess -> ActorRef.System

/// Build the `/models` route handler over a `ListModels`.
///
/// A failed lookup answers `502` with the reason rather than an empty list: "this provider
/// offers nothing" and "nobody has connected an account yet" are different facts, and a
/// picker that could not tell them apart would show an empty menu and no way to fix it.
let routes
    (auth: SessionAuth.Auth)
    (list: ListModels)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        match SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0]) with
        | Some SessionRoute.Models ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                Async.StartImmediate (
                    async {
                        match! list (actorOf identity) with
                        | Ok models -> respondJson res (Codec.toString Codec.modelCatalogue models)
                        | Error reason -> respondText res 502 reason
                    })
            true
        | Some _
        | None -> false
