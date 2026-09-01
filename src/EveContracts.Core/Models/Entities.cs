namespace EveContracts.Core.Models;

/// <summary>A public contract observed via ESI, plus its cached profit evaluation.</summary>
public class PublicContract
{
    public long ContractId { get; set; }
    public int RegionId { get; set; }
    public string Type { get; set; } = "item_exchange"; // item_exchange | auction | courier
    public string Title { get; set; } = "";
    public double Price { get; set; }
    public long StartLocationId { get; set; }
    public int SolarSystemId { get; set; }
    public string SystemName { get; set; } = "";
    public string StationName { get; set; } = "";
    public double SecurityStatus { get; set; }
    public int JumpsToJita { get; set; }
    public DateTime DateIssued { get; set; }
    public DateTime DateExpired { get; set; }
    public double VolumeM3 { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool ItemsFetched { get; set; }

    // Evaluation results (denormalized so the UI filters instantly)
    public double JitaSellValue { get; set; }
    public double Fees { get; set; }
    public double Hauling { get; set; }
    public double NetProfit { get; set; }
    public double Margin { get; set; }
    public string Verdict { get; set; } = "PENDING"; // BUY | THIN | LOW VOL | SKIP | LOWSEC | EXCLUDED | PENDING
    public string FlagsJson { get; set; } = "[]";

    public List<ContractItem> Items { get; set; } = [];
}

public class ContractItem
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
    public bool IsIncluded { get; set; } // false = requested from the buyer (want-to-buy leg)
}

/// <summary>Jita 4-4 best buy/sell plus previous-day traded volume for a type.</summary>
public class Price
{
    public int TypeId { get; set; }
    public double JitaSell { get; set; }
    public double JitaBuy { get; set; }
    public double PrevDayVolume { get; set; }
    public DateTime PricesUpdatedAt { get; set; }
    public DateTime VolumeUpdatedAt { get; set; }
}

public class Character
{
    public int CharacterId { get; set; }
    public string Name { get; set; } = "";
    public byte[] RefreshTokenEncrypted { get; set; } = [];
    public string AccessToken { get; set; } = "";
    public DateTime TokenExpiry { get; set; }
    public string AuthStatus { get; set; } = "ok"; // ok | expired
    public DateTime LastSync { get; set; }
}

public class OwnContract
{
    public long Id { get; set; }
    public long ContractId { get; set; }
    public int CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string Direction { get; set; } = "OUT"; // OUT = issuer, IN = assignee/acceptor
    public string Type { get; set; } = "item_exchange";
    public string Title { get; set; } = "";
    public string Route { get; set; } = "";
    public string OtherParty { get; set; } = "";
    public string Status { get; set; } = "outstanding";
    public double Price { get; set; }
    public double Reward { get; set; }
    public double Collateral { get; set; }
    public DateTime DateIssued { get; set; }
    public DateTime DateExpired { get; set; }
    public DateTime? DateCompleted { get; set; }
    public double VolumeM3 { get; set; }
    public int DaysToComplete { get; set; }
    public double Buyout { get; set; }
    public bool ItemsFetched { get; set; }
}

public class OwnContractItem
{
    public long Id { get; set; }
    public long OwnContractId { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
    public bool IsIncluded { get; set; }
}

public class ItemSetting
{
    public int TypeId { get; set; }
    public double? MinDailyVolumeOverride { get; set; }
    public bool Excluded { get; set; }
}

/// <summary>Key/value store for global settings and sync bookkeeping.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

// ---- Static data (SDE) ----

public class ItemType
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public int GroupId { get; set; }
    public int CategoryId { get; set; }
    public double Volume { get; set; }          // unpackaged m3
    public double PackagedVolume { get; set; }  // packaged m3 (ships shrink)
    public bool IsRig { get; set; }             // rigs are destroyed on removal — sunk cost when fitted
}

public class SolarSystem
{
    public int SolarSystemId { get; set; }
    public string Name { get; set; } = "";
    public int RegionId { get; set; }
    public double Security { get; set; }
    public int JumpsToJita { get; set; } = -1; // -1 = unreachable by gates
}

public class Station
{
    public long StationId { get; set; }
    public string Name { get; set; } = "";
    public int SolarSystemId { get; set; }
}

/// <summary>Cached ETag per ESI URL so unchanged responses cost a 304, not a body.</summary>
public class EsiEtag
{
    public string Url { get; set; } = "";
    public string Etag { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
