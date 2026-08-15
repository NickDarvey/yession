namespace Yession.App

open Yession.Domain

/// The client's visual language, authored entirely in F# by composing Tailwind's own
/// utility classes into typed, named values. Tailwind supplies the utilities; F# supplies
/// the composition; the TOKENS — palette, type ramp, caps tracking, structural spacing,
/// fonts, keyframes — live in the `@theme` block of `app/tailwind.css`, and nothing here
/// carries a raw hex or a structural pixel count that has a token.
///
/// The design is Metro / Zune (pre-Windows 8) worn by a Slack/Cursor workspace anatomy —
/// see docs/plans/02-metro-zune-styling.md. The rules that keep it coherent:
///
///   Type grid — everything sits on a 4px baseline rhythm, as paired size/line tokens:
///     label 11/16 · small 13/16 · body 15/24 · pivot 19/24 · heading 28/32 ·
///     wordmark 32/36 (px), plus the mono pair code 12/16 · code-sm 11/16.
///   The sidebar wordmark and the main header share one band (`h-band`, items-end,
///   common bottom padding) so their baselines align across the hairline.
///
///   Affordance — statuses are TEXT (colored caps, at most a small dot; never filled,
///   never boxed). Buttons are bordered Metro rectangles (transparent; hover brightens
///   the border; press fills solid). Nothing else carries a border.
///
///   Strokes — every border in the product is composed from the `Stroke` vocabulary
///   below (width, tone, and what interaction does to it) into a handful of phrases —
///   `field`/`fieldSelect`/`fieldBare`, `rowBase`/`rowLift`, `focusRing` — and those
///   phrases are what surfaces wear. No bare `border-*` utility is written outside
///   `Stroke`, and none at all in the views. The two remaining literals are variant-
///   PREFIXED (`md:[.nav-alt_&]:border-r-0`), undoing a column's divider while it is
///   shut: the variant is part of the class name, so there is no token to compose.
///
///   Colour — technocool: blue is interactive and the agent's voice; green is live/ok
///   and the human pulse. People are identified by tiny square display pics, not name
///   colours. The blue→green gradient appears exactly ONCE: the composer's focus edge.
module Style =

    /// Join utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

    // --- Strokes: the border vocabulary every bordered thing is built from ---------------
    //
    // A stroke is three independent choices — a WIDTH, a TONE at rest, and what happens
    // under interaction — and naming the three separately is what stops a settings field,
    // a queued row and a terminal composer from each inventing their own. Nothing below
    // this section writes a `border-*` utility directly; it composes these.
    //
    // Two widths, and only two:
    //   `ring` — 1px on all four sides. What you TYPE IN or PRESS (fields, buttons).
    //   `lead` — 2px on the leading edge. What is LISTED (queued messages and commands,
    //            a collaborator's collapsed draft), where the edge says what the row IS.
    //   `underline` — the single exception, and it is a shape not a third width: a heading
    //            that edits in place (the session title), where a rectangle drawn round
    //            28px type reads as a box rather than a field.
    //
    // Two rules keep the states honest, and they are opposites on purpose:
    //   * A RING answers to INTERACTION. It brightens on hover and goes blue while
    //     focused — every field, every button, no exceptions. Blue is interactive, and
    //     focus is the most interactive a thing gets.
    //   * A LEAD answers to the MODEL and never to the pointer: green = live and still
    //     editable, blue = waiting on you, err = wrong, a peer's colour = whose it is.
    //     Interaction lifts the SURFACE instead (`rowLift`), so a row's tone can never be
    //     misread as "the mouse happens to be here".
    module Stroke =

        // Widths.
        let ring = "border"
        let lead = "border-l-2"
        let underline = "border-0 border-b"

        // Tones at rest. `hair` is the quiet separator, `rim` a step brighter for a
        // control at rest; the rest carry the meanings they carry everywhere else.
        let hair = "border-hair"
        let rim = "border-edge"
        let faint = "border-ink-faint"
        let blue = "border-blue"
        let green = "border-green"
        /// Present but unpainted — a tab that is not the selected one still occupies its
        /// border box, so selecting it moves no text.
        let clear = "border-transparent"

        // What a RING does under interaction. `focus:` fires on a control that takes focus
        // ITSELF — which is every ring in the product, because a host whose real input is a
        // descendant (a mounted ProseMirror) draws no ring at all: it wears `fieldBare` and
        // lets its container carry the signal.
        let hoverRim = "hover:border-edge"
        let hoverInk = "hover:border-ink"
        let hoverErr = "hover:border-err"
        let focus = "focus:border-blue"
        /// The title's affordance: dotted until you are in it, then solid blue.
        let dotted = "border-dotted focus:border-solid"

        /// The region divider: a hairline on ONE side, separating two surfaces rather than
        /// enclosing a control. Named per side because which side it is on is structural,
        /// and it answers to nothing — not hover, not focus, not the model.
        let dividerTop = "border-t border-hair"
        let dividerBottom = "border-b border-hair"
        let dividerLeft = "border-l border-hair"
        let dividerRight = "border-r border-hair"

    /// The keyboard focus ring, worn by every control that can take focus. One value, so a
    /// control cannot ship with half a ring (`outline-2` with no `outline`, which is how the
    /// terminal tabs used to draw nothing at all).
    let focusRing =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    /// The same ring held further off — for type with no box of its own, where a ring on the
    /// glyphs' own edge reads as an underline.
    let private focusRingFar =
        "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-4"

    // --- Typography (the ramp lives as `--text-*` tokens in app/tailwind.css) -----------
    // Each `text-<step>` utility sets size AND line-height together, so a size can never
    // drift off its 4px line box; `leading-*` composes over a step where a context needs
    // a different box (roster rows and fields sit 13/20, a draft summary clamps 13/32).

    let wordmark = "font-extralight text-wordmark tracking-[-0.02em] text-ink"
    let heading = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink truncate"
    let body = "font-light text-body text-ink"
    let small = "font-light text-small text-ink-faint"
    /// The caps voice — one size, one tracking, semibold — worn by every label, status,
    /// button, and author line. Colour composes at the use site.
    let private caps = "font-semibold text-label tracking-caps uppercase"

    /// The voice a body is written in: Noto Serif for a person, and the system's own Noto Sans
    /// for the agent, which is part of the system (`--font-human`/`--font-agent`). Siblings, so
    /// a conversation reads as one voice in two registers rather than two typefaces arguing —
    /// and the difference is one of CLASS rather than of letterform detail, which is what makes
    /// it survive 15px. Attribution the eye reads before the words — the author line and the avatar say the same thing, but
    /// they say it once at the top, and a long turn scrolls past them.
    ///
    /// A face, not a colour: the caps author line already spends the palette on this
    /// distinction (`who` vs `whoAgent`), and message bodies are held at full-strength `ink`
    /// on purpose — what was said is the content, and dimming half of it to mark who said it
    /// would trade legibility for a fact the line above already carries.
    ///
    /// Worn by everything a person is CURRENTLY writing too — the open draft, a collapsed
    /// peer's draft, a queued message, every field they type into — so text does not change
    /// typeface at the moment it is sent. A command line is the exception and stays
    /// `font-terminal` (`fieldMono`): what is being written there is machine input.
    let messageVoice (isAgent: bool) = if isAgent then "font-agent" else "font-human"
    let label = caps + " text-ink-faint"
    let mono = "font-terminal text-code text-ink"
    let monoOut = "font-terminal text-code-sm text-ink-faint whitespace-pre-wrap"

    // --- Statuses: text only — never filled, never boxed --------------------------------

    let statusOk = caps + " text-green"
    let statusRun = caps + " text-blue"
    let statusErr = caps + " text-err"
    let statusFaint = caps + " text-ink-faint"
    /// The small leading dot a live status may carry (`bg-current` follows the text colour).
    let statusDot = "inline-block w-1.5 h-1.5 rounded-full bg-current mr-1.5 align-[1px]"
    let statusDotPulse = statusDot + " animate-pulse2 motion-reduce:animate-none"

    /// A standalone dot given its colour explicitly (`bg-green` etc. composed at the use
    /// site) for a row whose text is a DIFFERENT colour — `bg-current` would fight the
    /// composed colour utility, and which `bg-*` wins is stylesheet order, not authoring
    /// order.
    let syncDot = "inline-block w-1.5 h-1.5 rounded-full shrink-0"
    let syncDotPulse = syncDot + " animate-pulse2 motion-reduce:animate-none"
    /// The sidebar's one-line sync summary: dot and status words on one baseline.
    let syncRow = "flex items-center gap-2"

    // --- Buttons: bordered Metro rectangles — hover brightens, press fills --------------

    /// Sized by construction, not padding arithmetic: the box is `h-control` with the line
    /// flex-centred in it — the old `py-[7px]` was that same height, hand-derived and easy to
    /// break. A FIELD is built the same way from the same token (`fieldFace`), which is what
    /// makes a button and an input in one row actually line up.
    let private btnBase =
        cls [ "bg-transparent cursor-pointer font-ui"; caps
              "h-control px-3.5 inline-flex items-center justify-center transition-colors"
              Stroke.ring; focusRing ]

    /// The three faces, as (rest tone, hover, press) — the only thing that varies between
    /// them, so a fourth would be three tokens rather than another hand-written string.
    let btn =
        cls [ btnBase; Stroke.rim; "text-ink-dim"; Stroke.hoverInk; "hover:text-ink active:bg-ink active:text-bg" ]

    let btnPrimary =
        cls [ btnBase; Stroke.blue; "text-blue hover:text-blue-bright active:bg-blue active:text-bg" ]

    let btnDanger =
        cls [ btnBase; Stroke.rim; "text-ink-dim"; Stroke.hoverErr; "hover:text-err active:bg-err active:text-bg" ]

    /// The name of a LISTED record, when the name itself opens it. The row's primary act
    /// is carried by its content rather than by another rectangle in the right rail —
    /// which is how a table of five sessions ends up wearing ten bordered buttons, all
    /// shouting the same loudness as the page's one real CTA. Ink at rest, so a list of
    /// names reads as a list of names; blue and underlined under the pointer, because
    /// blue is what interactive means here. Underline (not colour) at rest would say the
    /// same thing, but on every row at once — the same noise in a quieter register.
    let recordLink =
        cls [ body; "no-underline hover:text-blue hover:underline decoration-1 underline-offset-4"; focusRing ]

    /// The mark that says a record link LEAVES (the session serves its own origin). Faint
    /// and small: it is a signpost, not a second status.
    let recordLinkMark = "text-code text-ink-faint ml-1 align-[1px]"

    /// A button with nothing to do YET — composed over one of the faces above, never used
    /// alone. It stays in the layout and in focus order (its work is coming, and a control
    /// that appears mid-sentence moves everything under the reader's hand); the BORDER carries
    /// the waiting, dropping to the quiet rim so the rectangle reads as an outline not yet
    /// filled. Which is this design's own vocabulary: a Metro button IS its border, hover
    /// brightens it, press fills it.
    ///
    /// Deliberately NOT an opacity dim: fading `text-blue` on the composer's `#111` takes it
    /// from 6.5:1 to about 2.5:1, and an 11px caps label at 2.5:1 is below the AA floor this
    /// product holds. The word stays exactly as legible as it was; only the frame changes.
    let btnWaiting = "!border-edge"

    /// Square icon buttons — self-contained, NOT composed over `btn`: Tailwind emits `p-0`
    /// BEFORE `px-*`/`py-*` in the stylesheet, so "btn + p-0" kept the text button's padding
    /// and crushed the glyph into a corner of a lopsided box (measured live: 30×24, ×
    /// touching the bottom-right edge).
    let private btnIconBase =
        cls [ "bg-transparent cursor-pointer p-0 grid place-items-center transition-colors"; Stroke.ring; focusRing ]

    let private btnIconNeutralFace =
        " " + cls [ Stroke.rim; "text-ink-dim"; Stroke.hoverInk; "hover:text-ink active:bg-ink active:text-bg" ]

    let private btnIconDangerFace =
        " " + cls [ Stroke.rim; "text-ink-dim"; Stroke.hoverErr; "hover:text-err active:bg-err active:text-bg" ]

    /// 24px square: the queue's reorder controls.
    let btnIcon = btnIconBase + " w-6 h-6" + btnIconNeutralFace
    /// 24px square, destructive: delete / disconnect.
    let btnIconDanger = btnIconBase + " w-6 h-6" + btnIconDangerFace
    /// 32px square, destructive: the composer's discard — the same height as the Send
    /// button it sits beside, so the pair shares top and bottom edges.
    let btnIconDangerLg = btnIconBase + " w-8 h-8" + btnIconDangerFace

    /// 32px square and BORDERLESS: the verb at the trailing edge of a field.
    ///
    /// A Metro button IS its rectangle, so dropping the rectangle is not a quieter button, it
    /// is a different kind of control — and it earns that by living inside the field it acts
    /// on rather than beside it. `SEND →` was the word and the arrow saying one thing twice
    /// in a 93px box on a strip of its own; the arrow alone, on the line you just wrote, says
    /// it once. The name goes on `aria-label`, which is where an icon-only control keeps it.
    let private btnInField =
        cls [ "w-8 h-8 shrink-0 grid place-items-center bg-transparent border-0 cursor-pointer p-0"
              "transition-colors"; focusRing ]
    let btnSendInField = cls [ btnInField; "text-blue hover:text-blue-bright" ]
    /// Waiting for something to send. The same control in the same place, at the weight of a
    /// thing with nothing to do — never `disabled`, in either spelling: an empty composer is
    /// not a blocked one.
    let btnSendInFieldWaiting = cls [ btnInField; "text-ink-faint hover:text-ink" ]
    let btnDiscardInField = cls [ btnInField; "text-ink-faint hover:text-err" ]
    /// Chrome, not an action: the small sidebar collapse/reveal chevrons. They lean the way
    /// they travel on hover and lead further on press — the only motion chrome earns, and the
    /// reason the two directions are separate values rather than one class plus a guess.
    let private navChevronBase =
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-small p-1.5 -m-1.5 "
        + "flex items-center gap-1 transition-[translate,color] duration-150 ease-out "
        + "motion-reduce:transition-none " + focusRing

    let navChevronBack = navChevronBase + " hover:-translate-x-0.5 active:-translate-x-1"
    let navChevronForward = navChevronBase + " hover:translate-x-0.5 active:translate-x-1"

    /// Worn by every control you TYPE INTO, and nowhere else. iOS zooms the page in when it
    /// focuses a text control whose type is under 16px and never zooms back out — tapping the
    /// terminal's 12px command line left the whole column magnified and shifted, which is not
    /// something a person can undo without pinching the page back themselves.
    ///
    /// The size is the fix. `maximum-scale=1` in the viewport tag stops the same zoom, and
    /// stops a person zooming the page AT ALL — a WCAG 1.4.4 failure, and the UI baseline
    /// (AGENTS.md) is a floor rather than a preference. So the phone gets 16px in the places
    /// where a keyboard is coming, and the ramp is the ramp everywhere else.
    ///
    /// `--text-touch` carries no line-height pair, so this sets the SIZE alone and each field
    /// keeps the line box its own step gave it.
    let private touchType = "max-md:text-touch"

    // --- Fields: ONE face, worn by every input in the product ----------------------------
    // A field is the surface tone inside a hairline ring that brightens on hover and goes
    // blue while focused. The settings inputs, the terminal command line and the queued
    // command all wear it; what varies is the TYPE it holds and the WIDTH its row gives it,
    // never the chrome. `fieldFace` therefore sets no width and no font — those belong to
    // the caller, and baking them in is how the three drifted apart in the first place.

    /// Built exactly as `btnBase` is, and from the same `h-control`: the box sets the height
    /// and the content is centred in it. It used to set `py-2` and let the line box decide,
    /// which made an input 42px, a select 40px and a button 32px — three heights in one row.
    /// `inline-flex` is what centres the content of the one field that is not an input: the
    /// device code is a `span`, and a span does not centre itself in a fixed height.
    let fieldFace =
        cls [ "bg-surface outline-none appearance-none h-control px-3 transition-colors"
              "inline-flex items-center"
              Stroke.ring; Stroke.hair; Stroke.hoverRim; Stroke.focus ]

    /// What a field holds, as opposed to what it looks like: the body scale, never the title's.
    ///
    /// The UI voice, not the human one — the distinction is SPEECH, not keystrokes. A composer,
    /// a queued message and a peer's draft wear `messageVoice` because what is typed there
    /// becomes a message and keeps the face it was written in. A session's display name, an API
    /// token, a mode select are chrome a person operates: nothing is being said, so there is no
    /// attribution to make, and a serif form control on a sans page reads as a mistake rather
    /// than a signal.
    let private fieldType =
        cls [ "font-ui font-light text-small leading-5 text-ink placeholder:text-ink-faint" ]

    /// A settings field (input/select), filling the column it sits in.
    let field = cls [ fieldFace; fieldType; "w-full"; touchType ]

    /// The same field where the ROW gives it a width rather than the column — the Manager's
    /// forms lay three of them out side by side. Public because the alternative is what was
    /// here before: the Manager spelling the whole face inline, which drifted to a 42px input
    /// beside a 32px button.
    let fieldOf (width: string) = cls [ fieldFace; fieldType; width; touchType ]

    /// A select. Everything a field is, plus room for the mark below it — `appearance-none`
    /// (see `fieldFace`) takes the platform's caret away, and a menu with no caret is a text
    /// box that will not take text.
    let fieldSelect = cls [ fieldFace; fieldType; "w-full pr-9" ]

    /// Its mark, drawn beside it. Absolutely positioned in the wrapper the select sits in and
    /// deaf to the pointer, so the whole rectangle still opens the menu.
    ///
    /// The WRAPPER carries the width — `fieldSelect` always fills it — so a row that sizes its
    /// own controls sizes the wrapper (`fieldSelectWrapOf`) and the mark still lands on the
    /// box's right edge.
    let fieldSelectWrap = "relative w-full"
    let fieldSelectWrapOf (width: string) = cls [ "relative"; width ]
    let fieldSelectMark = "pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-ink-faint"

    /// The chrome-LESS field: an input whose CONTAINER already carries the stroke (the
    /// composer's box, a listed row's leading edge), so the control itself must draw
    /// nothing. `[&_.ProseMirror]:outline-none` is not tidying: a mounted editor puts its
    /// `contenteditable` INSIDE this host, so an `outline-none` on the host never reaches
    /// it and the browser draws its default focus box — a white rectangle, in a design
    /// with no rectangles. The focus signal is the container's (a gradient edge, a lift).
    let private fieldBare =
        "bg-transparent border-0 outline-none resize-none [&_.ProseMirror]:outline-none"

    /// The mono field, chrome-less — a command line inside a row that already has a leading
    /// edge (a queued command, a collaborator's slot). Double chrome, a bordered field inside
    /// an edged card, is what made a queued COMMAND look like a different kind of thing from
    /// a queued MESSAGE, which is the one thing they are not.
    let fieldMonoBare =
        cls [ "flex-1 min-w-0"; fieldBare; "font-terminal text-code text-ink placeholder:text-ink-faint"; touchType ]

    // --- Listed rows: the leading edge says what the row IS ------------------------------
    // Every row in a list — a queued message, a queued command, a peer's collapsed draft —
    // is this: a surface with a 2px leading edge whose TONE is the row's state, lifting a
    // tone while the pointer or the caret is in it. The tone is composed at the use site
    // (`Stroke.green`, `Stroke.blue`) or set inline when it is a peer's own colour.

    let private rowBase = cls [ "flex bg-surface transition-colors"; Stroke.lead ]
    /// A row you can act on: the surface lifts, the edge does not move.
    let private rowLift = "hover:bg-surface-2 focus-within:bg-surface-2"

    // --- Pivots: the sidebar's two destinations, set as type ----------------------------
    // Zune navigated by WORDS — big, quiet, lowercase, with a thin chevron pointing the way the
    // surface was about to move — and Courier's chrome earned its place by being set rather than
    // drawn. `settings ›` and `‹ back` are ONE control, mirrored: same size, same foot of the
    // same column, so pressing it leaves the word replaced and the mark flipped, in place. (The
    // head is identity — the wordmark, then the settings title — and never navigation; the two
    // fought for the 280px band when they shared it, and two chevrons a thumb apart read as a
    // pair of arrows rather than a way in and a way out.)
    //
    // Everything that marks them as interactive is a RESPONSE: the word brightens to ink, the
    // mark turns blue and steps the way it points, and a press sends it further. Nothing at rest
    // but type.

    /// One step below the settings title (28/32) and two below the wordmark (32/36), on the
    /// same 4px rhythm: a destination, never a heading.
    let private pivotBase =
        "group bg-transparent border-0 cursor-pointer flex items-center gap-2 "
        + "font-extralight text-pivot tracking-[-0.01em] lowercase "
        + "text-ink-faint hover:text-ink focus-visible:text-ink transition-colors duration-150 ease-out "
        + "motion-reduce:transition-none " + focusRingFar

    let navPivot = pivotBase

    let private pivotMarkBase =
        "block transition-[translate,color] duration-150 ease-out motion-reduce:transition-none "
        + "group-hover:text-blue group-focus-visible:text-blue"

    /// Into settings — the column turns and the mark leads right.
    let pivotMarkForward =
        pivotMarkBase + " group-hover:translate-x-1 group-focus-visible:translate-x-1 group-active:translate-x-2"

    /// Back to the session — the same step, mirrored.
    let pivotMarkBack =
        pivotMarkBase + " group-hover:-translate-x-1 group-focus-visible:-translate-x-1 group-active:-translate-x-2"

    // --- Tiny square display pics (never round) -----------------------------------------
    // Two-tone checkers in the blue/green family stand in until real avatars exist; the
    // variant is picked by hashing the peer id so identity is stable without name colours.
    // The checker hexes are deliberately NOT theme tokens: they are artwork constants, and
    // each class must appear as the same literal in the `@source inline` mirror in
    // app/tailwind.css — a var() inside would decouple nothing and complicate the mirror.

    let avatar = "w-5 h-5 shrink-0"
    let avatarSm = "w-3.5 h-3.5 shrink-0"

    let private checker (a: string) (b: string) =
        sprintf "bg-[conic-gradient(from_0deg,%s_25%%,%s_0_50%%,%s_0_75%%,%s_0)]" a b a b

    let private humanCheckers =
        [| checker "#1ba1e2" "#0b5d85"
           checker "#a8dd00" "#55700a"
           checker "#17c3b2" "#0a5c54"
           checker "#4ab8f0" "#1a6a96"
           checker "#7fb800" "#3d5a05" |]

    /// A stable checker for a human peer id.
    let humanAvatar (id: string) : string =
        let hash = id |> Seq.fold (fun acc c -> acc * 31 + int c |> abs) 7
        humanCheckers.[hash % humanCheckers.Length]

    /// The agent's mark: a dark square holding a small solid blue square.
    let agentAvatar =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-blue"

    let agentAvatarSm =
        "bg-agent-ground grid place-items-center after:content-[''] after:w-1.5 after:h-1.5 after:bg-blue"

    // --- Workspace regions ---------------------------------------------------------------
    // Two presentation bits live on the root <html> element, outside `#app`, so they survive
    // every re-render and stay out of the model: `nav-alt` (toggled by [data-nav-toggle]) and
    // `settings-open` (by [data-settings-toggle]). Default = sidebar visible on desktop,
    // off-canvas on mobile; `nav-alt` = the inverse. Expressed with arbitrary variants so it
    // stays plain Tailwind.

    /// The 280px column. It holds TWO faces — the workspace nav and settings (`navPane` /
    /// `settingsPane`) — because settings is a place you go, not a thing that covers what you
    /// were reading. Collapsing on desktop animates the column's width shut; on mobile the
    /// column is an off-canvas drawer that slides over the conversation.
    let sidebar =
        "relative w-side shrink-0 bg-panel h-full overflow-hidden z-40 " + Stroke.dividerRight + " "
        + "md:transition-[width] md:duration-200 md:ease-out "
        + "md:[.nav-alt_&]:w-0 md:[.nav-alt_&]:border-r-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:w-[min(var(--spacing-side),84vw)] "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out max-md:-translate-x-[101%] "
        + "max-md:[.nav-alt_&]:translate-x-0 motion-reduce:transition-none"

    /// One face of the column: the two are stacked in place and held at the column's full
    /// width, so nothing reflows while the column animates shut.
    ///
    /// `visibility` is in the transition list on purpose — it is what keeps the hidden face out
    /// of the tab order and the accessibility tree, and transitioning it holds `visible` for the
    /// whole fade OUT (a discrete step at the end) while flipping instantly on the way IN.
    /// `opacity-0` alone would leave focusable controls behind an invisible panel.
    let private paneBase =
        "absolute inset-y-0 left-0 w-side max-md:w-[min(var(--spacing-side),84vw)] flex flex-col px-6 pb-5 "
        + "overflow-y-auto transition-[opacity,visibility] duration-200 ease-out motion-reduce:transition-none"

    let navPane = paneBase + " [.settings-open_&]:opacity-0 [.settings-open_&]:invisible"

    let settingsPane =
        paneBase + " opacity-0 invisible [.settings-open_&]:opacity-100 [.settings-open_&]:visible"

    // Zune's signature motion, recast: the two faces do not merely cross-fade — the arriving
    // face's rows slide in from the side a beat apart, and the leaving face's go in one piece,
    // the way it came from. Directional, fast, and never delayed on the way out.
    //
    // The delays are LITERAL class names, one value per lane, because Tailwind scans this source
    // for class names: a `sprintf "delay-[%dms]"` would compose a class that is never generated
    // (the same trap the avatar checkers hit — see `@source inline` in app/tailwind.css).
    // `translate`, not `transform`: Tailwind v4's `translate-x-*` utilities set the CSS
    // `translate` property, so a transition list naming `transform` animates nothing and the
    // rows would jump into place. (Measured on the live page — computed `transform` stayed
    // `none` through the whole toggle.)
    let private laneBase =
        "transition-[translate,opacity] duration-200 ease-out motion-reduce:transition-none"

    let private navLaneOut = " [.settings-open_&]:-translate-x-6 [.settings-open_&]:opacity-0 [.settings-open_&]:delay-0"
    /// The nav's rows, in arrival order (they return staggered and leave together).
    let navLane0 = laneBase + navLaneOut
    let navLane1 = laneBase + " delay-[60ms]" + navLaneOut
    let navLane2 = laneBase + " delay-[120ms]" + navLaneOut

    let private settingsLaneIn = " translate-x-6 opacity-0 [.settings-open_&]:translate-x-0 [.settings-open_&]:opacity-100"
    /// The settings rows, in arrival order (they arrive staggered and leave together).
    let settingsLane0 = laneBase + settingsLaneIn
    let settingsLane1 = laneBase + settingsLaneIn + " [.settings-open_&]:delay-[60ms]"
    let settingsLane2 = laneBase + settingsLaneIn + " [.settings-open_&]:delay-[120ms]"

    /// Mobile-only backdrop behind the open drawer; clicking it closes (data-nav-toggle).
    let scrim = "hidden max-md:[.nav-alt_&]:block fixed inset-0 z-30 bg-black/60"

    /// The shared header band (`--spacing-band`): baselines align across the sidebar/main
    /// hairline because all three heads compose the same token.
    let sideHead = "h-band shrink-0 flex items-end justify-between pb-5"
    let sideSection = "flex flex-col gap-2 py-4 " + Stroke.dividerTop
    let sideSectionFirst = "flex flex-col gap-2 pb-4"
    let sideRow = "flex items-baseline justify-between gap-2"
    /// A roster row aligns on the TEXT BASELINE, so the 11px caps ("you", a status) sit on
    /// the 13px name's baseline instead of floating box-centred beside it.
    let person = "flex items-baseline gap-2.5 font-light text-small leading-5 text-ink-dim"
    /// The avatar opts back out: a box has no baseline (it would park its bottom edge on
    /// the line), so it centres in the row the way it always did.
    let personAvatar = "self-center"
    let commandCard = "flex flex-col gap-1 px-3 py-2 bg-surface"

    /// The generated read surface (Plan 15). A query answers with rows, fields, or one
    /// value, and these are the three renderings — defined ONCE here because they are
    /// what every future query gets to look like, including the ones nobody has written.
    ///
    /// The table scrolls inside itself (`overflow-x-auto`): a sidebar is narrow, a row can
    /// be wide, and a column that overflows its container is how a settings pane starts
    /// scrolling sideways as a whole.
    let queryTable = "w-full overflow-x-auto"
    /// Column headings carry `ink-dim`, not `ink-faint`: they are 11px caps, which is
    /// below the 24px/19px-bold threshold where 3:1 would do, so they need the 4.5:1 ratio
    /// against `surface` that `ink-dim` gives (CLAUDE.md, UI baseline).
    let queryHeadCell = caps + " text-ink-dim text-left font-normal pr-4 pb-1 whitespace-nowrap"
    let queryCell = "font-light text-small leading-5 text-ink-dim pr-4 py-0.5 whitespace-nowrap"

    let mainColumn = "flex-1 flex flex-col min-w-0 h-full"

    /// The phone's band is a COMPRESSED one, not a different anatomy — but it has to hold what
    /// the desktop's does not: the session id, which is out of flow above `md` and in flow
    /// below it (see `titleId`). At 64px it did not. The 28/32 title plus its 16px id line is
    /// 51px, and 51 + the 12px foot left the heading's box starting at y=0 — the title's own
    /// top edge on the viewport's, which on a phone reads as a heading sliced off by the
    /// browser (measured live at 390: `top: 0`). 80px is the smallest step that seats that
    /// stack with air above it.
    let header =
        "relative h-band shrink-0 flex items-end gap-4 px-8 pb-5 max-md:h-20 max-md:px-4 max-md:pb-3 " + Stroke.dividerBottom

    /// Indent the heading one avatar column (20px + 12px gutter) so its left edge sits
    /// exactly on the message-text column below.
    let headerTitle = "ml-8"
    /// The header's right-hand group: sync status, and — only while the sidebar is off screen —
    /// the agent's absence. `pb-[1px]` is optical, not rhythm: it drops the 11px caps line's
    /// baseline onto the wordmark/title baseline (pb-1 left it 3px high, measured live).
    let headerAside = "ml-auto shrink-0 flex items-end gap-5 pb-[1px]"
    let headerStatus = "shrink-0"

    /// The agent's absence, FOLLOWING the surface that normally says it: shown only when the
    /// sidebar column (which holds the real call to action) is collapsed or off-canvas — which
    /// on a phone is most of the time. Never both at once, so it is a relocation, not a repeat.
    /// Same visibility rule as `navReopen`, for the same reason.
    let headerNoAgent =
        "bg-transparent border-0 cursor-pointer " + caps + " text-blue hover:text-blue-bright transition-colors "
        + focusRing + " "
        + "hidden md:[.nav-alt_&]:block max-md:block max-md:[.nav-alt_&]:hidden"

    /// The degradation strip between the header and the timeline: a hairline notice, never a
    /// modal and never a blocker — the client below it stays fully usable.
    let degradedBanner =
        "shrink-0 flex items-baseline gap-3 px-8 py-2 bg-surface max-md:px-4 " + Stroke.dividerBottom

    // --- Editable session title ------------------------------------------------------------

    /// The title block: the editable heading over its dim secondary id. `relative` anchors
    /// the absolutely-positioned remote-cursor overlays; `ml-8` keeps it on the content column.
    let titleWrap = "relative flex flex-col min-w-0 ml-8"

    /// The title itself: the heading, worn by a text input. No chrome except a subtle dotted
    /// underline (the editable affordance) that goes solid blue on focus. Edits in place, no
    /// save button — the model is the collaborative `Title` text. `md:top-[2px]` is optical:
    /// a 28/32 line box holds its baseline 2px higher over the shared bottom edge than the
    /// wordmark's 32/36 does, so the input steps down to put both on one line (measured
    /// live; mobile has no cross-column baseline to meet, so no nudge there).
    let titleInput =
        cls [ "w-full min-w-0 bg-transparent outline-none px-0 py-0"
              Stroke.underline; Stroke.dotted; Stroke.faint; Stroke.focus
              "font-extralight text-heading tracking-[-0.01em] lowercase text-ink"
              "placeholder:text-ink-faint truncate relative md:top-[2px]" ]

    /// The session id, shown small and dim under the title as a stable secondary identifier.
    /// On md+ it hangs OUT OF FLOW below the title, into the header band's bottom padding:
    /// in flow it added 18px under the title inside the bottom-aligned stack and lifted the
    /// title's baseline that far off the wordmark's (measured 41.5 vs 61 at 1440). On mobile
    /// the sidebar is off-canvas (no baseline to meet) and the band is only 64px, so the id
    /// stays in flow there.
    let titleId =
        "font-terminal text-code-sm text-ink-faint truncate mt-0.5 "
        + "md:absolute md:top-full md:left-0 md:right-0"

    /// A collaborator's selection highlight in the title: an absolutely-positioned span the
    /// browser sizes to `lo..hi` by measurement (the translucent background is set inline).
    /// Ignores pointer events so it never blocks typing; a collapsed selection has zero width.
    let remoteCursor = "absolute top-0 h-8 pointer-events-none rounded-sm"
    /// The caret bar inside a remote selection, offset to the peer's `head` by the browser.
    let remoteCursorCaret = "absolute top-0 w-0.5 h-8 -ml-px"
    /// The peer-name pill floating just above a remote caret.
    let remoteCursorLabel =
        "absolute -top-3 left-0 whitespace-nowrap font-semibold text-[9px] leading-3 "
        + "tracking-[0.08em] uppercase px-1 text-bg"

    /// The reopen chevron, floated in the gutter left of the title so it never shifts the
    /// heading off the content column. Hidden while the sidebar is visible.
    let navReopen =
        "absolute left-2 bottom-4.5 w-6 h-6 place-items-center hidden md:[.nav-alt_&]:grid "
        + "max-md:grid max-md:[.nav-alt_&]:hidden max-md:bottom-2.5"

    // --- Timeline --------------------------------------------------------------------------

    let timeline =
        "flex-1 overflow-y-auto px-8 py-6 flex flex-col gap-6 max-md:px-4 max-md:py-4 max-md:gap-5"

    /// Message rhythm: one 16px meta line + 8px gap + n×24px body lines, on a
    /// `20px avatar · 12px gutter · content` grid.
    let message = "grid grid-cols-[20px_1fr] gap-x-3 gap-y-2 max-w-[46rem]"
    let messageAvatar = "row-span-2 -mt-0.5" // optical: square top ≈ cap height
    let messageMeta = "flex items-baseline gap-2.5"
    let who = caps + " text-ink-dim"
    let whoAgent = caps + " text-blue"
    let messageBody = "col-start-2 font-light text-body text-ink"
    let messageBodyStreaming = "col-start-2 font-light text-body text-ink-dim"

    let caret =
        "inline-block w-[7px] h-[15px] bg-blue align-[-2px] ml-0.5 animate-blink motion-reduce:animate-none"

    /// The empty timeline's own mark: the streaming caret, standing where the first message
    /// will land. Dimmed, because it is an invitation rather than an event — a full-strength
    /// caret in an empty room reads as something already happening.
    ///
    /// Aligned to the message grid's content column (20px avatar + 12px gutter) so the first
    /// real message appears exactly where the caret was standing, rather than stepping sideways
    /// as it replaces it.
    let timelineIdle = "pl-8 max-md:pl-8"
    let caretIdle = caret + " opacity-50"

    /// The same caret while this client is still READING what it already had (Plan 20). It
    /// pulses rather than blinks, which is the vocabulary every other "working on it" in this
    /// app already uses (`statusDotPulse`), and it is full strength: something IS happening,
    /// unlike the dimmed invitation beside it.
    let caretReading = "inline-block w-[7px] h-[15px] bg-blue align-[-2px] ml-0.5 animate-pulse2 motion-reduce:animate-none"

    /// Present to a screen reader, absent to everyone else. For a state whose whole expression
    /// is a moving mark: the pulse says "reading" to people who can see it, and this says the
    /// same thing to the people who cannot.
    let srOnly = "sr-only"

    /// Terminal work in the chat (Plan 14, stage 1): one line, subtler than a message, and a
    /// real `<button>` — so it is keyboard-operable and focus-ringed by construction rather
    /// than by a handler bolted onto a div.
    ///
    /// It sits on the message grid's CONTENT column (`20px` avatar + `12px` gutter) so a run
    /// of chips between two messages lines up with the prose rather than drifting under the
    /// avatars. The palette stays `text-ink-dim`/`text-ink-faint` — chips are the busiest
    /// thing the chat will carry, and they must read as texture next to what people said.
    let chatChip =
        cls [ "w-full max-w-[46rem] bg-transparent cursor-pointer text-left"
              "flex items-baseline gap-2 pl-[32px] py-0.5"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// Who ran it — the same caps voice a message's author line wears, one step fainter.
    let chatChipWho = caps + " text-ink-faint shrink-0"
    /// The command itself: mono, truncated to one line. A chip that wrapped to three would
    /// stop being a chip.
    let chatChipCommand = "font-terminal text-code-sm text-ink-dim truncate min-w-0 flex-1"
    /// A stretch item's sentence — prose, not mono: nothing was typed that we recorded.
    let chatChipText = "font-light text-small text-ink-dim truncate min-w-0 flex-1"

    /// A turn's tool calls (Plan 16): a `<details>` on the same content column as the chips,
    /// so a chatty turn reads as one quiet line until somebody wants the detail.
    let chatToolRun = "w-full max-w-[46rem] pl-[32px] py-0.5"
    /// Its summary. A real `<summary>` rather than a button, so the disclosure is the
    /// browser's and arrives keyboard-operable and correctly announced.
    let chatToolSummary =
        cls [ "flex items-baseline gap-2 cursor-pointer list-none"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// One call inside an expanded run: the tool it called, then how it went.
    let chatToolCall = "flex items-baseline gap-2 py-0.5"
    /// `namespace/name` — mono, because it is an identifier and reads as one.
    let chatToolName = "font-terminal text-code-sm text-ink-dim truncate min-w-0"
    /// The arguments as recorded. Dim and truncated: this is evidence, not content.
    let chatToolArgs = "font-terminal text-code-sm text-ink-faint truncate min-w-0 flex-1"

    /// One agent burst (Plan 20, stage 4). A `<details>` on the same content column the chips
    /// and tool runs sit on, for the same reason: a turn that ran twelve commands reads as
    /// one line until somebody wants the twelve.
    let chatTaskCard = "w-full max-w-[46rem] pl-[32px] py-0.5"
    /// Its summary. A real `<summary>`, so the disclosure is the browser's and arrives
    /// keyboard-operable and correctly announced.
    let chatTaskSummary =
        cls [ "flex items-baseline gap-2 cursor-pointer list-none"
              "text-ink-dim hover:text-ink transition-colors duration-150 ease-out"
              focusRing ]
    /// The counts, at the end of the summary line. Baseline-aligned with the sentence beside
    /// them so the glyphs sit on the text's line rather than floating above it.
    let chatTaskCounts = "flex items-baseline gap-2 shrink-0"

    /// A repo note in the timeline (Plan 14): one quiet act-line, indented past the
    /// avatar gutter so the reading edge lines up with message bodies.
    let actNote = "max-w-[46rem] pl-[32px]"
    let actNoteText = caps + " text-ink-faint"

    /// Read-only rendered Markdown in the timeline (the mirror of the composer's live
    /// formatting). Preflight strips heading/list defaults, so each rendered element carries
    /// its own utilities — the same "F# composes utilities, no hand CSS" rule as everything
    /// else. Blocks share a tight vertical rhythm; only the first/last drop their outer margin.
    let proseP = "[&:not(:first-child)]:mt-2"
    let proseH1 = "text-pivot leading-7 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    // 17px is deliberately off the ramp: the one intermediate a four-level heading scale
    // needs between pivot (19) and body (15).
    let proseH2 = "text-[17px] leading-6 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    let proseH3 = "text-body font-semibold text-ink [&:not(:first-child)]:mt-2 mb-1"
    let proseH4 = "text-small leading-5 font-semibold uppercase tracking-[0.08em] text-ink-dim [&:not(:first-child)]:mt-2 mb-1"
    let proseUl = "list-disc pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseOl = "list-decimal pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseLi = "[&:not(:first-child)]:mt-1"
    let proseStrong = "font-semibold text-ink"
    // Inline code keeps the paragraph's line box (no leading of its own), so only the size
    // is set — 13px, the small step, but written bare to leave line-height inherited.
    let proseCode = "font-terminal text-[13px] bg-surface-2 text-ink px-1 py-0.5"
    let prosePre = "font-terminal text-code leading-5 bg-surface-2 text-ink p-3 [&:not(:first-child)]:mt-2 overflow-x-auto whitespace-pre-wrap"
    let proseQuote = Stroke.lead + " " + Stroke.hair + " pl-3 text-ink-dim [&:not(:first-child)]:mt-2"
    let proseLink = "text-blue underline decoration-1 underline-offset-2 hover:text-blue-bright"
    let proseHr = "border-0 " + Stroke.dividerTop + " my-3"

    // --- Agent activity strip ----------------------------------------------------------------

    let activity =
        "h-12 shrink-0 flex items-center gap-3 px-8 bg-panel max-md:px-4 " + Stroke.dividerTop

    let activityPulse = "w-2 h-2 bg-blue animate-pulse2 motion-reduce:animate-none"
    let activityText = "font-light text-small text-blue"
    let activityTurn = "text-label text-ink-faint tabular-nums max-md:hidden"

    // --- Queue: editable until drained; the green leading edge says so ------------------------

    let queue = "shrink-0 flex flex-col gap-0.5 px-8 pt-4 max-md:px-4"
    let queueHead = "flex items-baseline gap-3 pb-2"
    let queueCount = caps + " text-green"

    /// Green AT REST, not on hover: "still editable" is a fact about the entry, and the
    /// terminal's queued commands (`terminalQueuedReady`) already said it that way — a
    /// queued message and a queued command are the same act, so they wear the same edge.
    let queueItem =
        cls [ "group items-center gap-3 h-10 px-3"; rowBase; Stroke.green; rowLift ]

    /// `focus-within`, not `focus`: the caret lands in the editor MOUNTED INSIDE this host,
    /// so `focus:` on the host never fires and the brightening never happened.
    let queueInput =
        cls [ "flex-1 min-w-0 self-center h-5"; fieldBare
              messageVoice false
              "font-light text-small leading-5 text-ink-dim focus-within:text-ink"; touchType ]

    let queueTools =
        "flex gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 max-md:opacity-100 transition-opacity"

    // --- Composer: the one gradient in the product lives on its focus edge --------------------
    // A COMPOSER IS A BAND, not a box on a ground. It used to be a bordered box inside a padded
    // section inside a bordered column — a box in a box in a box, of which only the innermost
    // one could be typed in, and each nested rectangle bought nothing but another edge to
    // notice. The band runs the full width of its column and is told apart from what is above
    // it the way any two surfaces here are: a tone, and a rule.

    /// The band a message is written in. Lifts a tone while anything inside it has focus, so
    /// "this is where I am typing" is legible at a glance and not only from the 2px rule.
    let composer =
        "group relative shrink-0 flex flex-col bg-surface focus-within:bg-surface-2 transition-colors"

    /// The band's top rule, in two parts — because it is doing two jobs and one element could
    /// only ever do one of them.
    ///
    /// The gradient used to be scaled to NOTHING until focus, so an untouched session was a
    /// black column containing a black box: `#111` barely separates from the `#000` canvas, and
    /// the edge — the one thing marking "this is where you type" — appeared only once you had
    /// already found it and clicked. Leaving it up at rest instead would spend the focus
    /// gesture. So: a dim white hairline that is always there (the marker), and the gradient
    /// growing over it on focus (the gesture).
    ///
    /// It is the same pair that used to run down the composer's LEFT edge, turned ninety
    /// degrees to become the band's separator from the column above. Same 2px, same
    /// blue→green — Zune's orange→pink signature, recast — still spent exactly once in the
    /// product. Worn by the message composer and the terminal's command band alike, because
    /// they are the same object on two surfaces.
    let bandRail = "absolute inset-x-0 top-0 h-0.5 bg-ink/15"
    let bandEdge =
        "absolute inset-x-0 top-0 h-0.5 bg-grad scale-x-0 origin-left transition-transform "
        + "duration-300 ease-out group-focus-within:scale-x-100 motion-reduce:transition-none"

    /// A ROW, since the actions moved into it: the box was 56px of which 40 was a strip under
    /// the text holding a 93px bordered rectangle that said Send and then drew an arrow saying
    /// it again. The verb belongs at the trailing edge of the line you just wrote, which is
    /// where it now is — the same shape the terminal's command line takes, so a person moving
    /// between the two columns meets one object twice instead of two conventions.
    ///
    /// Carries no fill and no edge of its own any more: the band it sits in is the surface.
    let draftBox = "relative flex items-end"

    /// Chrome-less by construction (`fieldBare`): the band carries the tone and the rule, and
    /// the focus signal is the gradient growing across it.
    ///
    /// ONE LINE at rest, growing upward on focus. A composer that is 96px of empty box for
    /// most of a session is spending the conversation's height on the promise of a message
    /// rather than on the messages; it takes the room when it is being used and gives it back
    /// when it is not. `max-height` rather than `height` so the growth is animatable and so a
    /// long draft still scrolls inside rather than pushing the timeline off the top.
    let draftInput =
        cls [ "block w-full max-h-10 overflow-hidden transition-[max-height] duration-200 ease-out"
              "group-focus-within:max-h-64 group-focus-within:overflow-y-auto motion-reduce:transition-none"
              fieldBare
              messageVoice false
              "font-light text-body text-ink placeholder:text-ink-faint px-4 py-2"; touchType ]

    /// The writing side of the box: whose message it is, then the message. A column only
    /// because someone else's draft says so above the words; your own — the case that is
    /// almost always on screen — is the input and nothing else.
    let draftBody = "flex-1 min-w-0 flex flex-col"

    /// Send sits at the TRAILING edge — where the eye ends the line it just wrote, and where
    /// every send button a person has ever used lives — with discard as its quiet neighbour
    /// and whoever is typing beside them.
    ///
    /// The keyboard hint that used to sit on this row is gone from the layout. It was true and
    /// it taught something, but it spent a permanent strip saying what one press teaches; it
    /// survives as `aria-keyshortcuts` on the control it describes, which is where a screen
    /// reader looks for it and where it cannot go stale.
    let draftCommit = "shrink-0 flex items-center gap-1 pr-2 pb-2"
    let draftAuthor = "pl-4 pt-2 " + caps + " text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    //
    // Its leading edge is the AUTHOR'S colour (set inline, from `PeerColour`) — the same
    // move the terminal's peer-draft row makes, and the reason is the same: the row's whole
    // subject is whose words these are, so the edge should say it rather than repeat a
    // generic hover tint.
    let draftSummary =
        cls [ "group w-full items-center gap-3 h-8 pl-4 pr-2 text-left cursor-pointer"
              rowBase; rowLift; focusRing ]

    let draftSummaryName = "shrink-0 " + caps + " text-ink-faint"

    /// The clamped body: the same read-only editor as anywhere else, held to one line. `truncate`
    /// on the host would fight ProseMirror's block children, so the clamp is on its descendants.
    let draftSummaryBody =
        "flex-1 min-w-0 " + messageVoice false + " font-light text-small leading-8 text-ink-dim "
        + "overflow-hidden whitespace-nowrap [&_*]:inline [&_*]:truncate [&_*]:m-0"

    /// Who is in this draft right now: one dot per live caret, coloured by peer (`PeerColour`).
    let draftEditors = "shrink-0 flex items-center gap-1 pr-1"
    let draftEditorDot = "inline-block w-1.5 h-1.5 rounded-full"

    /// Starts your own draft, collapsing whoever's is open — the escape hatch from joining.
    let draftNew =
        "self-end bg-transparent border-0 cursor-pointer px-4 py-2 " + caps
        + " text-ink-faint hover:text-blue transition-colors " + focusRing

    // --- Settings ------------------------------------------------------------------------------
    // Settings is the column's other face, not a drawer over the conversation: you go there and
    // come back, and the thing you were reading never moves. Its open state is one bit on the
    // root <html> element (`settings-open`, toggled by [data-settings-toggle]).

    /// The settings face's header band: the same 88px rhythm as the nav and the main header, so
    /// the three baselines still align when the column changes face.
    let settingsHead = "h-band shrink-0 flex items-end justify-between pb-5"
    let settingsTitle = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink"

    // --- The agent's absence -------------------------------------------------------------------
    // Said ONCE, in the section that lists who is in this session, because that is where a
    // missing member is missing. It used to be said three times over (a sidebar row, a strip
    // above the composer, and the settings copy); repetition made it wallpaper, not a prompt.
    //
    // The absent state is the SAME roster row as the live one — same avatar cell, same
    // right-aligned status slot — so connecting an agent flips "no agent" to "ready" in
    // place; nothing moves and no box appears or collapses. (It used to be a boxed card,
    // which broke the roster's geometry and made the connect moment a layout jump.) The
    // prompt hangs beneath the row, on the roster's text column.

    let noAgentBlock = "flex flex-col gap-2"
    /// The prompt reuses the roster's own grid — a 20px avatar column and the text column,
    /// with the roster's 10px gutter — so the edge centres under the avatar and the text
    /// lands on the text column BY CONSTRUCTION, not by pixel arithmetic.
    let noAgentPrompt = "grid grid-cols-[20px_1fr] gap-x-2.5"
    /// The edge itself: the product's 2px edge width, in the agent's blue, centred in the
    /// avatar column and spanning the prompt's height.
    let noAgentEdge = "w-0.5 justify-self-center bg-blue"
    /// The prompt's text column: the explainer over its one action.
    let noAgentBody = "flex flex-col gap-2"
    /// Full-width within the column so it reads as the section's one action.
    let noAgentAction = "w-full"

    // --- Terminals (Plan 13) -------------------------------------------------------------------
    // The conversation column's mirror on the right: a strip of open terminals, the blocks
    // that have run in the selected one, and beneath them the composer — the message
    // composer's sibling, because queueing a command and queueing a message are the same act.
    //
    // It is a COLUMN, not an overlay. The conversation never moves when terminals open, for
    // the same reason settings is the sidebar's other face rather than a drawer over the
    // timeline: reading something and then having it slide out from under you is the thing
    // this shell does not do.

    /// The pane. Mirrors `sidebar`'s geometry (a fixed column on desktop that animates shut,
    /// an off-canvas column on mobile) reflected to the right edge.
    ///
    /// On a phone it takes the WHOLE width (Plan 14, stage 5). The columns collapse to one
    /// and opening a tab switches that column to the pane, keeping the tab strip — so the
    /// chat is a back-swipe away rather than an overlay to dismiss, and desktop and phone
    /// stay the same mental model. The old 92vw left a sliver of chat showing beside it,
    /// which reads as a dialog: something you get rid of rather than somewhere you are.
    /// On desktop the width is a CUSTOM PROPERTY the shell root carries, not the token — the
    /// token is its default. 420px was picked as "the width the content actually has" and is
    /// 20 columns short of the 80 a terminal prints; rather than guess a better number for
    /// everybody, the split is draggable and remembered (`PaneShell.installPaneResize`). The
    /// transition is suppressed while dragging, or the column chases the pointer a frame late.
    let terminalPanel =
        "relative w-term md:w-[var(--term-w,var(--spacing-term))] shrink-0 bg-panel h-full overflow-hidden z-40 flex flex-col "
        + Stroke.dividerLeft + " "
        + "md:transition-[width] md:duration-200 md:ease-out md:[.term-resizing_&]:transition-none "
        + "md:[.term-closed_&]:w-0 md:[.term-closed_&]:border-l-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:right-0 max-md:w-full max-md:border-l-0 "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out "
        + "max-md:[.term-closed_&]:translate-x-[101%] motion-reduce:transition-none"

    /// Held at the column's full width so nothing reflows while the column animates shut.
    let terminalPane = "absolute inset-0 md:w-[var(--term-w,var(--spacing-term))] w-term max-md:w-full flex flex-col"

    /// The split between the chat and this column, made draggable — a real `separator`, so it
    /// answers to the arrow keys as well as the pointer. Desktop only: on a phone the pane IS
    /// the column and there is nothing to divide.
    ///
    /// Inside the panel rather than straddling the divider, because the panel clips its
    /// overflow — which also rules out an outline for focus, so focus is the same blue the
    /// hover shows, at full strength.
    let terminalResize =
        cls [ "max-md:hidden absolute left-0 inset-y-0 w-1.5 z-50 cursor-col-resize"
              "bg-transparent hover:bg-blue/50 focus-visible:bg-blue focus-visible:outline-none"
              "transition-colors motion-reduce:transition-none" ]

    /// The column's head: a PROPERTIES BAR, not a title.
    ///
    /// It used to be the 88px band the sidebar and the main header wear, carrying the word
    /// "terminals" — the largest text on a phone screen, telling a reader looking at terminals
    /// that these are terminals, for 10% of the height. What a reader cannot otherwise know is
    /// which terminal this is and whether the agent has to ask before it runs, and neither had
    /// anywhere to live: the second was a labelled form control directly above the command
    /// line, in the highest-attention position on the surface, for a setting changed twice a
    /// session.
    ///
    /// So: 40px, at BOTH breakpoints, holding what this terminal IS plus the acts that are
    /// about the terminal rather than about the command you are typing. The band token is not
    /// missed — a band is for a heading, and this is a readout.
    let terminalHead = "h-10 shrink-0 flex items-center gap-2 px-3 " + Stroke.dividerBottom
    /// Which terminal this is. The one thing in the bar that is neither a fact you can change
    /// nor an act — so it is the only thing in ink.
    let terminalHeadName = "flex-1 min-w-0 truncate font-ui text-small text-ink"

    /// A property of the terminal, stated as a fact and changed by touching the fact.
    ///
    /// Not a labelled control: the label existed because the VALUES could not stand alone
    /// ("the agent's commands" is an answer with its question missing). Values that say what
    /// they are — `approval: agent` — leave a label nothing to add, and the control collapses
    /// to the readout it always wanted to be.
    ///
    /// BARE, in the field vocabulary's second sense (`fieldBare`): it draws no box because the
    /// bar it sits in carries the structure. Which is the honest reading — this is not a field
    /// you type into that happens to look quiet, it is a stated fact you can touch, and a
    /// control that invented its own border would be the third thing the chrome-consistency
    /// suite exists to rule out.
    let private terminalPropBase =
        cls [ caps; "bg-transparent border-0 outline-none appearance-none cursor-pointer shrink-0"
              "pl-1.5 pr-4 py-1 transition-colors"; focusRing ]
    let terminalProp = cls [ terminalPropBase; "text-ink-faint hover:text-ink" ]
    /// The same readout wearing the alarm, for the setting that lets commands run unasked. A
    /// safety property whose most permissive value is the quietest thing on screen is
    /// backwards; this is the one place in the pane where err is not an error.
    let terminalPropAlert = cls [ terminalPropBase; "text-err" ]
    /// The readout is a `<select>`, so it needs the caret `appearance-none` took away — drawn
    /// beside it, deaf to the pointer so the whole readout still opens the menu.
    let terminalPropWrap = "relative flex items-center shrink-0"
    let terminalPropMark = "pointer-events-none absolute right-0 top-1/2 -translate-y-1/2 text-ink-faint"

    /// An act that is about THIS TERMINAL rather than about the command you are writing:
    /// closing it, stepping back through its recording. In the bar's voice — quiet text — not
    /// as another bordered rectangle in a strip already full of them.
    let private terminalBarActBase =
        cls [ caps; "bg-transparent cursor-pointer shrink-0 px-1.5 py-1 transition-colors"
              Stroke.clear; focusRing ]
    let terminalBarAct = cls [ terminalBarActBase; "text-ink-faint hover:text-ink" ]
    /// The acts, grouped and set apart from the facts beside them. Same tone, same voice —
    /// what separates them is the gap, because a bar of eight evenly spaced words reads as one
    /// run-on and a reader has to parse it to find the verb.
    let terminalBarActs = "flex items-center gap-1 ml-3"
    /// Closing a terminal kills what is running in it. It reddens under the hand — the same
    /// promise the danger button makes, kept without the rectangle.
    let terminalBarDanger = cls [ terminalBarActBase; "text-ink-faint hover:text-err" ]

    /// The open-terminal strip: one chip per terminal, scrolling horizontally when there are
    /// more than fit rather than wrapping into a second band that shifts the whole column.
    ///
    /// The tabs run down to the strip's own divider so their marks land on it rather than
    /// drawing a second line above it (see `tabBase`). `pb-px` is the one pixel their pulled-up
    /// borders occupy: this strip scrolls, and anything painted outside a scroll container's
    /// padding box is something to scroll TO — a 1px overhang made the row scrollable on the
    /// vertical axis (`overflow-x` forces `overflow-y` to `auto`) and Chromium drew a scrollbar
    /// down the side of the tabs for it.
    let terminalTabs = "shrink-0 flex items-stretch gap-1 px-3 pt-2 pb-px overflow-x-auto " + Stroke.dividerBottom
    /// The tabs THEMSELVES, and nothing else. `role="tablist"` is a promise about what its
    /// children are, and the strip also holds two things that are not tabs — "+ new" and
    /// "close" — which a reader was told were tabs (four of them, in a list of two) and which
    /// the strip's own arrow-key walk had to step over. The row keeps its look; the promise
    /// now covers only what keeps it.
    ///
    /// A real box rather than `display: contents`: a role on a contents box is dropped from
    /// the accessibility tree by some browsers, which would trade one wrong tablist for no
    /// tablist at all. `shrink-0` keeps the strip the thing that scrolls when the tabs
    /// outgrow it, exactly as when they were its direct children.
    let terminalTabList = "flex items-stretch gap-1 shrink-0"
    /// A tab is marked by an UNDERLINE, not by a box.
    ///
    /// The selected one used to be a full blue rectangle — which in this design is the button
    /// vocabulary, and specifically the primary one: `btnPrimary` is a blue-bordered rectangle
    /// wearing blue text. So the strip's answer to "which terminal am I looking at" shouted at
    /// exactly the weight of the page's one real call to action, next to the thing you actually
    /// press. Nothing in a tab strip is a CTA; a tab is a statement about where you are.
    ///
    /// The mark is `Stroke.underline`, the same hairline the session title wears — an
    /// established, chrome-light affordance in this vocabulary rather than a new width. Every
    /// tab carries the border box (`Stroke.clear` when it is not the one), so selecting moves
    /// no text, and the selection is said twice quietly instead of once loudly: ink rather than
    /// faint, over a blue rule.
    ///
    /// And it is the strip's OWN rule, not a second one. `-mb-px` pulls each tab's bottom
    /// border down onto the divider the strip already draws, so there is one line under the
    /// row and the selected tab paints its segment of it: an unselected tab's border is
    /// transparent and the hairline shows straight through. Drawn at the tab's own height
    /// instead, it was a second rule 23px above the first — two horizontal lines saying one
    /// thing, which is the ornament this pass exists to remove.
    let private tabBase =
        cls [ caps; "bg-transparent cursor-pointer px-2.5 pt-1.5 pb-2 -mb-px max-w-40 truncate transition-colors"
              Stroke.underline; focusRing ]
    let terminalTab = cls [ tabBase; Stroke.clear; "text-ink-faint hover:text-ink" ]
    let terminalTabActive = cls [ tabBase; Stroke.blue; "text-ink" ]
    /// Adds a terminal. The one action in the strip that is not a selection — so it wears the
    /// strip's own quiet face and says `+`, rather than being the bordered rectangle that
    /// out-shouted every tab beside it. What it does is named for anyone not reading pixels.
    ///
    /// The strip's other non-tab, `close`, is gone from here entirely: it kills a running
    /// terminal, and dressing that as a sibling of "switch to this one" put the most
    /// destructive control in the pane one pixel from the most routine. It is an act about
    /// the terminal, and it lives with the terminal's other properties, in the bar.
    let terminalTabNew = cls [ tabBase; Stroke.clear; "text-ink-faint hover:text-ink" ]
    /// A tab's presence marks: one dot per peer whose caret is in THAT terminal, so a
    /// collaborator typing a command in a terminal you are not looking at is visible from
    /// the strip rather than only from inside it.
    let terminalTabPeers = "inline-flex items-center gap-0.5 ml-1.5 align-[1px]"

    /// A tab a person opened from the chat (Plan 14, stage 2): the tab itself plus its own
    /// close control. A group rather than one control, because a `<button>` inside a
    /// `<button>` is not markup — and the two do different things.
    let paneTabGroup = "inline-flex items-stretch shrink-0"
    /// The pin control, sized to the tab band beside it (Plan 20, stage 1).
    ///
    /// Two faces of ONE control, and neither is destructive: pinned is blue, exactly as the
    /// selected tab is blue, because both mean "this is mine and it stays"; unpinned is the
    /// faint ink every quiet affordance wears. It replaced a close control in the `err`
    /// face, and the colour change is the substance — nothing in the strip destroys anything
    /// any more, so nothing in it wears the danger tone.
    let paneTabPin =
        cls [ "bg-transparent cursor-pointer px-1.5 grid place-items-center transition-colors"
              Stroke.clear; "text-ink-faint hover:text-blue"; focusRing ]

    let paneTabPinned =
        cls [ "bg-transparent cursor-pointer px-1.5 grid place-items-center transition-colors"
              Stroke.clear; "text-blue hover:text-ink"; focusRing ]

    /// The pane's body — whatever the selected tab shows. It takes the column's remaining
    /// height so the thing inside it scrolls rather than the column.
    let paneBody = "flex-1 min-h-0 flex flex-col"
    /// A read-only tab's body: the same scrolling region the block history uses, so a block
    /// read from the chat looks exactly like the block read in its terminal.
    let paneReadonly = "flex-1 min-h-0 overflow-y-auto flex flex-col gap-3 px-3 py-3"
    /// A stretch tab's facts, above whatever renders its recording.
    let paneFacts = "shrink-0 flex flex-col gap-1 px-3 py-3 " + Stroke.dividerBottom

    // --- The terminal list (Plan 20, stage 0) --------------------------------------------

    /// The list's scroll box. It takes the pane's whole body, because the list IS the body
    /// while it is showing — not a drawer over a terminal, which would leave two surfaces
    /// arguing about which one the reader is in.
    let terminalListBody = "flex-1 min-h-0 overflow-y-auto flex flex-col"

    /// One row: state, name, verbs. A grid rather than a flex row so the names line up down
    /// the list whatever their state marks are — a ragged left edge is what makes a list of
    /// twenty read as twenty unrelated things.
    let terminalListRow =
        "grid grid-cols-[auto_1fr_auto] items-center gap-2 px-3 py-2 " + Stroke.dividerBottom

    /// The row's own control: its name, which opens it. Ink at rest so the list reads as a
    /// list of names, blue under the pointer because that is what interactive means here —
    /// the same reasoning `recordLink` carries, at the list's size.
    let terminalListName =
        cls [ "bg-transparent cursor-pointer text-left w-full truncate p-0 font-ui text-body text-ink"
              "no-underline hover:text-blue transition-colors"; focusRing ]

    /// A closed row's name. The recording is still worth opening, and the row says which
    /// half of the list it is in by its tone rather than by repeating the word "closed" —
    /// the play mark beside it is what it IS.
    let terminalListNameClosed =
        cls [ "bg-transparent cursor-pointer text-left w-full truncate p-0 font-ui text-body text-ink-dim"
              "no-underline hover:text-blue transition-colors"; focusRing ]

    /// The row's verbs, kept on one baseline at its right edge.
    let terminalListVerbs = "flex items-center gap-1 shrink-0"

    /// The list's own empty state: the same idle prompt the empty pane wears, because a
    /// session with no terminals is one fact however you arrive at it.
    let terminalListEmpty = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center"

    /// The block history's scroll box, and the stream inside it.
    ///
    /// A terminal grows DOWNWARD from the top and the viewport rides the tail. Those are two
    /// different statements and only the second one is about the bottom: with a long history
    /// the newest line does sit at the bottom edge, because the scroller is pinned there
    /// (`keepSurfacesPinned`), not because the content is.
    ///
    /// `mt-auto` on the stream said the first statement as if it were the second, and with a
    /// short history it showed: two lines pinned to the floor under 500px of void, measured on
    /// a phone. No terminal has ever looked like that. `relative` so the way back to the live
    /// edge can float over this box rather than take a band from it.
    let terminalScrollback = "relative flex-1 min-h-0 overflow-y-auto flex flex-col px-3 py-3"
    let terminalStream = "flex flex-col gap-2"

    /// The player's own mount, and any other read-only region that shows a recording rather
    /// than a run of blocks: the same box, without the bottom anchoring (a player is one
    /// child and belongs at the top of its region).
    let terminalBlocks = "flex-1 min-h-0 overflow-y-auto flex flex-col gap-3 px-3 py-3"

    /// One block: the command that ran, then everything it printed.
    ///
    /// It is not a card. A card per command turned a scrollback into a stack of boxes —
    /// borders, fills and 12px of padding repeated around every two lines of mono, which is
    /// the one thing a terminal does not look like. What separates two blocks is what
    /// separates them in a terminal: a green prompt glyph, the command in ink, its output
    /// dim beneath, and a line of air.
    let terminalBlock = "flex flex-col"
    /// The command line as it was run — and the block's disclosure: pressing it opens the
    /// facts (who ran it, who let it, how it ended) that used to be printed beside every
    /// command whether anyone wanted them or not. A real `<summary>`, so the disclosure is
    /// the browser's and arrives keyboard-operable and correctly announced.
    let terminalBlockCommand = "flex items-baseline gap-2"
    let terminalBlockSummary =
        cls [ "group"; terminalBlockCommand; "cursor-pointer list-none"
              "hover:text-ink transition-colors duration-150 ease-out"; focusRing ]
    /// The mark at the end of the line: an ellipsis, because what it hides is the rest of
    /// the sentence. `group-open:` turns it while the facts are showing.
    let terminalBlockMark =
        "ml-auto shrink-0 font-terminal text-code-sm text-ink-faint select-none "
        + "group-hover:text-ink transition-colors duration-150 ease-out motion-reduce:transition-none"
    /// The facts themselves, on the output's column so they read as an aside to the command
    /// rather than as more output.
    let terminalBlockFacts = "flex flex-wrap items-baseline gap-x-4 gap-y-0.5 pl-4 py-1"
    let terminalBlockFact = caps + " text-ink-faint"

    let terminalPrompt = "shrink-0 font-terminal text-code text-green select-none"
    let terminalCommandText = "font-terminal text-code text-ink break-all"
    /// Output: preformatted, wrapping, and horizontally scrollable for the lines that will
    /// not wrap — the column must never make the PAGE scroll sideways. No padding of its
    /// own: it sits directly under its command, on the scrollback's own gutter, the way a
    /// terminal prints.
    let terminalOutput = "overflow-x-auto font-terminal text-code-sm leading-4 whitespace-pre-wrap break-words text-ink-dim"
    let terminalOutputEmpty = small
    /// The truncation notice: a stated gap in the record, in the error voice because a
    /// missing audit trail is not a neutral fact.
    let terminalTruncated = caps + " shrink-0 px-3 py-2 text-err"

    /// The live screen (Plan 14, stage 6). Monospaced, preformatted, and scrollable in both
    /// axes — a terminal's lines are as wide as the program made them, and wrapping them
    /// would redraw a screen the program laid out. The focus ring matters more here than
    /// anywhere: this is the one surface whose whole purpose is having the keyboard.
    let terminalScreen =
        cls [ "flex-1 min-h-0 overflow-auto px-3 py-2 font-terminal text-code-sm leading-4"
              "whitespace-pre text-ink bg-bg"; focusRing ]

    /// The DVR, which is TWO acts that were wearing one button in one band.
    ///
    /// Going back and coming back are opposites with opposite lifetimes, and once separated
    /// each has an obvious home — neither of them a band of its own.
    ///
    /// Going back is a DESTINATION, and you reach a destination by scrolling to it. So the way
    /// in sits at the top of the scrollback, in the content, scrolling with it, exactly where
    /// the history you have runs out: earlier is up. It costs no permanent chrome at all, and
    /// it is there only when something is actually recorded.
    let terminalReplayFrom =
        cls [ "self-start flex items-center gap-2 bg-transparent border-0 cursor-pointer px-0 py-1"
              "font-terminal text-code-sm text-ink-faint hover:text-ink transition-colors"; focusRing ]

    /// Coming back is TRANSIENT — it exists only while you are behind the live edge — and it
    /// is about where you are in the scroll, so it floats over the scroller. The same slot
    /// every chat client puts "jump to latest" in, for the same reason.
    let terminalLiveFloat =
        cls [ "absolute right-3 bottom-3 z-10 flex items-center gap-3 px-3 py-2 bg-surface"
              Stroke.ring; Stroke.rim ]
    /// The rewound read is a player, not a scroller, so it has no scroll box of its own to
    /// float over — this is the positioned region the way back hangs in.
    let terminalReplayRegion = "relative flex-1 min-h-0 flex flex-col"

    /// The command band beneath the blocks: the command line, whatever is queued against this
    /// terminal, and nothing else. The approval control that used to head it is a property of
    /// the terminal and now says so from the bar.
    ///
    /// The message composer's band exactly — same tone, same top rule, same gradient on focus
    /// — because typing a command here and typing a message there are the same act, and the
    /// pane was built out of the composer's parts for that reason. It carries no gutter of its
    /// own: what is in it runs edge to edge, and the rows that are not the command line bring
    /// their own padding (`terminalBandRow`).
    let terminalComposer =
        "group relative shrink-0 flex flex-col bg-surface focus-within:bg-surface-2 transition-colors"

    /// A row in the band that is not the command line — the lease bar, the "not marking"
    /// notice. They used to inherit the section's padding; the band has none.
    let terminalBandRow = "flex items-center gap-2 px-3 py-2"

    /// The command line: the row IS the field.
    ///
    /// It used to share its row with a `$` glyph and a Run button, and its column with two
    /// more rows above — leaving the one thing you type into the smallest thing in the pane
    /// (measured 273px of a 390px phone), narrow enough that a real command scrolled inside it
    /// while being typed.
    ///
    /// The `$` is now the PLACEHOLDER. That is not a trick to save an element: it puts the
    /// glyph exactly at the text origin, so it marks where the command will start and is
    /// replaced by the first character rather than sitting beside a box the text then begins
    /// to the right of. The old placeholder said "a command to run here", which a `$` says
    /// shorter; the field keeps its `aria-label`, because a placeholder is not a name.
    let terminalCommandWrap = "relative w-full"
    /// `py-3` is not a guess: 12 + 16 + 12 is exactly the 40px the message composer's band
    /// stands at (`py-2` around a 24px line), and the two sit side by side on a desktop where
    /// four pixels of disagreement between them reads as one of the columns being wrong.
    let terminalCommand =
        cls [ "w-full"; fieldBare
              "font-terminal text-code text-ink px-3 py-3 pr-10 placeholder:text-green"; touchType ]
    /// What sits at the field's trailing edge, inside its border: whoever else has a caret in
    /// this slot, and the verb.
    let terminalCommandTrail = "absolute right-1 inset-y-0 flex items-center gap-1"

    // Run itself is `btnSendInField` / `btnSendInFieldWaiting` — the message composer's Send,
    // unchanged. Queueing a command and sending a message are the same act, which is why the
    // terminal composer was built from the message composer's parts in the first place; they
    // should not have two different verbs at two different weights. Which of the two faces it
    // wears comes from the MODEL (a published slot, the same fact the send path acts on),
    // never from a second measurement of the field.
    /// A queued command awaiting its turn (or its approval) — a listed row, so its leading
    /// edge carries its state: green ready, blue waiting on someone.
    let private terminalQueued = cls [ "flex-col gap-1 px-3 py-2"; rowBase ]
    let terminalQueuedReady = cls [ terminalQueued; Stroke.green ]
    let terminalQueuedAwaiting = cls [ terminalQueued; Stroke.blue ]
    let terminalQueuedRow = "flex items-center gap-2"
    /// Someone else's composer slot in this terminal: shown, not editable-by-mistake — it is
    /// the same live text, so it is the terminal's version of watching a draft being written.
    /// Its leading edge is the author's own colour, set inline.
    let terminalPeerDraft = cls [ "items-center gap-2 px-3 py-2"; rowBase ]
    /// Who is in a slot right now, by live caret — one dot per peer, coloured by peer.
    let terminalEditors = "shrink-0 flex items-center gap-1"

    /// The empty state, when no terminal is open.
    let terminalEmpty = "flex-1 flex flex-col items-center justify-center gap-3 px-6 text-center"

    /// Reopens the column once it is shut — the mirror of the sidebar's reopen chevron,
    /// leaning the way the column travels. Rendered from the model rather than hidden by a
    /// variant: whether the control exists is a fact about the model, and a button that is
    /// merely invisible is still in the tab order.
    let terminalReopen = navChevronBack + " shrink-0"

    // --- ANSI styling ---------------------------------------------------------------------------
    // Turning a parsed `AnsiStyle` into what a span wears. Split in two on purpose:
    //
    //   * the SIXTEEN named colours are theme tokens, emitted as literal utility class names.
    //     Literal because Tailwind scans this source for the classes it must generate — a
    //     `sprintf "text-term-%s"` composes a class that is never built, and the span would
    //     come out unstyled. (The same trap the avatar checkers hit; see `@source inline`.)
    //   * the other 240 (the 6x6x6 cube, the grey ramp) and any 24-bit colour are ARITHMETIC,
    //     resolved by `Ansi.rgbOf` and emitted inline. There is no token to name them with,
    //     and 240 generated utilities to cover a case a build log might use once is not a
    //     trade worth making.

    let private ansiFgClass (colour: AnsiColour) : string =
        match colour with
        | IndexedColour 0 -> "text-term-black"
        | IndexedColour 1 -> "text-term-red"
        | IndexedColour 2 -> "text-term-green"
        | IndexedColour 3 -> "text-term-yellow"
        | IndexedColour 4 -> "text-term-blue"
        | IndexedColour 5 -> "text-term-magenta"
        | IndexedColour 6 -> "text-term-cyan"
        | IndexedColour 7 -> "text-term-white"
        | IndexedColour 8 -> "text-term-black-bright"
        | IndexedColour 9 -> "text-term-red-bright"
        | IndexedColour 10 -> "text-term-green-bright"
        | IndexedColour 11 -> "text-term-yellow-bright"
        | IndexedColour 12 -> "text-term-blue-bright"
        | IndexedColour 13 -> "text-term-magenta-bright"
        | IndexedColour 14 -> "text-term-cyan-bright"
        | IndexedColour 15 -> "text-term-white-bright"
        | _ -> ""

    let private ansiBgClass (colour: AnsiColour) : string =
        match colour with
        | IndexedColour 0 -> "bg-term-black"
        | IndexedColour 1 -> "bg-term-red"
        | IndexedColour 2 -> "bg-term-green"
        | IndexedColour 3 -> "bg-term-yellow"
        | IndexedColour 4 -> "bg-term-blue"
        | IndexedColour 5 -> "bg-term-magenta"
        | IndexedColour 6 -> "bg-term-cyan"
        | IndexedColour 7 -> "bg-term-white"
        | IndexedColour 8 -> "bg-term-black-bright"
        | IndexedColour 9 -> "bg-term-red-bright"
        | IndexedColour 10 -> "bg-term-green-bright"
        | IndexedColour 11 -> "bg-term-yellow-bright"
        | IndexedColour 12 -> "bg-term-blue-bright"
        | IndexedColour 13 -> "bg-term-magenta-bright"
        | IndexedColour 14 -> "bg-term-cyan-bright"
        | IndexedColour 15 -> "bg-term-white-bright"
        | _ -> ""

    /// A span's colours, resolved with `Inverse` already applied — the flag is a render-time
    /// swap, which is exactly why the parser leaves it as a flag rather than pre-swapping
    /// colours it does not know the defaults for. A background fill also forces the
    /// foreground to the page ground, so inverted text stays readable when only one side of
    /// the pair was ever set.
    let private ansiColours (style: AnsiStyle) : AnsiColour * AnsiColour =
        if style.Inverse then
            (match style.Background with DefaultColour -> IndexedColour 0 | c -> c),
            (match style.Foreground with DefaultColour -> IndexedColour 7 | c -> c)
        else style.Foreground, style.Background

    /// The utility classes for a styled run.
    let ansiClasses (style: AnsiStyle) : string =
        let foreground, background = ansiColours style
        [ if style.Bold then "font-semibold"
          // Dim is opacity, not a colour: it has to compose with whatever colour is set,
          // and it must not drop text below the contrast floor — 75% of a colour that
          // clears 4.5:1 on this ground still clears 3:1, and dim text is decoration.
          if style.Dim then "opacity-75"
          if style.Italic then "italic"
          if style.Underline then "underline"
          ansiFgClass foreground
          if background <> DefaultColour then "px-0.5"
          ansiBgClass background ]
        |> List.filter (fun c -> c <> "")
        |> String.concat " "

    /// The inline `style` for a run whose colour is arithmetic rather than named. Empty for
    /// every named colour, which is the common case.
    let ansiInline (style: AnsiStyle) : string =
        let foreground, background = ansiColours style
        let rgb (label: string) (colour: AnsiColour) =
            match AnsiColour.rgbOf colour with
            | Some (r, g, b) -> sprintf "%s:rgb(%d %d %d);" label r g b
            | None -> ""
        rgb "color" foreground + rgb "background-color" background

    // --- Document shell ------------------------------------------------------------------------

    /// Classes for the `#app` wrapper (served once in `View.page`; the browser only ever
    /// swaps its innerHTML, so these persist untouched across re-renders).
    ///
    /// `h-dvh`, never `h-screen`. `100vh` is the LARGE viewport — the height the page WOULD
    /// have with the browser's toolbars hidden — so on a phone showing its toolbars the shell
    /// is ~140px taller than anything that can be seen, and the whole document pans inside the
    /// visible area. Every symptom of that is a chrome one: the header slides up under the
    /// address bar and loses the top of the title, the composer sits behind the bottom bar,
    /// and the timeline is cut by an edge that is off screen (photographed on iOS Safari;
    /// desktop never shows it, because a desktop's viewport does not move).
    ///
    /// `100dvh` is what is visible NOW, so the shell fills exactly that and nothing pans —
    /// which is what the layout below already assumes, every region being sized off this one
    /// height. On a viewport that never changes (every desktop, and the test harnesses) the
    /// two units are the same number.
    let app = "flex h-dvh overflow-hidden bg-bg text-ink font-ui antialiased"

    /// Tailwind, built locally into a stylesheet and served by both the Session Process and
    /// the Manager UI — never a CDN (local first). The utilities and the theme tokens come
    /// from the CLI build over `app/tailwind.css`, whose `@source` rules scan the F# sources
    /// for the composed class names.
    ///
    /// Takes the URL rather than building it: the stylesheet is addressed by a digest of its
    /// own bytes, which only the serving process (having read them) can know.
    let headTags (styleSheetUrl: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\">" styleSheetUrl

    /// A stylesheet the page may never need: linked, so its address is the server's to state
    /// and the browser may fetch it whenever it likes, but `media="not all"` so it matches
    /// nothing and cannot hold up first paint. Whoever needs it turns it on by flipping
    /// `media` — `Replay.mount` does, for the replay player's sheet.
    ///
    /// Only worth doing for a sheet that is BOTH sizeable and used by a minority of sessions.
    /// The app's own stylesheet is neither, and a page that deferred it would paint unstyled.
    let deferredHeadTags (styleSheetUrl: string) (hook: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\" media=\"not all\" %s>" styleSheetUrl hook
