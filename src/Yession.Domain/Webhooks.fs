namespace Yession.Domain.Hooks

/// What a session asks the Manager to send it, when something out on the internet posts to
/// one of the Manager's hook endpoints.
///
/// The Manager relays hooks it cannot read. It verifies that a delivery is signed, matches
/// it against the filters sessions have declared, and forwards it — and at no point does it
/// learn which service sent it, what the event means, or what any field is called. The
/// knowledge stays in the session that declared the filter, which is the only place that
/// has the credential to act on it anyway.
///
/// That is what makes this a FILTER rather than a predicate: a session ships the Manager
/// data, never code. Code would have to be interpreted, the Manager would have to implement
/// every construct the code could contain, and a session upgraded to emit a new one would
/// stop working against an older Manager — making the Manager a version ceiling on the
/// sessions it supervises, which is the opposite of why it is kept ignorant.
///
/// So the language is made small enough to have no versions at all: a conjunction of
/// equalities. Every operator that could be added — a disjunction, a negation, a pattern —
/// is one more thing two builds can disagree about, and equality is the one that cannot be
/// read two ways. There is nothing here to extend, and that is the feature.

/// A path into a delivery, as dotted segments.
///
/// A delivery is addressed as ONE document — `headers.x-github-event` and
/// `body.repository.full_name` are the same kind of path — so the language needs no second
/// form for "look in the headers". Header names are matched lowercased, because HTTP does
/// not promise a case and nothing downstream should have to care.
///
/// Private, so a path cannot exist without having been parsed. A segment containing a dot
/// is not addressable; no provider this serves has one, and inventing an escape now would
/// be a syntax to disagree about later.
type FieldPath = private FieldPath of string list

/// What a session will accept from an endpoint: every one of these must hold.
///
/// An empty `Where` matches every delivery to that endpoint, which is the honest reading of
/// "no constraints" and not a special case.
type DeliveryFilter = { Where : (FieldPath * string) list }

module FieldPath =

    let segments (FieldPath path) = path

    /// The path as it travels and as it reads: `body.repository.full_name`.
    let render (FieldPath path) = String.concat "." path

    let create (raw: string) : Result<FieldPath, string> =
        let trimmed = raw.Trim ()
        if trimmed = "" then Error "a field path is empty"
        elif trimmed |> Seq.exists System.Char.IsWhiteSpace then
            Error (sprintf "field path %s contains whitespace" trimmed)
        else
            let parts = trimmed.Split '.' |> List.ofArray
            if parts |> List.exists (fun segment -> segment = "") then
                Error (sprintf "field path %s has an empty segment" trimmed)
            else Ok (FieldPath (parts |> List.map (fun s -> s.ToLowerInvariant ())))

module DeliveryFilter =

    /// Accepts everything on the endpoint it is declared against.
    let everything : DeliveryFilter = { Where = [] }

    /// Does this delivery satisfy every constraint?
    ///
    /// `lookup` is a FUNCTION rather than a parsed document, which is what keeps this
    /// module free of any JSON library and the rule itself a fold over a list. Whoever
    /// holds the bytes resolves a path against them; a path that is absent, or that names
    /// a container rather than a value, answers `None` and fails its constraint — never
    /// matching by accident is the direction that matters here.
    let matches (filter: DeliveryFilter) (lookup: FieldPath -> string option) : bool =
        filter.Where |> List.forall (fun (path, expected) -> lookup path = Some expected)
