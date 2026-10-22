using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Demeter.Services.Fishing;

/// <summary>A fishing hole, from the <c>FishingSpot</c> sheet.</summary>
/// <param name="X">Map position, sheet units (the sheet carries no height).</param>
public sealed record FishingHole(uint Id, string Name, uint TerritoryId, string ZoneName, int X, int Z, int Radius, int Level);

/// <summary>
/// The game's side of fishing data: which holes hold a fish (<c>FishingSpot.Item</c>), and each
/// zone's weather chances (<c>TerritoryType.WeatherRate</c> → <c>WeatherRate</c>, made cumulative).
/// Read once; ocean voyages and spearfishing have sheets of their own and are not here.
/// </summary>
public sealed class FishSpots
{
    private readonly Dictionary<uint, List<FishingHole>> _holesByFish = new();
    private readonly Dictionary<uint, IReadOnlyList<(uint Weather, int Cumulative)>> _ratesByTerritory = new();
    private readonly Dictionary<uint, string> _weatherNames = new();
    private readonly Dictionary<uint, string> _itemNames = new();

    public FishSpots(IDataManager data, Action<string>? log = null)
    {
        try
        {
            foreach (var w in data.GetExcelSheet<Weather>())
                _weatherNames[w.RowId] = w.Name.ExtractText();

            var territories = data.GetExcelSheet<TerritoryType>();
            var rates = data.GetExcelSheet<WeatherRate>();
            foreach (var spot in data.GetExcelSheet<FishingSpot>())
            {
                var territory = spot.TerritoryType.RowId;
                if (territory == 0)
                    continue;
                var hole = new FishingHole(spot.RowId, spot.PlaceName.ValueNullable?.Name.ExtractText() ?? $"spot {spot.RowId}",
                    territory, territories.GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"territory {territory}",
                    spot.X, spot.Z, spot.Radius, spot.GatheringLevel);
                foreach (var item in spot.Item)
                    if (item.RowId != 0)
                        (_holesByFish.TryGetValue(item.RowId, out var l) ? l : _holesByFish[item.RowId] = []).Add(hole);

                if (!_ratesByTerritory.ContainsKey(territory)
                    && territories.GetRowOrDefault(territory) is { } t && rates.GetRowOrDefault(t.WeatherRate.RowId) is { } r)
                {
                    var chances = new List<(uint, int)>();
                    for (var i = 0; i < r.Weather.Count; i++)
                        chances.Add((r.Weather[i].RowId, r.Rate[i]));
                    _ratesByTerritory[territory] = WeatherForecast.Cumulative(chances);
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Reading the fishing spots failed ({ex.GetType().Name}: {ex.Message}).");
        }

        _items = data;
    }

    private readonly IDataManager _items;

    public IReadOnlyList<FishingHole> HolesFor(uint fishItemId) => _holesByFish.GetValueOrDefault(fishItemId) ?? [];

    public IReadOnlyList<(uint Weather, int Cumulative)> RatesFor(uint territoryId)
        => _ratesByTerritory.GetValueOrDefault(territoryId) ?? [];

    public string WeatherName(uint id) => _weatherNames.GetValueOrDefault(id) is { Length: > 0 } n ? n : $"weather {id}";

    public string ItemName(uint id)
    {
        if (_itemNames.TryGetValue(id, out var cached))
            return cached;
        var name = _items.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText().Trim();
        return _itemNames[id] = string.IsNullOrEmpty(name) ? $"item {id}" : name;
    }
}
