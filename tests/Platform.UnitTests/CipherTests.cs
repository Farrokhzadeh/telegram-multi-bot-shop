using Platform.Testing;

namespace Platform.UnitTests;

public sealed class CipherTests
{
    [Fact]
    public void GoldenScopeRotationAndCorruptionQualification() => CipherQualification.Run();
}
