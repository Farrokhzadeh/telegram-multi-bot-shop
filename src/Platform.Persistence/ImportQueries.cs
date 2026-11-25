namespace Platform.Persistence;

internal sealed partial class EfSession
{
    internal void RequireImportCommand()
    {
        if (!write || !nativeWriterBoundary || writer is not null) throw Denied("An offline import transaction is required.");
        admitted = true;
    }
}
