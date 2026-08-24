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

module ModelCatalogue =

    /// One lookup per session, kept for as long as the session lives.
    ///
    /// A provider's catalogue does not move while a session runs, and the surface that asks
    /// for it is one a person opens and closes repeatedly — so the first ANSWER is the
    /// answer, and every later reader gets it without another round trip.
    ///
    /// Only a SUCCESS is kept. A failure here is almost always "nothing is connected yet",
    /// and caching that would leave the picker permanently empty for a session that signs
    /// in a minute later — which is precisely the state the connection panel exists to get
    /// out of. So a failure is reported and forgotten, and the next asker tries again.
    let cached (lookup: ListModels) : ListModels =
        let mutable answer : AgentModel list option = None
        fun actor ->
            async {
                match answer with
                | Some models -> return Ok models
                | None ->
                    match! lookup actor with
                    | Ok models ->
                        answer <- Some models
                        return Ok models
                    | Error reason -> return Error reason
            }

    /// The models a catalogue offers, ordered for a person: by name, case-insensitively.
    /// A provider's own order is its release order, its internal ordering, or nothing at
    /// all — none of which is what somebody scanning a list is looking for.
    let ordered (models: AgentModel list) : AgentModel list =
        models |> List.sortBy (fun m -> m.Name.ToLowerInvariant (), ModelId.value m.Id)
