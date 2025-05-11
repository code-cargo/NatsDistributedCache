using Xunit;

namespace CodeCargo.NatsDistributedCache.TestUtils.Assertions;

public static class ExceptionAssert
{
    public static void ThrowsArgumentOutOfRange(
        Action testCode,
        string paramName,
        string message,
        object actualValue)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(testCode);

        if (paramName is not null)
        {
            Assert.Equal(paramName, ex.ParamName);
        }

        if (message is not null)
        {
            Assert.StartsWith(message, ex.Message); // can have "\r\nParameter name:" etc
        }

        if (actualValue is not null)
        {
            Assert.Equal(actualValue, ex.ActualValue);
        }
    }

    public static async Task ThrowsArgumentOutOfRangeAsync(
        Func<Task> test,
        string paramName,
        string message,
        object actualValue)
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(test);
        if (paramName is not null)
        {
            Assert.Equal(paramName, ex.ParamName);
        }

        if (message is not null)
        {
            Assert.StartsWith(message, ex.Message); // can have "\r\nParameter name:" etc
        }

        if (actualValue is not null)
        {
            Assert.Equal(actualValue, ex.ActualValue);
        }
    }
}
