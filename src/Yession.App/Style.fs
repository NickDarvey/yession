namespace Yession.App

/// The client's visual language, authored entirely in F# by composing Tailwind's own
/// utility classes into typed, named values. Tailwind supplies the utilities (delivered
/// as a script — the Play CDN — never a stylesheet); F# supplies the composition. No CSS
/// file exists anywhere and no CSS is written by hand: views compose these values with
/// `Style.cls`, and the theme (palette, fonts, keyframes) is registered from the
/// F#-emitted config object in `Style.headTags`.
///
/// The design is Metro / Zune (pre-Windows 8) worn by a Slack/Cursor workspace anatomy —
/// see docs/plans/02-metro-zune-styling.md. The rules that keep it coherent:
///
///   Type grid — everything sits on a 4px baseline rhythm:
///     label 11/16 · small 13/16 · body 15/24 · heading 28/32 · wordmark 32/36 (px).
///   The sidebar wordmark and the main header share one 88px band (items-end, common
///   bottom padding) so their baselines align across the hairline.
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

    // --- Typography (4px baseline rhythm) ----------------------------------------------

    let wordmark = "font-extralight text-[32px] leading-9 tracking-[-0.02em] text-ink"
    let heading = "font-extralight text-[28px] leading-8 tracking-[-0.01em] lowercase text-ink truncate"
    let body = "font-light text-[15px] leading-6 text-ink"
    let small = "font-light text-[13px] leading-4 text-ink-faint"
    let label = "font-semibold text-[11px] leading-4 tracking-[0.18em] uppercase text-ink-faint"
    let mono = "font-mono text-[12px] leading-4 text-ink"
    let monoOut = "font-mono text-[11px] leading-4 text-ink-faint whitespace-pre-wrap"

    // --- Statuses: text only — never filled, never boxed --------------------------------

    let private statusBase = "font-semibold text-[11px] leading-4 tracking-[0.14em] uppercase"
    let statusOk = statusBase + " text-green"
    let statusRun = statusBase + " text-blue"
    let statusErr = statusBase + " text-err"
    let statusFaint = statusBase + " text-ink-faint"
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

    let private btnBase =
        "bg-transparent cursor-pointer font-sans font-semibold text-[11px] leading-4 "
        + "tracking-[0.16em] uppercase px-3.5 py-[7px] transition-colors border "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let btn = btnBase + " border-[#2e2e2e] text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg"
    let btnPrimary = btnBase + " border-blue text-blue hover:text-[#7fd0f5] active:bg-blue active:text-bg"
    let btnDanger = btnBase + " border-[#2e2e2e] text-ink-dim hover:border-err hover:text-err active:bg-err active:text-bg"

    /// Square icon buttons — self-contained, NOT composed over `btn`: Tailwind emits `p-0`
    /// BEFORE `px-*`/`py-*` in the stylesheet, so "btn + p-0" kept the text button's padding
    /// and crushed the glyph into a corner of a lopsided box (measured live: 30×24, ×
    /// touching the bottom-right edge).
    let private btnIconBase =
        "bg-transparent cursor-pointer border p-0 grid place-items-center transition-colors "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let private btnIconNeutralFace = " border-[#2e2e2e] text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg"
    let private btnIconDangerFace = " border-[#2e2e2e] text-ink-dim hover:border-err hover:text-err active:bg-err active:text-bg"

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
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-[13px] leading-4 px-1 "
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
        + "font-extralight text-[19px] leading-6 tracking-[-0.01em] lowercase "
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
        "bg-[#0e1418] grid place-items-center after:content-[''] after:w-2 after:h-2 after:bg-blue"

    let agentAvatarSm =
        "bg-[#0e1418] grid place-items-center after:content-[''] after:w-1.5 after:h-1.5 after:bg-blue"

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
        "relative w-[280px] shrink-0 bg-panel h-full overflow-hidden z-40 border-r border-hair "
        + "md:transition-[width] md:duration-200 md:ease-out "
        + "md:[.nav-alt_&]:w-0 md:[.nav-alt_&]:border-r-0 "
        + "max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:w-[min(280px,84vw)] "
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
        "absolute inset-y-0 left-0 w-[280px] max-md:w-[min(280px,84vw)] flex flex-col px-6 pb-5 "
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

    /// The shared 88px header band: baselines align across the sidebar/main hairline.
    let sideHead = "h-[88px] shrink-0 flex items-end justify-between pb-5"
    let sideSection = "flex flex-col gap-2 py-4 border-t border-hair"
    let sideSectionFirst = "flex flex-col gap-2 pb-4"
    let sideRow = "flex items-baseline justify-between gap-2"
    let person = "flex items-center gap-2.5 font-light text-[13px] leading-5 text-ink-dim"
    let commandCard = "flex flex-col gap-1 px-3 py-2 bg-surface"

    let mainColumn = "flex-1 flex flex-col min-w-0 h-full"

    let header =
        "relative h-[88px] shrink-0 flex items-end gap-4 px-8 pb-5 border-b border-hair max-md:h-16 max-md:px-4 max-md:pb-3"

    /// Indent the heading one avatar column (20px + 12px gutter) so its left edge sits
    /// exactly on the message-text column below.
    let headerTitle = "ml-8"
    /// The header's right-hand group: sync status, and — only while the sidebar is off screen —
    /// the agent's absence.
    let headerAside = "ml-auto shrink-0 flex items-end gap-5 pb-1"
    let headerStatus = "shrink-0"

    /// The agent's absence, FOLLOWING the surface that normally says it: shown only when the
    /// sidebar column (which holds the real call to action) is collapsed or off-canvas — which
    /// on a phone is most of the time. Never both at once, so it is a relocation, not a repeat.
    /// Same visibility rule as `navReopen`, for the same reason.
    let headerNoAgent =
        "bg-transparent border-0 cursor-pointer font-semibold text-[11px] leading-4 "
        + "tracking-[0.14em] uppercase text-blue hover:text-[#7fd0f5] transition-colors "
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
    /// save button — the model is the collaborative `Title` text.
    let titleInput =
        "w-full min-w-0 bg-transparent border-0 border-b border-dotted border-ink-faint "
        + "focus:border-solid focus:border-blue outline-none px-0 py-0 "
        + "font-extralight text-[28px] leading-8 tracking-[-0.01em] lowercase text-ink "
        + "placeholder:text-ink-faint truncate"

    /// The session id, shown small and dim under the title as a stable secondary identifier.
    let titleId = "font-mono text-[11px] leading-4 text-ink-faint truncate mt-0.5"

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
        "absolute left-2 bottom-[18px] w-6 h-6 place-items-center hidden md:[.nav-alt_&]:grid "
        + "max-md:grid max-md:[.nav-alt_&]:hidden max-md:bottom-[10px]"

    // --- Timeline --------------------------------------------------------------------------

    let timeline =
        "flex-1 overflow-y-auto px-8 py-6 flex flex-col gap-6 max-md:px-4 max-md:py-4 max-md:gap-5"

    /// Message rhythm: one 16px meta line + 8px gap + n×24px body lines, on a
    /// `20px avatar · 12px gutter · content` grid.
    let message = "grid grid-cols-[20px_1fr] gap-x-3 gap-y-2 max-w-[46rem]"
    let messageAvatar = "row-span-2 -mt-0.5" // optical: square top ≈ cap height
    let messageMeta = "flex items-baseline gap-2.5"
    let who = "font-semibold text-[11px] leading-4 tracking-[0.16em] uppercase text-ink-dim"
    let whoAgent = "font-semibold text-[11px] leading-4 tracking-[0.16em] uppercase text-blue"
    let messageBody = "col-start-2 font-light text-[15px] leading-6 text-ink"
    let messageBodyStreaming = "col-start-2 font-light text-[15px] leading-6 text-ink-dim"
    let caret =
        "inline-block w-[7px] h-[15px] bg-blue align-[-2px] ml-0.5 animate-blink motion-reduce:animate-none"

    /// Read-only rendered Markdown in the timeline (the mirror of the composer's live
    /// formatting). Preflight strips heading/list defaults, so each rendered element carries
    /// its own utilities — the same "F# composes utilities, no hand CSS" rule as everything
    /// else. Blocks share a tight vertical rhythm; only the first/last drop their outer margin.
    let proseP = "[&:not(:first-child)]:mt-2"
    let proseH1 = "text-[19px] leading-7 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    let proseH2 = "text-[17px] leading-6 font-normal text-ink [&:not(:first-child)]:mt-3 mb-1"
    let proseH3 = "text-[15px] leading-6 font-semibold text-ink [&:not(:first-child)]:mt-2 mb-1"
    let proseH4 = "text-[13px] leading-5 font-semibold uppercase tracking-[0.08em] text-ink-dim [&:not(:first-child)]:mt-2 mb-1"
    let proseUl = "list-disc pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseOl = "list-decimal pl-5 [&:not(:first-child)]:mt-2 marker:text-ink-faint"
    let proseLi = "[&:not(:first-child)]:mt-1"
    let proseStrong = "font-semibold text-ink"
    let proseCode = "font-mono text-[13px] bg-surface-2 text-ink px-1 py-0.5"
    let prosePre = "font-mono text-[12px] leading-5 bg-surface-2 text-ink p-3 [&:not(:first-child)]:mt-2 overflow-x-auto whitespace-pre-wrap"
    let proseQuote = "border-l-2 border-hair pl-3 text-ink-dim [&:not(:first-child)]:mt-2"
    let proseLink = "text-blue underline decoration-1 underline-offset-2 hover:text-[#7fd0f5]"
    let proseHr = "border-0 border-t border-hair my-3"

    // --- Agent activity strip ----------------------------------------------------------------

    let activity =
        "h-12 shrink-0 flex items-center gap-3 px-8 border-t border-hair bg-panel max-md:px-4"

    let activityPulse = "w-2 h-2 bg-blue animate-pulse2 motion-reduce:animate-none"
    let activityText = "font-light text-[14px] leading-4 text-blue"
    let activityTurn = "text-[11px] leading-4 text-ink-faint tabular-nums max-md:hidden"

    // --- Queue: editable until drained; the green left edge encodes editability ---------------

    let queue = "shrink-0 flex flex-col gap-0.5 px-8 pt-4 max-md:px-4"
    let queueHead = "flex items-baseline gap-3 pb-2"
    let queueCount = "font-semibold text-[11px] leading-4 tracking-[0.18em] uppercase text-green"

    let queueItem =
        "group flex items-center gap-3 bg-surface h-10 px-3 border-l-2 border-hair "
        + "hover:border-green focus-within:border-green hover:bg-surface-2 focus-within:bg-surface-2 transition-colors"

    let queueInput =
        "flex-1 min-w-0 self-center h-5 bg-transparent border-0 outline-none resize-none "
        + "[&_.ProseMirror]:outline-none "
        + "font-sans font-light text-[13px] leading-5 text-ink-dim focus:text-ink"

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
        + "text-[15px] leading-6 text-ink placeholder:text-ink-faint px-4 pt-3 pb-1"

    let draftActions = "flex items-center gap-2 pl-4 pr-2 pb-2"

    /// Send sits at the TRAILING edge — where the eye ends the line it just wrote, and where
    /// every send button a person has ever used lives — with discard as its quiet neighbour.
    /// Everything that describes the draft (who is in it, whose it is) stays on the left.
    let draftCommit = "ml-auto flex items-center gap-2"
    let draftAuthor = "pr-1 font-semibold text-[10px] leading-4 tracking-[0.14em] uppercase text-ink-faint truncate"

    // A draft nobody has open here: one line of it, so the composer reads as "what is being
    // written" rather than a stack of boxes. Clicking it opens it (and closes whatever was).
    let draftSummary =
        "group w-full flex items-center gap-3 h-8 pl-4 pr-2 bg-surface/60 text-left border-l-2 "
        + "border-hair hover:border-blue hover:bg-surface transition-colors cursor-pointer"

    let draftSummaryName =
        "shrink-0 font-semibold text-[10px] leading-4 tracking-[0.14em] uppercase text-ink-faint"

    /// The clamped body: the same read-only editor as anywhere else, held to one line. `truncate`
    /// on the host would fight ProseMirror's block children, so the clamp is on its descendants.
    let draftSummaryBody =
        "flex-1 min-w-0 font-sans font-light text-[13px] leading-8 text-ink-dim "
        + "overflow-hidden whitespace-nowrap [&_*]:inline [&_*]:truncate [&_*]:m-0"

    /// Who is in this draft right now: one dot per live caret, coloured by peer (`PeerColour`).
    let draftEditors = "shrink-0 flex items-center gap-1 pr-1"
    let draftEditorDot = "inline-block w-1.5 h-1.5 rounded-full"

    /// Starts your own draft, collapsing whoever's is open — the escape hatch from joining.
    let draftNew =
        "self-end font-semibold text-[10px] leading-4 tracking-[0.14em] uppercase text-ink-faint "
        + "hover:text-blue transition-colors"

    // --- Settings ------------------------------------------------------------------------------
    // Settings is the column's other face, not a drawer over the conversation: you go there and
    // come back, and the thing you were reading never moves. Its open state is one bit on the
    // root <html> element (`settings-open`, toggled by [data-settings-toggle]).

    /// The settings face's header band: the same 88px rhythm as the nav and the main header, so
    /// the three baselines still align when the column changes face.
    let settingsHead = "h-[88px] shrink-0 flex items-end justify-between pb-5"
    let settingsTitle = "font-extralight text-[28px] leading-8 tracking-[-0.01em] lowercase text-ink"

    /// A settings field (input/select): a quiet Metro rectangle on the surface tone,
    /// border brightening to blue on focus — the body scale, never the title's.
    let field =
        "w-full bg-surface border border-hair focus:border-blue outline-none appearance-none "
        + "px-3 py-2 font-light text-[13px] leading-5 text-ink placeholder:text-ink-faint"

    // --- The agent's absence -------------------------------------------------------------------
    // Said ONCE, in the section that lists who is in this session, because that is where a
    // missing member is missing. It used to be said three times over (a sidebar row, a strip
    // above the composer, and the settings copy); repetition made it wallpaper, not a prompt.
    // Blue is the agent's voice, so the blue left edge is its absence — the same edge grammar
    // the queue uses for editability.

    let noAgentCard = "flex flex-col gap-2 bg-surface border-l-2 border-blue px-3 py-3 mt-1"
    /// Full-width so it reads as the section's one action, not an afterthought beside the text.
    let noAgentAction = "w-full text-center"

    // --- Document shell ------------------------------------------------------------------------

    /// Classes for the `#app` wrapper (served once in `View.page`; the browser only ever
    /// swaps its innerHTML, so these persist untouched across re-renders).
    let app = "flex h-screen overflow-hidden bg-bg text-ink font-sans antialiased"

    /// Tailwind, built locally into a stylesheet and served from the Session Process at
    /// `/app.css` — never a CDN (local first; the app works offline). The utilities and the
    /// theme (colours, fonts, keyframes) come from the CLI build configured in
    /// `tailwind.config.js`, which scans the F# sources for the composed class names.
    let headTags = sprintf "<link rel=\"stylesheet\" href=\"%s\">" (SessionRoute.relative AppCss)
