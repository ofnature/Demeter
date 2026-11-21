using System;
using Dalamud.Plugin.Services;
using Demeter.Services.Fishing.Ocean;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;

namespace Demeter.Services.Game;

/// <summary>
/// The voyage, from the game's ocean fishing director (<c>InstanceContentOceanFishing</c>): there
/// only while the character is aboard, and the one source for route, zone and spectral current.
/// </summary>
public sealed unsafe class GameOceanVoyage : IOceanVoyageSource
{
    private readonly IDataManager _data;

    public GameOceanVoyage(IDataManager data) => _data = data;

    public VoyageReading? Read()
    {
        var framework = EventFramework.Instance();
        if (framework == null)
            return null;
        var voyage = framework->GetInstanceContentOceanFishing();
        if (voyage == null)
            return null;

        var routeId = voyage->CurrentRoute;
        var zone = (int)voyage->CurrentZone;
        var route = _data.GetExcelSheet<IKDRoute>().GetRowOrDefault(routeId);
        var spot = route is { } r && zone >= 0 && zone < r.Spot.Count ? r.Spot[zone].ValueNullable : null;
        return new VoyageReading(
            routeId,
            route?.Name.ExtractText() ?? $"route {routeId}",
            zone,
            spot?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"zone {zone + 1}",
            (VoyageStatus)(int)voyage->Status,
            voyage->SpectralCurrentActive,
            spot?.SpotMain.RowId ?? 0,
            spot?.SpotSub.RowId ?? 0);
    }
}
