using EveContracts.Core.Formatting;
using EveContracts.Core.Sde;
using EveContracts.Core.Services;
using Xunit;

namespace EveContracts.Tests;

public class EvaluationTests
{
    private static readonly EvalThresholds Defaults = new(
        MinMarginPct: 10, MinDailyVolume: 20, MaxPrice: 800e6,
        FeePct: 4.5, HaulRate: 800, HighsecOnly: true, IncludeAuctions: false, IncludeCharges: false);

    private static EvalItem Ship(double sell, double buy = 0, double vol = 100, long qty = 1, double m3 = 10000) =>
        new(587, "Test Ship", qty, true, Categories.Ship, m3, sell, buy, vol, null, false);

    private static EvalInput Contract(double price, double sec = 0.9, int jumps = 0, params EvalItem[] items) =>
        new("item_exchange", price, sec, jumps, DateTime.UtcNow.AddDays(3), "test", items);

    [Fact]
    public void ProfitableHighsecContract_IsBuy()
    {
        // 300M sell, 200M price, 0 jumps: fees = 13.5M, profit = 86.5M, margin 43%
        var r = EvaluationService.Evaluate(Contract(200e6, items: Ship(300e6)), Defaults);
        Assert.Equal("BUY", r.Verdict);
        Assert.Equal(300e6 * 0.045, r.Fees, 3);
        Assert.Equal(0, r.Hauling);
        Assert.Equal(300e6 - 200e6 - 13.5e6, r.NetProfit, 3);
    }

    [Fact]
    public void HaulingFormula_MatchesSpec()
    {
        // 10,000 m3, 9 jumps: 10000 * 800 * (0.5 + 0.9) = 11.2M
        var r = EvaluationService.Evaluate(Contract(50e6, jumps: 9, items: Ship(100e6)), Defaults);
        Assert.Equal(10000 * 800 * (0.5 + 0.9), r.Hauling, 3);
    }

    [Fact]
    public void JitaContract_NoHauling()
    {
        var r = EvaluationService.Evaluate(Contract(50e6, jumps: 0, items: Ship(100e6)), Defaults);
        Assert.Equal(0, r.Hauling);
    }

    [Fact]
    public void LowsecPickup_TrumpsProfit()
    {
        var r = EvaluationService.Evaluate(Contract(50e6, sec: 0.4, items: Ship(500e6)), Defaults);
        Assert.Equal("LOWSEC", r.Verdict);
        Assert.Contains("LOWSEC_PICKUP", r.Flags);
    }

    [Fact]
    public void Lowsec_AllowedWhenHighsecOnlyOff()
    {
        var r = EvaluationService.Evaluate(Contract(50e6, sec: 0.4, items: Ship(500e6)),
            Defaults with { HighsecOnly = false });
        Assert.Equal("BUY", r.Verdict);
    }

    [Fact]
    public void UnprofitableContract_IsSkip()
    {
        var r = EvaluationService.Evaluate(Contract(200e6, items: Ship(100e6)), Defaults);
        Assert.Equal("SKIP", r.Verdict);
        Assert.True(r.NetProfit < 0);
    }

    [Fact]
    public void LowVolumeItem_IsLowVol()
    {
        var r = EvaluationService.Evaluate(Contract(100e6, items: Ship(300e6, buy: 250e6, vol: 5)), Defaults);
        Assert.Equal("LOW VOL", r.Verdict);
        Assert.Contains("LOW_VOLUME_ITEM", r.Flags);
    }

    [Fact]
    public void PerItemVolumeOverride_Wins()
    {
        var item = Ship(300e6, buy: 250e6, vol: 5) with { MinVolumeOverride = 2 };
        var r = EvaluationService.Evaluate(Contract(100e6, items: item), Defaults);
        Assert.Equal("BUY", r.Verdict);
    }

    [Fact]
    public void ThinMargin_IsThin()
    {
        // sell 300M, price 280M: profit ~6.5M, margin ~2.3% < 10%
        var r = EvaluationService.Evaluate(Contract(280e6, items: Ship(300e6)), Defaults);
        Assert.Equal("THIN", r.Verdict);
    }

    [Fact]
    public void MineralContract_IsExcluded()
    {
        var mineral = new EvalItem(34, "Tritanium", 1000000, true, 4 /* Material */, 0.01, 5, 4, 1e9, null, false);
        var r = EvaluationService.Evaluate(Contract(1e6, items: mineral), Defaults);
        Assert.Equal("EXCLUDED", r.Verdict);
    }

    [Fact]
    public void ChargeItem_ExcludedByDefault_IncludedWhenEnabled()
    {
        var charge = new EvalItem(240, "Ammo", 1000, true, Categories.Charge, 0.0125, 100, 80, 5000, null, false);
        Assert.Equal("EXCLUDED", EvaluationService.Evaluate(Contract(10e3, items: charge), Defaults).Verdict);
        Assert.NotEqual("EXCLUDED", EvaluationService.Evaluate(Contract(10e3, items: charge),
            Defaults with { IncludeCharges = true }).Verdict);
    }

    [Fact]
    public void Auction_ExcludedByDefault()
    {
        var input = Contract(100e6, items: Ship(300e6)) with { ContractType = "auction" };
        Assert.Equal("EXCLUDED", EvaluationService.Evaluate(input, Defaults).Verdict);
        Assert.Equal("BUY", EvaluationService.Evaluate(input, Defaults with { IncludeAuctions = true }).Verdict);
    }

    [Fact]
    public void WantToBuy_IsExcluded()
    {
        var requested = Ship(300e6) with { IsIncluded = false };
        var r = EvaluationService.Evaluate(Contract(0, items: requested), Defaults);
        Assert.Equal("EXCLUDED", r.Verdict);
    }

    [Fact]
    public void RequestedItems_SubtractAtJitaBuy()
    {
        var give = Ship(300e6);
        var want = Ship(0, buy: 50e6) with { IsIncluded = false };
        var r = EvaluationService.Evaluate(Contract(100e6, items: [give, want]), Defaults);
        Assert.Equal(300e6 - 50e6, r.JitaSellValue, 3);
    }

    [Fact]
    public void OverMaxPrice_IsExcluded()
    {
        var r = EvaluationService.Evaluate(Contract(900e6, items: Ship(2e9)), Defaults);
        Assert.Equal("EXCLUDED", r.Verdict);
    }

    [Fact]
    public void IlliquidItem_ValuedAtJitaBuy_NotSellWall()
    {
        // Officer mod: fake 60B sell wall, real 2.6B buy orders, ~0 daily volume.
        var officer = Ship(60e9, buy: 2.6e9, vol: 1);
        var r = EvaluationService.Evaluate(Contract(2e9, items: officer), Defaults with { MaxPrice = 100e9 });
        Assert.Equal(2.6e9, r.JitaSellValue, 3); // buy max, not the wall
        Assert.Equal("LOW VOL", r.Verdict);
    }

    [Fact]
    public void FreeBaitContract_IsScam()
    {
        var officer = Ship(60e9, buy: 2.6e9, vol: 1);
        var r = EvaluationService.Evaluate(Contract(0, items: officer), Defaults);
        Assert.Equal("SCAM", r.Verdict);
        Assert.Contains("TOO_GOOD", r.Flags);
    }

    [Fact]
    public void TooGoodIlliquid_IsScam_ButLiquidSnipePasses()
    {
        // Illiquid + 25x return: scam.
        var illiquid = Ship(3e9, buy: 2.6e9, vol: 1);
        Assert.Equal("SCAM", EvaluationService.Evaluate(Contract(100e6, items: illiquid), Defaults).Verdict);

        // Liquid + 13x return: genuine mispriced snipe — flagged, not blocked.
        var liquid = Ship(15e9, buy: 14e9, vol: 200);
        var r = EvaluationService.Evaluate(Contract(1e9, items: liquid), Defaults with { MaxPrice = 100e9 });
        Assert.Equal("BUY", r.Verdict);
        Assert.Contains("TOO_GOOD", r.Flags);
    }

    [Fact]
    public void UnpricedRequestedItem_IsScam()
    {
        var give = Ship(300e6);
        var want = Ship(0, buy: 0) with { IsIncluded = false };
        var r = EvaluationService.Evaluate(Contract(100e6, items: [give, want]), Defaults);
        Assert.Equal("SCAM", r.Verdict);
        Assert.Contains("UNPRICED_REQUESTED", r.Flags);
    }

    [Fact]
    public void RequestedItems_CostAtAcquisitionPrice()
    {
        // Handing over an item costs its Jita SELL (you buy it off sell orders).
        var give = Ship(300e6);
        var want = Ship(80e6, buy: 50e6) with { IsIncluded = false };
        var r = EvaluationService.Evaluate(Contract(100e6, items: [give, want]), Defaults);
        Assert.Equal(300e6 - 80e6, r.JitaSellValue, 3);
    }

    [Fact]
    public void FittedRigs_AreSunkCost()
    {
        // Ship + rigs: rigs are fitted (destroyed on removal) — worth 0 on resale.
        var hull = Ship(300e6);
        var rig = new EvalItem(31718, "Large Trimark Armor Pump I", 3, true, Categories.Module,
            5, 8e6, 6e6, 500, null, false, IsRig: true);
        var r = EvaluationService.Evaluate(Contract(200e6, items: [hull, rig]), Defaults);
        Assert.Equal(300e6, r.JitaSellValue, 3); // hull only, no rig value
        Assert.Contains("RIGGED_HULL", r.Flags);

        // Loose rigs (no ship in the contract) sell normally.
        var loose = EvaluationService.Evaluate(Contract(10e6, items: rig), Defaults);
        Assert.Equal(24e6, loose.JitaSellValue, 3);
        Assert.DoesNotContain("RIGGED_HULL", loose.Flags);
    }

    [Fact]
    public void SunkRigVolume_DoesNotTriggerLowVol()
    {
        var hull = Ship(300e6);
        var illiquidRig = new EvalItem(31718, "Rare Rig", 1, true, Categories.Module,
            5, 8e6, 6e6, 1 /* below floor */, null, false, IsRig: true);
        var r = EvaluationService.Evaluate(Contract(200e6, items: [hull, illiquidRig]), Defaults);
        Assert.Equal("BUY", r.Verdict);
    }

    [Fact]
    public void BuyBasis_ValuesEverythingAtBuyMax()
    {
        var liquid = Ship(300e6, buy: 260e6, vol: 100);
        var r = EvaluationService.Evaluate(Contract(200e6, items: liquid),
            Defaults with { PriceBasis = "buy" });
        Assert.Equal(260e6, r.JitaSellValue, 3);
        // Same contract on sell basis uses the sell price.
        var rs = EvaluationService.Evaluate(Contract(200e6, items: liquid), Defaults);
        Assert.Equal(300e6, rs.JitaSellValue, 3);
    }

    [Fact]
    public void ScamTitle_Flagged_ButDoesNotBlock()
    {
        var input = Contract(100e6, items: Ship(300e6)) with { Title = "★ Cheap Golem quick sale" };
        var r = EvaluationService.Evaluate(input, Defaults);
        Assert.Equal("BUY", r.Verdict);
        Assert.Contains("SCAM_TITLE", r.Flags);
    }

    [Fact]
    public void QuantityMultipliesEverything()
    {
        var r = EvaluationService.Evaluate(Contract(500e6, jumps: 10, items: Ship(300e6, qty: 3, m3: 10000)), Defaults);
        Assert.Equal(900e6, r.JitaSellValue, 3);
        Assert.Equal(30000, r.TotalM3, 3);
        Assert.Equal(30000 * 800 * 1.5, r.Hauling, 3);
    }
}

public class IskFormatTests
{
    [Theory]
    [InlineData(1.45e9, "1.45B")]
    [InlineData(385e6, "385.0M")]
    [InlineData(22e3, "22K")]
    [InlineData(500, "500")]
    public void Short_MatchesDesign(double v, string expected) => Assert.Equal(expected, IskFormat.Short(v));

    [Fact]
    public void Negative_UsesTypographicMinus() => Assert.Equal("−1.5M", IskFormat.Short(-1.5e6));

    [Fact]
    public void Signed_PrefixesPlus() => Assert.Equal("+87.0M", IskFormat.Signed(87e6));
}

public class CsvTests
{
    [Fact]
    public void QuotedFieldsWithCommasAndNewlines()
    {
        var input = "id,name,desc\n1,Rifter,\"A fast, agile frigate.\nLine two\"\n2,Slasher,plain\n";
        var rows = Csv.ReadRecords(new StringReader(input)).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("A fast, agile frigate.\nLine two", rows[1][2]);
        Assert.Equal("Slasher", rows[2][1]);
    }

    [Fact]
    public void EscapedQuotes()
    {
        var rows = Csv.ReadRecords(new StringReader("a,b\n\"say \"\"hi\"\"\",2\n")).ToList();
        Assert.Equal("say \"hi\"", rows[1][0]);
    }
}
