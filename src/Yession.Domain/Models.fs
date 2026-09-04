namespace Yession.Domain.Agent

open Yession.Domain

open System

/// Which model a turn runs on, as a vocabulary that names no provider.
///
/// A model has an id the provider knows it by and a name a person reads, and that is the
/// whole of it. Everything about WHICH provider — the endpoint, the credential, the shape
/// of the reply, the fact that this deployment talks to Anthropic at all — stays on the
/// far side of `ListModels`, so the picker, the synced register and the turn all speak the
/// same provider-neutral pair.

/// A model id, exactly as its provider names it.
///
/// Validated rather than taken on trust: this string is handed to a spawned process as a
/// command-line option, and it arrives from a collaborative register any peer may write.
/// A value with whitespace or control characters in it is not an id any provider has —
/// it is a paste accident or worse, and the moment to refuse it is before it leaves here.
type ModelId = private ModelId of string

module ModelId =

    /// No provider names a model with more than this, and a register any peer may write
    /// should not be able to carry a document.
    let private maxLength = 200

    let create (raw: string) : Result<ModelId, string> =
        if isNull (box raw) then Error "ModelId cannot be null"
        elif String.IsNullOrWhiteSpace raw then Error "ModelId cannot be empty or whitespace"
        else
            let trimmed = raw.Trim ()
            if trimmed.Length > maxLength then
                Error (sprintf "ModelId cannot be longer than %d characters" maxLength)
            elif trimmed |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
                Error "ModelId cannot contain whitespace or control characters"
            else Ok (ModelId trimmed)

    let value (ModelId id) = id

/// One model a provider offers. `Name` is what a person picks from; it falls back to the
/// id where a provider offers no better label, because a picker that renders an empty row
/// is worse than one that renders a raw id.
type AgentModel =
    { Id   : ModelId
      Name : string }

module AgentModel =

    /// A model with a label, defaulting to its id when the provider gave none.
    let create (id: ModelId) (name: string) : AgentModel =
        { Id = id
          Name = if String.IsNullOrWhiteSpace name then ModelId.value id else name.Trim () }

/// Ask the provider which models exist, on a party's authority.
///
/// It takes an actor for the same reason a turn does (Plan 08): the agent has no scope of
/// its own, so every call on a provider runs on somebody's credential. The catalogue a
/// person can see is the catalogue their credential can see.
type ListModels = ActorRef -> Async<Result<AgentModel list, string>>

/// A kept catalogue and the way to drop it, handed back together because they are two
/// halves of one thing: whoever holds the credential state is the only party that can
/// know a kept answer has stopped being one.
type ModelCatalogueCache =
    { /// The lookup, answering from the kept catalogue while it is still an answer.
      List : ListModels
      /// Drop whatever is kept. Called by whatever holds the credential state, when that
      /// state moves under the key an answer was kept for.
      Forget : unit -> unit }

module ModelCatalogue =

    /// How long a kept catalogue is still an answer.
    ///
    /// Long enough that opening the settings drawer four times is one round trip; short
    /// enough that a model released this morning is offered by lunchtime.
    let freshness = TimeSpan.FromMinutes 10.0

    /// A catalogue kept only while it is still an answer to the question that produced it,
    /// which has three parts and each moves on its own:
    ///
    ///   * WHO is asking. `keyOf` names the credential an actor's calls would run on —
    ///     local and cheap, which is what lets a hit cost nothing at all — and the answer
    ///     is kept under it. This used to be kept under nothing, so the first person to
    ///     open the picker filled it for everybody and the next was served a catalogue
    ///     their own credential had never been asked for. A catalogue is a fact about a
    ///     credential; keeping it as a fact about the session is what made that possible.
    ///   * WHICH credential that is. A sign-in, a disconnect or a swap can move it without
    ///     moving the key, so `Forget` is called by whatever holds that state.
    ///   * WHAT the provider offers. Nothing tells a session that a model was released, and
    ///     sessions here live for days, so `ttl` bounds how long an answer stands. The
    ///     comment this replaced said a provider's catalogue does not move while a session
    ///     runs; it does, and a session that had been up a week could not be told.
    ///
    /// One answer is kept, not one per key: a second asker on a different credential
    /// replaces it, which costs a round trip and never serves a list that was somebody
    /// else's.
    ///
    /// Only a SUCCESS is kept. A failure here is almost always "nothing is connected yet",
    /// and caching that would leave the picker permanently empty for a session that signs
    /// in a minute later — which is precisely the state the connection panel exists to get
    /// out of. So a failure is reported and forgotten, and the next asker tries again.
    let keyed
        (now: unit -> DateTimeOffset)
        (ttl: TimeSpan)
        (keyOf: ActorRef -> 'key)
        (lookup: ListModels)
        : ModelCatalogueCache =
        let mutable answer : ('key * AgentModel list * DateTimeOffset) option = None
        { Forget = fun () -> answer <- None
          List =
            fun actor ->
                async {
                    let key = keyOf actor
                    let asked = now ()
                    match answer with
                    | Some (kept, models, at) when kept = key && asked - at < ttl -> return Ok models
                    | _ ->
                        match! lookup actor with
                        | Error reason -> return Error reason
                        | Ok models ->
                            answer <- Some (key, models, asked)
                            return Ok models
                } }

    /// The models a catalogue offers, ordered for a person: by name, case-insensitively.
    /// A provider's own order is its release order, its internal ordering, or nothing at
    /// all — none of which is what somebody scanning a list is looking for.
    let ordered (models: AgentModel list) : AgentModel list =
        models |> List.sortBy (fun m -> m.Name.ToLowerInvariant (), ModelId.value m.Id)
