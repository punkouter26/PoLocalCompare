using PoLocalCompare.Api.Services;
using PoLocalCompare.Application.Archive.ExportLabReport;
using PoLocalCompare.Application.Duels.CommenceDuel;
using PoLocalCompare.Application.Duels.GetDuel;
using PoLocalCompare.Application.Duels.ListDuels;
using PoLocalCompare.Application.Duels.RecordVerdict;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Application.Leaderboard.GetKillList;
using PoLocalCompare.Application.Leaderboard.GetLeaderboard;
using PoLocalCompare.Application.Models.ListModels;
using PoLocalCompare.Application.Models.RegisterModel;

namespace PoLocalCompare.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<RegisterModelHandler>();
        services.AddScoped<ListModelsHandler>();
        services.AddScoped<CommenceDuelHandler>();
        services.AddScoped<GetDuelHandler>();
        services.AddScoped<GetLeaderboardHandler>();
        services.AddScoped<GetKillListHandler>();
        services.AddScoped<ListDuelsHandler>();
        services.AddScoped<ExportLabReportHandler>();
        services.AddSingleton<DuelExecutionService>();

        services.AddScoped<RecordVerdictHandler>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var kFactor = cfg.GetValue<double>("Elo:KFactor", 32.0);
            return new RecordVerdictHandler(
                sp.GetRequiredService<IDuelRepository>(),
                sp.GetRequiredService<IModelRepository>(),
                sp.GetRequiredService<IEloHistoryRepository>(),
                kFactor);
        });

        return services;
    }
}
