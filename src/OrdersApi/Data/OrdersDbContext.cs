namespace OrdersApi.Data;

internal sealed class OrdersDbContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();
        order.ToTable("orders");
        order.Property(o => o.Id).HasColumnName("id");
        order.Property(o => o.MessageId).HasColumnName("message_id");
        order.Property(o => o.DeliveryAttemptHeader).HasColumnName("delivery_attempt_header");
        order.Property(o => o.CreatedAt).HasColumnName("created_at");
    }
}