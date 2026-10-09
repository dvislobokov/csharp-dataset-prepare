namespace Shop.Tests;

public class OrderTests
{
    public void TotalIsSum()
    {
        var order = new Order { Customer = "a" };
        order.Lines.Add(new OrderLine("x", 2m, 3));
        var total = order.Total;
    }
}
