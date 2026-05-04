using Hermod.Core.Mqtt;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Tests for the fast-path <c>_uuid</c> extractor used on the ingest
/// hot path. The extractor must return null (not throw) on any
/// malformed input — production traffic is noisy and a parse failure
/// in the ingest callback would take the whole pump down.
/// </summary>
public class PayloadUuidExtractorTests
{
    [Fact]
    public void TryExtract_PayloadWithUuid_ReturnsValue()
    {
        var payload = "{\"state\":\"ON\",\"_uuid\":\"abc-123\",\"seq\":42}";

        var result = PayloadUuidExtractor.TryExtract(payload);

        Assert.Equal("abc-123", result);
    }

    [Fact]
    public void TryExtract_PayloadWithoutUuid_ReturnsNull()
    {
        var payload = "{\"state\":\"ON\",\"seq\":42}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_EmptyPayload_ReturnsNull()
    {
        Assert.Null(PayloadUuidExtractor.TryExtract(string.Empty));
    }

    [Fact]
    public void TryExtract_NullPayload_ReturnsNull()
    {
        Assert.Null(PayloadUuidExtractor.TryExtract(null));
    }

    [Fact]
    public void TryExtract_MalformedJson_ReturnsNull()
    {
        var payload = "{\"_uuid\":\"abc\",broken";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_NonObjectJson_ReturnsNull()
    {
        Assert.Null(PayloadUuidExtractor.TryExtract("[1,2,3]"));
    }

    [Fact]
    public void TryExtract_UuidAsNumber_ReturnsNull()
    {
        var payload = "{\"_uuid\":42}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_UuidSubstringInOtherField_DoesNotFalseMatch()
    {
        var payload = "{\"note\":\"not _uuid here\"}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }
}
