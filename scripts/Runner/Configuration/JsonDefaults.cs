namespace Runner.Configuration;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}