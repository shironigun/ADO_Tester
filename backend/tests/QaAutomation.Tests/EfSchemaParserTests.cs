using QaAutomation.Infrastructure.SchemaExplorer;

namespace QaAutomation.Tests;

/// <summary>Self-check for the Phase-B EF parser against the real product patterns
/// ([Table(nameof/Schema)], [Key], [ForeignKey] navs, DbSet&lt;T&gt;, HasNoKey, composite HasKey).</summary>
public sealed class EfSchemaParserTests
{
    private const string Entity = """
        namespace MyApp.Customers.Services.Models.Data
        {
            [Table(nameof(Customer ), Schema = Schemas.Customers)]
            public class Customer
            {
                [Key]
                public int CrmCustomerId { get; set; }
                public int DealerId { get; set; }
                public string Name { get; set; }
                public DateTime? OptOutDate { get; set; }

                [ForeignKey(nameof(CrmCustomerId))]
                public List<CustomerPhone> CustomerPhones { get; set; }

                [ForeignKey(nameof(DealerId))]
                public Dealer Dealer { get; set; }
            }
        }
        """;

    [Fact]
    public void Parses_table_key_columns_and_navs()
    {
        var cls = Assert.Single(EfSchemaParser.ParseClasses(Entity));

        Assert.Equal("Customer", cls.ClassName);
        Assert.Equal("Customer", cls.TableName);
        Assert.Equal("Customers", cls.Schema);   // Schemas.Customers → last segment

        // Scalar columns (nav props are NOT scalars).
        var scalars = cls.Props.Where(p => p.RawType is var t && EfSchemaParser.CollectionElement(t) is null
            && EfSchemaParser.IsScalar(t)).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "CrmCustomerId", "DealerId", "Name", "OptOutDate" }, scalars);

        Assert.True(cls.Props.Single(p => p.Name == "CrmCustomerId").IsKey);

        // Collection nav → element type + FK column.
        var phones = cls.Props.Single(p => p.Name == "CustomerPhones");
        Assert.Equal("CustomerPhone", EfSchemaParser.CollectionElement(phones.RawType));
        Assert.Equal("CrmCustomerId", phones.FkColumn);

        // Reference nav → type + FK column.
        var dealer = cls.Props.Single(p => p.Name == "Dealer");
        Assert.Null(EfSchemaParser.CollectionElement(dealer.RawType));
        Assert.False(EfSchemaParser.IsScalar(dealer.RawType));
        Assert.Equal("DealerId", dealer.FkColumn);
    }

    private const string Context = """
        public class CustomerDbContext : DbContext
        {
            public DbSet<Customer> Customer { get; set; }
            public DbSet<CustomerView> CustomerView { get; set; }
            public DbSet<SegmentLocation> SegmentLocations { get; set; }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<SegmentLocation>().HasKey(x => new { x.SegmentId, x.DealerLocationId });
                modelBuilder.Entity<CustomerView>().HasNoKey();
            }
        }
        """;

    [Fact]
    public void Parses_dbcontext_dbsets_nokey_and_composite_keys()
    {
        Assert.True(EfSchemaParser.LooksLikeDbContext(Context));
        var info = EfSchemaParser.ParseDbContext(Context);

        Assert.Equal("CustomerDbContext", info.ClassName);
        Assert.Equal(new[] { "Customer", "CustomerView", "SegmentLocation" }, info.DbSetTypes);
        Assert.Contains("CustomerView", info.HasNoKey);
        Assert.Equal(new[] { "SegmentId", "DealerLocationId" }, info.CompositeKeys["SegmentLocation"]);
    }
}
