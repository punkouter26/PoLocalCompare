namespace PoLocalCompare.Shared.Prompts;

/// <summary>One curated starter prompt.</summary>
/// <param name="Id">Stable slug — used as the demo-mode seed key and the UI element key.</param>
/// <param name="Emoji">Decorative only; every rendering must mark it <c>aria-hidden</c>.</param>
/// <param name="SelfRunning">
/// True when the output animates or plays by itself. Demo mode only picks these — an unattended
/// screen showing a form nobody types into demonstrates nothing.
/// </param>
public sealed record PromptTemplate(
    string Id,
    string Title,
    string Category,
    string Emoji,
    string Text,
    bool SelfRunning);

/// <summary>
/// The curated prompt set behind the Home prompt picker and demo mode.
/// </summary>
/// <remarks>
/// Every prompt asks for a single self-contained HTML file, because that is what
/// <c>SandboxedViewport</c> can render: the iframe is <c>sandbox="allow-scripts"</c> with no
/// <c>allow-same-origin</c>, so anything split across files or fetched same-origin renders blank
/// and the model looks worse than it is. Prompts also stay clear of network calls for the same
/// reason — a duel should compare the models, not the tester's firewall.
/// </remarks>
public static class PromptLibrary
{
    public const string CategoryGraphics = "Graphics";
    public const string CategoryGames = "Games";
    public const string CategoryDataViz = "Data viz";
    public const string CategoryTools = "Tools";
    public const string CategoryInterface = "Interface";
    public const string CategorySimulation = "Simulation";

    public static IReadOnlyList<PromptTemplate> All { get; } =
    [
        new("spinning-cube", "Spinning 3D cube", CategoryGraphics, "🧊",
            "Build a self-contained single HTML file showing a 3D cube rotating continuously on two axes. " +
            "Use CSS 3D transforms — no external libraries. Give each face a different colour, add soft " +
            "perspective and a subtle drop shadow, and centre it on a dark gradient background.",
            SelfRunning: true),

        new("particle-field", "Particle constellation", CategoryGraphics, "✨",
            "Build a self-contained single HTML file with a full-screen canvas animation: 120 particles " +
            "drifting slowly, with a thin line drawn between any two that come within 120px of each other. " +
            "Lines fade with distance. Dark background, cyan particles, smooth 60fps requestAnimationFrame loop.",
            SelfRunning: true),

        new("tic-tac-toe-auto", "Self-playing tic-tac-toe", CategoryGames, "⭕",
            "Build a self-contained single HTML file with a tic-tac-toe game that plays itself: two AI " +
            "players alternate moves every 700ms using a simple win/block heuristic, the board animates each " +
            "placement, and when the game ends it shows the result then resets after 2 seconds. Add a running " +
            "score of X wins, O wins and draws.",
            SelfRunning: true),

        new("game-of-life", "Conway's Game of Life", CategorySimulation, "🦠",
            "Build a self-contained single HTML file running Conway's Game of Life on a 50x50 grid. Seed it " +
            "randomly, step every 120ms, and wrap at the edges. Render on a canvas with living cells in a warm " +
            "colour that fades as a cell ages. Show the generation count and population.",
            SelfRunning: true),

        new("bouncing-balls", "Bouncing ball physics", CategorySimulation, "🎾",
            "Build a self-contained single HTML file with 25 balls of random size and colour bouncing inside " +
            "the viewport with gravity, damping on collision with the walls, and ball-to-ball collision. " +
            "Canvas-based, smooth animation, dark background.",
            SelfRunning: true),

        new("digital-clock", "Animated digital clock", CategoryInterface, "🕐",
            "Build a self-contained single HTML file showing a large digital clock with the current time, " +
            "updating every second. Each digit flips with a smooth CSS animation when it changes. Include the " +
            "date underneath, use a monospace display face, and centre everything on a gradient background.",
            SelfRunning: true),

        new("solar-system", "Solar system orbits", CategorySimulation, "🪐",
            "Build a self-contained single HTML file animating a top-down solar system: a sun and six planets " +
            "orbiting at different speeds and radii, each leaving a faint orbital trail. Pure CSS animation or " +
            "canvas — no libraries. Label each planet.",
            SelfRunning: true),

        new("matrix-rain", "Matrix code rain", CategoryGraphics, "🌧",
            "Build a self-contained single HTML file with the classic falling-glyph 'code rain' effect on a " +
            "full-screen canvas: columns of green characters falling at varying speeds, the leading character " +
            "brighter than the trail, with a fading motion blur behind them.",
            SelfRunning: true),

        new("audio-visualizer", "Waveform visualiser", CategoryGraphics, "📊",
            "Build a self-contained single HTML file showing an animated audio-style waveform visualiser: 64 " +
            "vertical bars whose heights are driven by layered sine waves so it looks like music is playing. " +
            "Bars are colour-graded by height, with a mirrored reflection below.",
            SelfRunning: true),

        new("typing-effect", "Terminal typing demo", CategoryInterface, "⌨",
            "Build a self-contained single HTML file that mimics a terminal typing itself out: it types a few " +
            "shell commands with a blinking cursor, prints plausible output after each, then clears and loops. " +
            "Monospace font, dark terminal styling, realistic per-character timing.",
            SelfRunning: true),

        new("sorting-visualizer", "Sorting algorithm race", CategoryDataViz, "📶",
            "Build a self-contained single HTML file that visualises bubble sort and quicksort racing on the " +
            "same shuffled array of 40 bars, side by side. Bars being compared highlight in a different colour. " +
            "It restarts automatically with a fresh shuffle when both finish.",
            SelfRunning: true),

        new("weather-card", "Weather dashboard card", CategoryInterface, "🌤",
            "Build a self-contained single HTML file with a polished weather dashboard card for one city: " +
            "current temperature, condition icon drawn in CSS or inline SVG, a five-day forecast row, and an " +
            "hourly temperature line chart. Use realistic hard-coded sample data — do not call any API. " +
            "Glassmorphic card on a gradient background.",
            SelfRunning: false),

        new("click-counter", "Click counter", CategoryInterface, "🔢",
            "Build a self-contained single HTML file with a click-counter app: a centred card showing a large " +
            "number and a button. Clicking increments the count with a spring animation. Add reset and " +
            "decrement buttons, and a clean modern design with rounded corners and a gradient background.",
            SelfRunning: false),

        new("markdown-preview", "Live markdown preview", CategoryTools, "📝",
            "Build a self-contained single HTML file with a two-pane live markdown editor: type markdown on " +
            "the left, see rendered HTML on the right, updating as you type. Implement the parser yourself — " +
            "headings, bold, italics, links, lists, inline code and fenced code blocks. No libraries.",
            SelfRunning: false),

        new("kanban-board", "Drag-and-drop kanban", CategoryTools, "🗂",
            "Build a self-contained single HTML file with a three-column kanban board (To do, In progress, " +
            "Done) holding six sample cards. Cards can be dragged between columns using the HTML5 drag-and-drop " +
            "API, with a visible drop indicator. Each column shows its card count.",
            SelfRunning: false),

        new("calculator", "Scientific calculator", CategoryTools, "🧮",
            "Build a self-contained single HTML file with a working scientific calculator: digits, the four " +
            "operators, parentheses, percentage, square root, and a running expression display above the " +
            "result. Full keyboard support. Dark theme with tactile-feeling buttons.",
            SelfRunning: false),

        new("chart-dashboard", "Analytics dashboard", CategoryDataViz, "📈",
            "Build a self-contained single HTML file with an analytics dashboard: four KPI tiles across the " +
            "top, then a line chart and a bar chart drawn with inline SVG from hard-coded sample data. " +
            "Charts have axes, gridlines and hover tooltips. No charting libraries.",
            SelfRunning: false),

        new("password-generator", "Password generator", CategoryTools, "🔐",
            "Build a self-contained single HTML file with a password generator: a length slider from 8 to 64, " +
            "toggles for uppercase, digits and symbols, a live strength meter, and a copy-to-clipboard button " +
            "that confirms it copied. Generate on any change.",
            SelfRunning: false),
    ];

    public static IReadOnlyList<string> Categories { get; } =
        All.Select(p => p.Category).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>The subset demo mode draws from — output that animates without being touched.</summary>
    public static IReadOnlyList<PromptTemplate> SelfRunning { get; } =
        All.Where(p => p.SelfRunning).ToList();

    public static PromptTemplate? ById(string? id) =>
        id is null ? null : All.FirstOrDefault(p => p.Id == id);

    public static IReadOnlyList<PromptTemplate> InCategory(string? category) =>
        string.IsNullOrEmpty(category) || category == "All"
            ? All
            : All.Where(p => p.Category == category).ToList();
}
