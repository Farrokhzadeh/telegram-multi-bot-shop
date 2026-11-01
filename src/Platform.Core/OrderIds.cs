namespace Platform.Core;

public static class OrderIds
{
    public static Guid New() => Guid.CreateVersion7();
}
