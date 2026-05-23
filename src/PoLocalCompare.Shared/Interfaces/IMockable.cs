namespace PoLocalCompare.Shared.Interfaces;

/// <summary>
/// Marker interface for service implementations that return simulated/mock data
/// instead of calling real external dependencies.
/// </summary>
/// <remarks>
/// Pattern: Marker Interface (GoF) — used by the UI layer to detect when the
/// application is running in mock mode and surface a "USING MOCK DATA" banner
/// to developers and testers. Any service registered via DI that implements
/// this interface will trigger the banner.
/// </remarks>
public interface IMockable { }
