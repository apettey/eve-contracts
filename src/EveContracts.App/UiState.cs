namespace EveContracts.App;

/// <summary>Shared UI state for the two views. Singleton; components subscribe to Changed.</summary>
public class UiState
{
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    // Tabs
    public string Tab { get; set; } = "scanner";

    // Scanner filters (defaults per the design prototype)
    public string Region { get; set; } = "The Forge";
    public int MinMargin { get; set; } = 10;
    public double MinVolume { get; set; } = 20;
    public int MaxPriceM { get; set; } = 800;
    public bool HighsecOnly { get; set; } = true;
    public string PriceBasis { get; set; } = "sell";
    public long? SelectedContractId { get; set; }

    // Own contracts filters
    public int? CharacterFilter { get; set; }
    public string DirFilter { get; set; } = "All";
    public string StatusFilter { get; set; } = "All";
    public long? SelectedOwnId { get; set; }

    public bool SettingsOpen { get; set; }
}
