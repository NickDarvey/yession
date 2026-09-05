module Yession.Host.WebhookRelay

// The Manager's hook relay: opaque bytes in, signature verified, matched against whatever
// filters sessions declared, forwarded to those sessions. It never learns which service
// sent a delivery, what the event means, or what any field is called — a session names the
// paths it cares about (`Yession.Domain.Hooks`) and this module resolves them without
// understanding them.
//
// That ignorance is for MINIMALITY, not containment: a smaller Manager is a smaller single
// point of failure, and a session that does not need the Manager to learn its provider can
// be upgraded on its own. Sessions are trusted — they already hold the credentials they
// would act on — so the Manager verifies the DELIVERY, which arrives from the internet, and
// takes a session's word on the SUBSCRIPTION, which arrives over the authenticated control
// channel from a child it spawned.
//
// The secret is derived, never stored. An operator chooses a webhook secret when they
// register the App (GitHub says so: "type a string to use as a secret key"), so the Manager
// generates one and the operator pastes it in — nothing to find, nothing to record, no
// plaintext secret in the state file. Deriving it from the KEK the credential manager
// already holds is what makes it survive a restart without being written anywhere.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Domain.Hooks

// --- how a provider signs ------------------------------------------------------------

/// How to check a delivery's signature. Three fields, because that is the whole shape of
/// the family this covers: an HMAC-SHA256 over the RAW BODY, digested hex or base64, in a
/// named header, behind an optional prefix. GitHub, Shopify and Linear are all in it.
///
/// Not in it: schemes that sign a CONSTRUCTED string carrying a timestamp — Stripe signs
/// `<t>.<body>` and reads it back out of a structured header, Slack signs `v0:<ts>:<body>`.
/// More configuration does not reach those; they need the scheme itself. The relay forwards
/// a delivery's headers, so the alternative is a session verifying its own endpoint, and
/// that is deliberately not built.
type SignatureSpec =
    { /// Lowercased — Node lowercases request header names on the way in.
      Header : string
      /// `hex` or `base64`, as the provider digests.
      Encoding : string
      /// What sits before the digest in the header value; `""` when nothing does.
      Prefix : string }

module SignatureSpec =

    /// The default, and it is a convention rather than a provider: `X-Hub-Signature-256`
    /// comes from WebSub, which is why GitHub uses it and why defaulting to it teaches the
    /// Manager nothing about GitHub.
    let webSub : SignatureSpec = { Header = "x-hub-signature-256"; Encoding = "hex"; Prefix = "sha256=" }

    /// `header:encoding:prefix`, e.g. `x-shopify-hmac-sha256:base64:`. Split on the first
    /// two colons only, so a prefix may contain anything at all — `sha256=` included, which
    /// is the default's own.
    let decode (raw: string) : Result<SignatureSpec, string> =
        match raw.Trim () with
        | "" -> Ok webSub
        | trimmed ->
            let parts = trimmed.Split ([| ':' |], 3)
            if parts.Length < 2 then
                Error (sprintf "signature spec %s is not header:encoding[:prefix]" trimmed)
            else
                let header = parts.[0].Trim().ToLowerInvariant ()
                let encoding = parts.[1].Trim().ToLowerInvariant ()
                let prefix = if parts.Length = 3 then parts.[2] else ""
                if header = "" then Error "signature spec has no header"
                elif encoding <> "hex" && encoding <> "base64" then
                    Error (sprintf "signature encoding %s is not hex or base64" encoding)
                else Ok { Header = header; Encoding = encoding; Prefix = prefix }

// --- what an operator declared --------------------------------------------------------

    /// Back to the text that decodes to it. The trailing colon appears only when there is a
    /// prefix to put after it, so the empty-prefix form is `header:encoding` — which is one
    /// of the two spellings `decode` accepts for it, and the one worth writing.
    let encode (spec: SignatureSpec) : string =
        if spec.Prefix = "" then sprintf "%s:%s" spec.Header spec.Encoding
        else sprintf "%s:%s:%s" spec.Header spec.Encoding spec.Prefix

/// One endpoint as configured: a name, which rotation of its secret is current, and how a
/// delivery to it is signed.
type EndpointSpec = { Name : string; Rotation : int; Signature : SignatureSpec }

/// The grammar of one `--webhook`, both ways.
///
/// A CODEC rather than a parser, and the encode side is not symmetry for its own sake: it is
/// what the manager page renders, so the section that shows an operator their generated
/// secret can also show the exact option that produced the endpoint — the one thing needed to
/// rotate it, and otherwise invisible, since a rotation counter appears nowhere else.
///
/// It also pins the grammar in a way no parser test can. `decode (encode spec) = spec` holds
/// for every spec, so a decoder that silently drops a field, or an encoder that cannot
/// express one the decoder accepts, is red. The other direction deliberately does NOT hold:
/// `encode (decode text)` canonicalises — a defaulted signature disappears, `@0` disappears,
/// an empty prefix loses its trailing colon — which is what makes the rendered form the one
/// worth copying rather than a transcript of what somebody typed.
module EndpointSpec =

    [<Literal>]
    let private SignatureSeparator = '='

    [<Literal>]
    let private RotationSeparator = '@'

    /// `name[@rotation][=header:encoding[:prefix]]`, e.g. `github`, `github@1`,
    /// `shopify@2=x-shopify-hmac-sha256:base64:`.
    ///
    /// Three separators, each unambiguous by construction: a NAME is letters, digits, `-`
    /// and `_` (below), so neither `@` nor `=` can appear in one; a rotation is digits; and
    /// the signature is split on its first two colons only, so a prefix may hold anything —
    /// including the `=` in the default's own `sha256=`, which is why the split here is on
    /// the FIRST `=` and not the last.
    let decode (raw: string) : Result<EndpointSpec, string> =
        let trimmed = raw.Trim ()
        let head, signature =
            match trimmed.Split ([| SignatureSeparator |], 2) with
            | [| h; sign |] -> h.Trim (), sign
            | _ -> trimmed, ""
        let name, rotation =
            match head.Split ([| RotationSeparator |], 2) with
            | [| n; r |] ->
                n.Trim (),
                (match System.Int32.TryParse (r.Trim ()) with
                 | true, value when value >= 0 -> Ok value
                 | _ -> Error (sprintf "endpoint %s has a rotation that is not a whole number" (n.Trim ())))
            | _ -> head, Ok 0
        if name = "" then Error "an endpoint name is empty"
        elif name |> Seq.exists (fun c -> not (System.Char.IsLetterOrDigit c || c = '-' || c = '_')) then
            // The name is a URL path segment and a declaration's own field at once, so it is
            // held to what both accept rather than escaped for each.
            Error (sprintf "endpoint name %s is not letters, digits, - or _" name)
        else
            rotation
            |> Result.bind (fun r ->
                SignatureSpec.decode signature
                |> Result.map (fun spec -> { Name = name; Rotation = r; Signature = spec }))

    /// Back to the option that decodes to it, in its shortest form: rotation 0 and the
    /// default signature are what absence already means, so writing them would be noise on
    /// a page whose whole job is to be copied.
    let encode (spec: EndpointSpec) : string =
        let rotation = if spec.Rotation > 0 then sprintf "%c%d" RotationSeparator spec.Rotation else ""
        let signature =
            if spec.Signature = SignatureSpec.webSub then ""
            else sprintf "%c%s" SignatureSeparator (SignatureSpec.encode spec.Signature)
        sprintf "%s%s%s" spec.Name rotation signature

    /// Every `--webhook` given, or the first that could not be read. A name declared twice
    /// is refused rather than resolved: two declarations of one endpoint disagree about its
    /// rotation or its signature, and picking either silently is how a deployment serves a
    /// secret the operator did not think they had asked for.
    let decodeAll (declarations: string list) : Result<EndpointSpec list, string> =
        declarations
        |> List.filter (fun raw -> raw.Trim () <> "")
        |> List.fold
            (fun acc raw ->
                acc
                |> Result.bind (fun done' ->
                    decode raw
                    |> Result.bind (fun spec ->
                        if done' |> List.exists (fun s -> s.Name = spec.Name) then
                            Error (sprintf "endpoint %s is declared more than once" spec.Name)
                        else Ok (done' @ [ spec ]))))
            (Ok [])

/// One endpoint as served: its accepted secrets, newest first, and the declaration that
/// produced it — carried so the manager page can show an operator the option to write, which
/// is the only place a rotation counter is visible at all.
type HookEndpoint =
    { Name : string
      /// What an operator wrote (canonicalised): `EndpointSpec.encode` of its spec.
      Declared : EndpointSpec
      /// Newest first, and there is more than one during a rotation: bump the counter, read
      /// the new secret off the manager page, paste it into the provider, and the previous
      /// one keeps working until the counter moves again. Without the overlap every
      /// rotation would have a window where live deliveries are refused.
      Secrets : string list
      Signature : SignatureSpec }

/// The signing secret for an endpoint at a rotation. Derived from the Manager's KEK — the
/// key the OS credential manager already holds for the secret store — so it is stable
/// across restarts, written nowhere, and gone if the KEK is.
let secretAt (kek: string) (name: string) (rotation: int) : string =
    Interop.hmacSha256 kek (sprintf "yession-webhook:%s:%d" name rotation) "base64url"

/// Resolve the endpoints an operator declared into the endpoints the relay serves.
///
/// The current rotation and the one before it are both accepted; rotation 0 has no
/// predecessor and accepts one.
let endpointsFor (kek: string) (specs: EndpointSpec list) : HookEndpoint list =
    specs
    |> List.map (fun spec ->
        { Name = spec.Name
          Declared = spec
          Secrets =
            [ secretAt kek spec.Name spec.Rotation
              if spec.Rotation > 0 then secretAt kek spec.Name (spec.Rotation - 1) ]
          Signature = spec.Signature })

// --- reading a delivery ----------------------------------------------------------------

/// Parse a body into the object a filter can address; `null` for anything that is not one
/// (invalid JSON, an array, a bare value). Deliberately total — a body that cannot be read
/// is a 400, not an exception.
[<Emit("""(function (raw) {
  try { const v = JSON.parse(raw); return (v !== null && typeof v === 'object' && !Array.isArray(v)) ? v : null }
  catch { return null }
})($0)""")>]
let private parseObject (raw: string) : obj = jsNative

[<Emit("""(function (pairs) {
  const out = {}
  for (const [k, v] of pairs) out[String(k).toLowerCase()] = v
  return out
})($0)""")>]
let private headerObject (pairs: (string * string) list) : obj = jsNative

/// Resolve one path against a delivery.
///
/// The first segment names which half — `headers` or `body` — and anything else addresses
/// nothing, which is what makes "a delivery is one document" a rule rather than a
/// convention. Keys are compared case-insensitively against the already-lowercased path,
/// so a provider's camelCase body and HTTP's indifferent header case both resolve; the
/// cost is that two keys in one object differing only by case are not distinguishable, and
/// no provider this serves has a pair like that.
///
/// A path that lands on a container, or on nothing, answers `null` — which fails its
/// constraint. Matching by accident is the direction that would hurt.
[<Emit("""(function (headers, body, segments) {
  let cur = (segments[0] === 'headers') ? headers : (segments[0] === 'body') ? body : null
  if (cur === null || cur === undefined) return null
  for (let i = 1; i < segments.length; i++) {
    if (cur === null || typeof cur !== 'object' || Array.isArray(cur)) return null
    const key = Object.keys(cur).find(k => k.toLowerCase() === segments[i])
    if (key === undefined) return null
    cur = cur[key]
  }
  const t = typeof cur
  return (t === 'string') ? cur : (t === 'number' || t === 'boolean') ? String(cur) : null
})($0, $1, $2)""")>]
let private resolveIn (headers: obj) (body: obj) (segments: string array) : string = jsNative

// --- the relay ---------------------------------------------------------------------------

/// What a session asked for, and which launch to send it to.
type private Subscription =
    { Id : string
      /// The per-launch control secret, which is what `NotificationHub` is keyed by — so a
      /// subscription dies exactly when its launch does, the moment the Manager drops it.
      Secret : string
      Filter : DeliveryFilter }

type Relay =
    { /// What the operator page shows: the endpoints served and the secrets they accept.
      Endpoints : HookEndpoint list
      /// Record a filter for a launch; answers the subscription id.
      Subscribe : string -> DeliveryFilter -> string
      /// Drop one subscription of a launch; `false` when it was not there.
      Unsubscribe : string -> string -> bool
      /// Drop every subscription of a launch that has ended.
      Drop : string -> unit
      /// Take one delivery. Answers the HTTP status: 204 accepted, 401 unsigned or wrongly
      /// signed, 400 unreadable, 404 no such endpoint. Never says whether anything matched.
      Deliver : string -> (string * string) list -> string -> int }

module Relay =

    /// A Manager serving no hook endpoints — the default, and what an operator who
    /// declared none gets. Every delivery is a 404 because no endpoint exists to take it.
    let none : Relay =
        { Endpoints = []
          Subscribe = fun _ _ -> ""
          Unsubscribe = fun _ _ -> false
          Drop = fun _ -> ()
          Deliver = fun _ _ _ -> 404 }

/// Build the relay.
///
/// `notify` is the reverse leg keyed by control secret (`NotificationHub.NotifySecret`), so
/// forwarding a delivery is the same push every other Manager→Session signal rides.
let create
    (endpoints: HookEndpoint list)
    (notify: string -> SessionNotification -> unit)
    (mintId: unit -> string)
    : Relay =

    let mutable subscriptions : Subscription list = []

    let verified (endpoint: HookEndpoint) (headers: (string * string) list) (body: string) : bool =
        match headers |> List.tryFind (fun (name, _) -> name.ToLowerInvariant () = endpoint.Signature.Header) with
        | None -> false
        | Some (_, presented) ->
            // Every accepted secret is tried, which is what makes a rotation seamless. Each
            // comparison is constant-time; trying two of them leaks only that there are two.
            endpoint.Secrets
            |> List.exists (fun secret ->
                let digest = Interop.hmacSha256 secret body endpoint.Signature.Encoding
                Interop.timingSafeEqualStr presented (endpoint.Signature.Prefix + digest))

    let deliver (name: string) (headers: (string * string) list) (body: string) : int =
        match endpoints |> List.tryFind (fun e -> e.Name = name) with
        | None -> 404
        | Some endpoint ->
            if not (verified endpoint headers body) then 401
            else
                let parsed = parseObject body
                if isNull parsed then 400
                else
                    let headerDoc = headerObject headers
                    let lookup (path: FieldPath) =
                        match resolveIn headerDoc parsed (FieldPath.segments path |> Array.ofList) with
                        | null -> None
                        | value -> Some value
                    // A snapshot, so a subscription arriving mid-fan-out is picked up by the
                    // next delivery rather than mutating what this one is walking.
                    for subscription in List.ofSeq subscriptions do
                        if DeliveryFilter.matches subscription.Filter lookup then
                            notify
                                subscription.Secret
                                (WebhookDelivered (subscription.Id, endpoint.Name, headers, body))
                    204

    { Endpoints = endpoints
      Subscribe =
        fun secret filter ->
            let id = mintId ()
            subscriptions <- subscriptions @ [ { Id = id; Secret = secret; Filter = filter } ]
            id
      Unsubscribe =
        fun secret id ->
            let keep, drop =
                subscriptions |> List.partition (fun s -> not (s.Secret = secret && s.Id = id))
            subscriptions <- keep
            not (List.isEmpty drop)
      Drop = fun secret -> subscriptions <- subscriptions |> List.filter (fun s -> s.Secret <> secret)
      Deliver = deliver }

// --- the route ---------------------------------------------------------------------------

[<Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = jsNative

/// Every header, as pairs, names lowercased by Node on the way in.
///
/// An ARRAY, converted at the boundary: a JS array is what `Object.entries` yields and what
/// Fable's array maps onto, while an F# list is a linked structure it does not. Typing this
/// as a list compiles and then quietly matches nothing.
[<Emit("Object.entries($0.headers).map(([k, v]) => [k, Array.isArray(v) ? v.join(', ') : String(v)])")>]
let private headerPairsOf (req: Interop.IncomingMessage) : (string * string) array = jsNative

let private headersOf (req: Interop.IncomingMessage) : (string * string) list =
    headerPairsOf req |> List.ofArray

let private readBody (req: Interop.IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + Interop.bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respond (res: Interop.ServerResponse) (status: int) (text: string) =
    res.writeHead (status, JsInterop.createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ])
    |> ignore
    res.``end`` text

/// Handle a delivery. `false` when the path is not ours, so the composing server falls
/// through — the `Control.tryHandle` shape.
///
/// Deliberately NOT behind the Manager's operator authentication, and that asymmetry is the
/// point: the caller is whatever service an operator pointed at this URL, and it
/// authenticates by signing the body. There is no session to have a cookie, and an unsigned
/// delivery is refused here rather than let through to be judged later.
let tryHandle (relay: Relay) (req: Interop.IncomingMessage) (res: Interop.ServerResponse) : bool =
    let path = pathnameOf req.url
    if not (path.StartsWith "/hooks/") then false
    elif req.``method`` <> "POST" then
        respond res 405 "a delivery is a POST"
        true
    else
        readBody req (fun body ->
            // The status is all the caller learns. Whether anything was subscribed is not
            // in it, because a provider's delivery log would otherwise become a way to
            // probe what this deployment is watching.
            match relay.Deliver (path.Substring "/hooks/".Length) (headersOf req) body with
            | 204 ->
                res.writeHead (204, JsInterop.createObj [ "cache-control", box "no-store" ]) |> ignore
                res.``end`` ""
            | 401 -> respond res 401 "bad signature"
            | 400 -> respond res 400 "body is not a json object"
            | status -> respond res status "no such hook endpoint")
        true

/// Compose the relay from what the process can see.
///
/// The refusal lives here rather than in the composition root, because it is a rule about
/// this module's own state: a derived secret is only as durable as the key it comes from,
/// and an ephemeral store mints a fresh KEK every boot. Serving endpoints on one would hand
/// the operator a secret to paste into a provider that stops working at the next restart —
/// silently, and only for inbound deliveries. Better to refuse at boot and say why.
let compose
    (declarations: string list)
    (kek: unit -> Async<Result<string option, string>>)
    (notify: string -> SessionNotification -> unit)
    (mintId: unit -> string)
    : Async<Result<Relay, string>> =
    async {
        match declarations |> List.filter (fun raw -> raw.Trim () <> "") with
        | [] -> return Ok Relay.none
        | declared ->
            match EndpointSpec.decodeAll declared with
            | Error e -> return Error e
            | Ok specs ->
                match! kek () with
                | Error e -> return Error (sprintf "the signing key could not be read: %s" e)
                | Ok None ->
                    return
                        Error
                            "--webhook needs a durable secret store, because each endpoint's \
                             signing secret is derived from the key that seals it. This deployment \
                             has an ephemeral store, so the key — and every secret an operator \
                             pasted into a provider — would be different after a restart. Run with \
                             a usable OS credential manager, or declare no endpoints."
                | Ok (Some payload) -> return Ok (create (endpointsFor payload specs) notify mintId)
    }
