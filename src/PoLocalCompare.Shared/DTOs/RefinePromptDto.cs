namespace PoLocalCompare.Shared.DTOs;

/// <summary>A rough request to be sharpened into a brief (<c>POST /api/prompts/refine</c>).</summary>
public sealed record RefinePromptRequest(string Prompt);

/// <summary>The sharpened brief. It replaces the prompt text; nothing is stored server-side.</summary>
public sealed record RefinePromptResponse(string Prompt);
