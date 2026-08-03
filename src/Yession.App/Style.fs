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
///   Colour — technocool: blue is interactive and the agent's voice; green is live/ok
///   and the human pulse. People are identified by tiny square display pics, not name
///   colours. The blue→green gradient appears exactly ONCE: the composer's focus edge.
module Style =

    /// Join utility groups into a class attribute value.
    let cls (groups: string list) : string = String.concat " " groups

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
    let label = caps + " text-ink-faint"
    let mono = "font-mono text-code text-ink"
    let monoOut = "font-mono text-code-sm text-ink-faint whitespace-pre-wrap"

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

    /// Sized by construction, not padding arithmetic: the box is `h-8` (the same 32px the
    /// composer's large icon button and the Send row share) with the line flex-centred in
    /// it — the old `py-[7px]` was that same 32px, hand-derived and easy to break.
    let private btnBase =
        "bg-transparent cursor-pointer font-sans " + caps + " "
        + "h-8 px-3.5 inline-flex items-center justify-center transition-colors border "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let btn = btnBase + " border-edge text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg"
    let btnPrimary = btnBase + " border-blue text-blue hover:text-blue-bright active:bg-blue active:text-bg"
    let btnDanger = btnBase + " border-edge text-ink-dim hover:border-err hover:text-err active:bg-err active:text-bg"

    /// Square icon buttons — self-contained, NOT composed over `btn`: Tailwind emits `p-0`
    /// BEFORE `px-*`/`py-*` in the stylesheet, so "btn + p-0" kept the text button's padding
    /// and crushed the glyph into a corner of a lopsided box (measured live: 30×24, ×
    /// touching the bottom-right edge).
    let private btnIconBase =
        "bg-transparent cursor-pointer border p-0 grid place-items-center transition-colors "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let private btnIconNeutralFace = " border-edge text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg"
    let private btnIconDangerFace = " border-edge text-ink-dim hover:border-err hover:text-err active:bg-err active:text-bg"

    /// 24px square: the queue's reorder controls.
    let btnIcon = btnIconBase + " w-6 h-6" + btnIconNeutralFace
    /// 24px square, destructive: delete / disconnect.
    let btnIconDanger = btnIconBase + " w-6 h-6" + btnIconDangerFace
    /// 32px square, destructive: the composer's discard — the same height as the Send
    /// button it sits beside, so the pair shares top and bottom edges.
    let btnIconDangerLg = btnIconBase + " w-8 h-8" + btnIconDangerFace
    /// Chrome, not an action: the small sidebar collapse/reveal chevrons. They lean the way
    /// they travel on hover and lead further on press — the only motion chrome earns, and the
    /// reason the two directions are separate values rather than one class plus a guess.
    let private navChevronBase =
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-small px-1 "
        + "flex items-center gap-1 transition-[translate,color] duration-150 ease-out "
        + "motion-reduce:transition-none "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let navChevronBack = navChevronBase + " hover:-translate-x-0.5 active:-translate-x-1"
    let navChevronForward = navChevronBase + " hover:translate-x-0.5 active:translate-x-1"

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
        + "motion-reduce:transition-none "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-4"

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
        "relative w-side shrink-0 bg-panel h-full overflow-hidden z-40 border-r border-hair "
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
    let sideSection = "flex flex-col gap-2 py-4 border-t border-hair"
    let sideSectionFirst = "flex flex-col gap-2 pb-4"
    let sideRow = "flex items-baseline justify-between gap-2"
    /// A roster row aligns on the TEXT BASELINE, so the 11px caps ("you", a status) sit on
    /// the 13px name's baseline instead of floating box-centred beside it.
    let person = "flex items-baseline gap-2.5 font-light text-small leading-5 text-ink-dim"
    /// The avatar opts back out: a box has no baseline (it would park its bottom edge on
    /// the line), so it centres in the row the way it always did.
    let personAvatar = "self-center"
    let commandCard = "flex flex-col gap-1 px-3 py-2 bg-surface"

    let mainColumn = "flex-1 flex flex-col min-w-0 h-full"

    let header =
        "relative h-band shrink-0 flex items-end gap-4 px-8 pb-5 border-b border-hair max-md:h-16 max-md:px-4 max-md:pb-3"

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
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2 "
        + "hidden md:[.nav-alt_&]:block max-md:block max-md:[.nav-alt_&]:hidden"

    /// The degradation strip between the header and the timeline: a hairline notice, never a
    /// modal and never a blocker — the client below it stays fully usable.
    let degradedBanner =
        "shrink-0 flex items-baseline gap-3 px-8 py-2 border-b border-hair bg-surface max-md:px-4"

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
        "w-full min-w-0 bg-transparent border-0 border-b border-dotted border-ink-faint "
        + "focus:border-solid focus:border-blue outline-none px-0 py-0 "
        + "font-extralight text-heading tracking-[-0.01em] lowercase text-ink "
        + "placeholder:text-ink-faint truncate relative md:top-[2px]"

    /// The session id, shown small and dim under the title as a stable secondary identifier.
    /// On md+ it hangs OUT OF FLOW below the title, into the header band's bottom padding:
    /// in flow it added 18px under the title inside the bottom-aligned stack and lifted the
    /// title's baseline that far off the wordmark's (measured 41.5 vs 61 at 1440). On mobile
    /// the sidebar is off-canvas (no baseline to meet) and the band is only 64px, so the id
    /// stays in flow there.
    let titleId =
        "font-mono text-code-sm text-ink-faint truncate mt-0.5 "
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
    let proseCode = "font-mono text-[13px] bg-surface-2 text-ink px-1 py-0.5"
    let prosePre = "font-mono text-code leading-5 bg-surface-2 text-ink p-3 [&:not(:first-child)]:mt-2 overflow-x-auto whitespace-pre-wrap"
    let proseQuote = "border-l-2 border-hair pl-3 text-ink-dim [&:not(:first-child)]:mt-2"
    let proseLink = "text-blue underline decoration-1 underline-offset-2 hover:text-blue-bright"
    let proseHr = "border-0 border-t border-hair my-3"

    // --- Agent activity strip ----------------------------------------------------------------

    let activity =
        "h-12 shrink-0 flex items-center gap-3 px-8 border-t border-hair bg-panel max-md:px-4"

    let activityPulse = "w-2 h-2 bg-blue animate-pulse2 motion-reduce:animate-none"
    let activityText = "font-light text-small text-blue"
    let activityTurn = "text-label text-ink-faint tabular-nums max-md:hidden"

    // --- Queue: editable until drained; the green left edge encodes editability ---------------

    let queue = "shrink-0 flex flex-col gap-0.5 px-8 pt-4 max-md:px-4"
    let queueHead = "flex items-baseline gap-3 pb-2"
    let queueCount = caps + " text-green"

    let queueItem =
        "group flex items-center gap-3 bg-surface h-10 px-3 border-l-2 border-hair "
        + "hover:border-green focus-within:border-green hover:bg-surface-2 focus-within:bg-surface-2 transition-colors"

    let queueInput =
        "flex-1 min-w-0 self-center h-5 bg-transparent border-0 outline-none resize-none "
        + "[&_.ProseMirror]:outline-none "
        + "font-sans font-light text-small leading-5 text-ink-dim focus:text-ink"

    let queueTools =
        "flex gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 max-md:opacity-100 transition-opacity"

    // --- Composer: the one gradient in the product lives on its focus edge --------------------

    let composer = "shrink-0 flex flex-col gap-3 px-8 pt-4 pb-6 max-md:px-4 max-md:pb-4"
    /// The box lifts a tone while anything inside it has focus, so "this is where I am typing"
    /// is legible at a glance and not only from the 2px edge below.
    let draftBox = "group relative bg-surface focus-within:bg-surface-2 transition-colors"

    /// The focus edge: grows top-to-bottom in the blue→green gradient on focus —
    /// Zune's orange→pink signature, recast, spent exactly once.
    let draftEdge =
        "absolute left-0 inset-y-0 w-0.5 bg-grad scale-y-0 origin-top transition-transform "
        + "duration-300 ease-out group-focus-within:scale-y-100 motion-reduce:transition-none"

    /// `[&_.ProseMirror]:outline-none` is not cosmetic tidying: the editor mounts its own
    /// `contenteditable` INSIDE this host, so `outline-none` here never reached it and the
    /// browser drew its default focus box around the composer — a white rectangle across the
    /// whole width, in a design with no rectangles. The focus signal is the gradient edge and
    /// the box's lift (`draftBox`), both of which stay.
    let draftInput =
        "block w-full bg-transparent border-0 outline-none resize-none font-sans font-light "
        + "[&_.ProseMirror]:outline-none "
        + "text-body text-ink placeholder:text-ink-faint px-4 pt-3 pb-1"

    let draftActions = "flex items-center gap-2 pl-4 pr-2 pb-2"

    /// Send sits at the TRAILING edge — where the eye ends the line it just wrote, and where
    /// every send button a person has ever used lives — with discard as its quiet neighbour.
    /// Everything that describes the draft (who is in it, whose it is) stays on the left.
    let draftCommit = "ml-auto flex items-center gap-2"
    let draftAuthor = "pr-1 " + caps + " text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    let draftSummary =
        "group w-full flex items-center gap-3 h-8 pl-4 pr-2 bg-surface/60 text-left border-l-2 "
        + "border-hair hover:border-blue hover:bg-surface transition-colors cursor-pointer"

    let draftSummaryName = "shrink-0 " + caps + " text-ink-faint"

    /// The clamped body: the same read-only editor as anywhere else, held to one line. `truncate`
    /// on the host would fight ProseMirror's block children, so the clamp is on its descendants.
    let draftSummaryBody =
        "flex-1 min-w-0 font-sans font-light text-small leading-8 text-ink-dim "
        + "overflow-hidden whitespace-nowrap [&_*]:inline [&_*]:truncate [&_*]:m-0"

    /// Who is in this draft right now: one dot per live caret, coloured by peer (`PeerColour`).
    let draftEditors = "shrink-0 flex items-center gap-1 pr-1"
    let draftEditorDot = "inline-block w-1.5 h-1.5 rounded-full"

    /// Starts your own draft, collapsing whoever's is open — the escape hatch from joining.
    let draftNew = "self-end " + caps + " text-ink-faint hover:text-blue transition-colors"

    // --- Settings ------------------------------------------------------------------------------
    // Settings is the column's other face, not a drawer over the conversation: you go there and
    // come back, and the thing you were reading never moves. Its open state is one bit on the
    // root <html> element (`settings-open`, toggled by [data-settings-toggle]).

    /// The settings face's header band: the same 88px rhythm as the nav and the main header, so
    /// the three baselines still align when the column changes face.
    let settingsHead = "h-band shrink-0 flex items-end justify-between pb-5"
    let settingsTitle = "font-extralight text-heading tracking-[-0.01em] lowercase text-ink"

    /// A settings field (input/select): a quiet Metro rectangle on the surface tone,
    /// border brightening to blue on focus — the body scale, never the title's.
    let field =
        "w-full bg-surface border border-hair focus:border-blue outline-none appearance-none "
        + "px-3 py-2 font-light text-small leading-5 text-ink placeholder:text-ink-faint"

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

    /// The terminals column. Mirrors `sidebar`'s geometry (a fixed column on desktop that
    /// animates shut, an off-canvas drawer on mobile) reflected to the right edge.
    let terminalPanel =
        "relative w-term shrink-0 bg-panel h-full overflow-hidden z-40 border-l border-hair flex flex-col "
        + "md:transition-[width] md:duration-200 md:ease-out "
        + "md:[.term-closed_&]:w-0 md:[.term-closed_&]:border-l-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:right-0 max-md:w-[min(var(--spacing-term),92vw)] "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out "
        + "max-md:[.term-closed_&]:translate-x-[101%] motion-reduce:transition-none"

    /// Held at the column's full width so nothing reflows while the column animates shut.
    let terminalPane = "absolute inset-0 w-term max-md:w-[min(var(--spacing-term),92vw)] flex flex-col"

    /// The column's head, on the same 88px band as the sidebar and the main header.
    let terminalHead = "h-band shrink-0 flex items-end justify-between gap-2 px-5 pb-5 border-b border-hair"

    /// The open-terminal strip: one chip per terminal, scrolling horizontally when there are
    /// more than fit rather than wrapping into a second band that shifts the whole column.
    let terminalTabs = "shrink-0 flex items-stretch gap-1 px-3 py-2 overflow-x-auto border-b border-hair"
    let private tabBase =
        caps + " px-2.5 py-1.5 max-w-40 truncate border transition-colors focus-visible:outline-2 focus-visible:outline-blue"
    let terminalTab = tabBase + " border-transparent text-ink-faint hover:text-ink"
    let terminalTabActive = tabBase + " border-blue text-blue"
    /// Adds a terminal. The one action in the strip that is not a selection.
    let terminalTabNew = tabBase + " border-edge text-ink-dim hover:border-ink hover:text-ink"

    /// The scrolling block history.
    let terminalBlocks = "flex-1 min-h-0 overflow-y-auto flex flex-col gap-3 px-3 py-3"

    /// One block: the command that ran, then its output.
    let terminalBlock = "flex flex-col bg-surface"
    /// The command line as it was run — mono, and marked with a prompt glyph so a command
    /// is never mistaken for the output above it.
    let terminalBlockCommand = "flex items-baseline gap-2 px-3 py-2 border-b border-hair"
    let terminalPrompt = "shrink-0 font-mono text-code text-green select-none"
    let terminalCommandText = "font-mono text-code text-ink break-all"
    /// Output: preformatted, wrapping, and horizontally scrollable for the lines that will
    /// not wrap — the column must never make the PAGE scroll sideways.
    let terminalOutput = "px-3 py-2 overflow-x-auto font-mono text-code-sm leading-4 whitespace-pre-wrap break-words text-ink-dim"
    let terminalOutputEmpty = "px-3 py-2 " + small
    /// The truncation notice: a stated gap in the record, in the error voice because a
    /// missing audit trail is not a neutral fact.
    let terminalTruncated = caps + " px-3 py-2 text-err"

    /// The composer area beneath the blocks.
    let terminalComposer = "shrink-0 flex flex-col gap-2 px-3 py-3 border-t border-hair"
    /// A queued command awaiting its turn (or its approval).
    let terminalQueued = "flex flex-col gap-1 px-3 py-2 bg-surface border-l-2"
    let terminalQueuedReady = terminalQueued + " border-l-green"
    let terminalQueuedAwaiting = terminalQueued + " border-l-blue"
    let terminalQueuedRow = "flex items-center gap-2"
    /// The command input itself: mono, on the surface tone, blue focus edge like every
    /// other field — a terminal composer is a field, not a terminal.
    let terminalInput =
        "flex-1 min-w-0 bg-surface border border-hair focus:border-blue outline-none "
        + "px-3 py-2 font-mono text-code text-ink placeholder:text-ink-faint"
    /// Someone else's composer slot in this terminal: shown, not editable-by-mistake — it is
    /// the same live text, so it is the terminal's version of watching a draft being written.
    let terminalPeerDraft = "flex items-center gap-2 px-3 py-2 bg-surface border-l-2"
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
    let app = "flex h-screen overflow-hidden bg-bg text-ink font-sans antialiased"

    /// Tailwind, built locally into a stylesheet and served by both the Session Process and
    /// the Manager UI — never a CDN (local first). The utilities and the theme tokens come
    /// from the CLI build over `app/tailwind.css`, whose `@source` rules scan the F# sources
    /// for the composed class names.
    ///
    /// Takes the URL rather than building it: the stylesheet is addressed by a digest of its
    /// own bytes, which only the serving process (having read them) can know.
    let headTags (styleSheetUrl: string) =
        sprintf "<link rel=\"stylesheet\" href=\"%s\">" styleSheetUrl
