using Microsoft.Extensions.Logging;

namespace Shop;

public class OrderService(IOrderRepository repository, ILogger<OrderService> logger)
{
    private readonly int _maxLines = 10;

    public async Task<Order> GetOrderAsync(Guid id, CancellationToken ct)
    {
        var order = await repository.GetByIdAsync(id, ct);
        if (order is null)
        {
            logger.LogWarning("Order {OrderId} was not found", id);
            throw new InvalidOperationException($"Order {id} missing");
        }
        var expensive = order.Lines.Where(l => l.Price > 100).Select(l => l.Sku).ToList();
        int count = expensive.Count;
        return order;
    }

    public decimal Sum(Order order)
    {
        decimal total = 0;
        foreach (var line in order.Lines)
        {
            total += line.Price * line.Quantity;
        }
        return total;
    }

    public static string Describe(Order? order) => order?.Customer ?? "none";

    public Guid Handle(Order @event)
    {
        var copy = @event.Id;
        return copy;
    }
}
