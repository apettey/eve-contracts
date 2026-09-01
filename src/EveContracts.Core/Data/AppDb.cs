using EveContracts.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EveContracts.Core.Data;

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<PublicContract> PublicContracts => Set<PublicContract>();
    public DbSet<ContractItem> ContractItems => Set<ContractItem>();
    public DbSet<Price> Prices => Set<Price>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<OwnContract> OwnContracts => Set<OwnContract>();
    public DbSet<ItemSetting> ItemSettings => Set<ItemSetting>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ItemType> ItemTypes => Set<ItemType>();
    public DbSet<SolarSystem> SolarSystems => Set<SolarSystem>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<EsiEtag> EsiEtags => Set<EsiEtag>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PublicContract>(e =>
        {
            e.HasKey(x => x.ContractId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RegionId);
            e.HasIndex(x => x.Verdict);
            e.HasIndex(x => x.NetProfit);
            e.HasIndex(x => x.DateExpired);
        });
        b.Entity<ContractItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.TypeId);
        });
        b.Entity<Price>().HasKey(x => x.TypeId);
        b.Entity<Character>().HasKey(x => x.CharacterId);
        b.Entity<OwnContract>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ContractId, x.CharacterId }).IsUnique();
            e.HasIndex(x => x.Status);
        });
        b.Entity<ItemSetting>().HasKey(x => x.TypeId);
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<ItemType>(e =>
        {
            e.HasKey(x => x.TypeId);
            e.HasIndex(x => x.CategoryId);
        });
        b.Entity<SolarSystem>().HasKey(x => x.SolarSystemId);
        b.Entity<Station>().HasKey(x => x.StationId);
        b.Entity<EsiEtag>().HasKey(x => x.Url);
    }
}
