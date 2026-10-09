namespace Shop;

public sealed class Order
{
    public Guid Id { get; init; }
    public string Customer { get; set; } = "";
    public List<OrderLine> Lines { get; } = [];
    public decimal Total => Lines.Sum(l => l.Price * l.Quantity);
}

public sealed record OrderLine(string Sku, decimal Price, int Quantity);

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Order?> GetByIdAsync(Guid id);
    Task AddAsync(Order order, CancellationToken ct);
}
