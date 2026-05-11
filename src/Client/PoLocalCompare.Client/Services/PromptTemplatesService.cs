using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

public sealed class PromptTemplatesService
{
    private readonly List<PromptTemplate> _templates = new()
    {
        new PromptTemplate
        {
            Id = "html-generator",
            Name = "HTML Generator",
            Category = "Web Development",
            Icon = "🌐",
            Prompt = "Create a complete, working single-page HTML application. The app should include: modern responsive CSS with a dark theme, interactive JavaScript functionality, proper semantic HTML structure, and a polished user interface. Focus on making something visually impressive and fully functional."
        },
        new PromptTemplate
        {
            Id = "landing-page",
            Name = "Landing Page",
            Category = "Marketing",
            Icon = "🚀",
            Prompt = "Create a stunning landing page with hero section, feature grid, testimonials, pricing table, and CTA buttons. Use a dark color scheme with green accents. Include smooth animations on scroll and interactive hover effects. Make it conversion-focused with clear hierarchy."
        },
        new PromptTemplate
        {
            Id = "dashboard",
            Name = "Dashboard UI",
            Category = "Web Development",
            Icon = "📊",
            Prompt = "Build a complete admin dashboard interface with sidebar navigation, data tables with sorting, charts visualization, stat cards, and a modern dark theme. Include realistic mock data. Focus on data density while maintaining readability."
        },
        new PromptTemplate
        {
            Id = "todo-app",
            Name = "Todo App",
            Category = "Productivity",
            Icon = "✅",
            Prompt = "Create a beautiful and functional todo list application with task categories, priority levels, due dates, drag-and-drop reordering, and local storage persistence. Include filtering, search, and bulk actions. Dark theme with neon accents."
        },
        new PromptTemplate
        {
            Id = "form-builder",
            Name = "Form Builder",
            Category = "Utilities",
            Icon = "📝",
            Prompt = "Design a dynamic form builder interface where users can add different field types (text, email, select, checkbox, radio, date, file upload) with validation rules. Include a live preview mode. Modern dark UI with drag-and-drop functionality."
        },
        new PromptTemplate
        {
            Id = "portfolio",
            Name = "Portfolio Site",
            Category = "Marketing",
            Icon = "🎨",
            Prompt = "Create an impressive personal portfolio website with project showcase, skills section, about me page, and contact form. Include smooth page transitions, parallax effects, and a distinctive dark design aesthetic. Make it memorable and professional."
        },
        new PromptTemplate
        {
            Id = "weather-app",
            Name = "Weather Widget",
            Category = "Utilities",
            Icon = "🌤️",
            Prompt = "Build a visually stunning weather dashboard with current conditions, 7-day forecast, hourly charts, location search, and animated weather icons. Use a glassmorphism dark theme with dynamic gradients based on weather conditions."
        },
        new PromptTemplate
        {
            Id = "game-ui",
            Name = "Game Interface",
            Category = "Entertainment",
            Icon = "🎮",
            Prompt = "Design a retro-futuristic game interface with score display, inventory grid, character stats panel, and animated effects. Include pixel-art inspired elements mixed with modern glass effects. Make it feel like a premium indie game HUD."
        }
    };

    public IReadOnlyList<PromptTemplate> GetAll() => _templates.AsReadOnly();
    
    public IEnumerable<string> GetCategories() => _templates.Select(t => t.Category).Distinct();
    
    public IEnumerable<PromptTemplate> GetByCategory(string category) => 
        _templates.Where(t => t.Category == category);
    
    public PromptTemplate? GetById(string id) => _templates.FirstOrDefault(t => t.Id == id);
    
    public IEnumerable<PromptTemplate> Search(string query) =>
        _templates.Where(t => 
            t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            t.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            t.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public record PromptTemplate
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string Icon { get; init; }
    public required string Prompt { get; init; }
}