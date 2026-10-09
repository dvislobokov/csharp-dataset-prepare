namespace Shop;

public static class Strings
{
#if NEVER_DEFINED
    public static int Hidden = 1;
#endif
    public static string Raw = """
        multi line raw
        """;
    public static string Plain = "hello world"; // trailing comment here
    public static string Interp(int x) => $"value {x} here";
}
