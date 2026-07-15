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

    // --- Buttons: bordered Metro rectangles — hover brightens, press fills --------------

    let private btnBase =
        "bg-transparent cursor-pointer font-sans font-semibold text-[11px] leading-4 "
        + "tracking-[0.16em] uppercase px-3.5 py-[7px] transition-colors border "
        + "focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue focus-visible:outline-offset-2"

    let btn = btnBase + " border-[#2e2e2e] text-ink-dim hover:border-ink hover:text-ink active:bg-ink active:text-bg"
    let btnPrimary = btnBase + " border-blue text-blue hover:text-[#7fd0f5] active:bg-blue active:text-bg"
    let btnDanger = btnBase + " border-[#2e2e2e] text-ink-dim hover:border-err hover:text-err active:bg-err active:text-bg"
    /// 24px square icon button (compose after `btn`/`btnDanger` to override the padding).
    let btnIcon = "w-6 h-6 p-0 grid place-items-center tracking-normal"
    /// Chrome, not an action: the subtle sidebar collapse/expand chevrons.
    let navChevron =
        "bg-transparent border-0 cursor-pointer text-ink-faint hover:text-ink text-[13px] leading-4 px-1 transition-colors"

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
    // The sidebar/drawer state is one bit: the root <html> element's `nav-alt` class
    // (toggled by [data-nav-toggle] in the shell). Default = sidebar visible on desktop,
    // off-canvas on mobile; `nav-alt` = the inverse. Expressed with arbitrary variants so
    // it stays plain Tailwind.

    let sidebar =
        "w-[280px] shrink-0 bg-panel h-full flex flex-col px-6 pb-5 border-r border-hair overflow-y-auto z-40 "
        + "md:[.nav-alt_&]:hidden "
        + "max-md:fixed max-md:inset-y-0 max-md:left-0 max-md:w-[min(280px,84vw)] "
        + "max-md:transition-transform max-md:duration-200 max-md:ease-out max-md:-translate-x-[101%] "
        + "max-md:[.nav-alt_&]:translate-x-0 motion-reduce:transition-none"

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
    let headerStatus = "ml-auto shrink-0 pb-1"

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
        + "font-sans font-light text-[13px] leading-5 text-ink-dim focus:text-ink"

    let queueTools =
        "flex gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 max-md:opacity-100 transition-opacity"

    // --- Composer: the one gradient in the product lives on its focus edge --------------------

    let composer = "shrink-0 flex flex-col gap-3 px-8 pt-4 pb-6 max-md:px-4 max-md:pb-4"
    let draftBox = "group relative bg-surface"

    /// The focus edge: grows top-to-bottom in the blue→green gradient on focus —
    /// Zune's orange→pink signature, recast, spent exactly once.
    let draftEdge =
        "absolute left-0 inset-y-0 w-0.5 bg-grad scale-y-0 origin-top transition-transform "
        + "duration-300 ease-out group-focus-within:scale-y-100 motion-reduce:transition-none"

    let draftInput =
        "block w-full bg-transparent border-0 outline-none resize-none font-sans font-light "
        + "text-[15px] leading-6 text-ink placeholder:text-ink-faint px-4 pt-3 pb-1"

    let draftActions = "flex items-center gap-2 pl-4 pr-2 pb-2"
    let draftAuthor = "ml-auto pr-2 font-semibold text-[10px] leading-4 tracking-[0.14em] uppercase text-ink-faint"

    // --- Document shell ------------------------------------------------------------------------

    /// Classes for the `#app` wrapper (served once in `View.page`; the browser only ever
    /// swaps its innerHTML, so these persist untouched across re-renders).
    let app = "flex h-screen overflow-hidden bg-bg text-ink font-sans antialiased"

    /// Tailwind, built locally into a stylesheet and served from the Session Process at
    /// `/app.css` — never a CDN (local first; the app works offline). The utilities and the
    /// theme (colours, fonts, keyframes) come from the CLI build configured in
    /// `tailwind.config.js`, which scans the F# sources for the composed class names.
    let headTags = "<link rel=\"stylesheet\" href=\"/app.css\">"
