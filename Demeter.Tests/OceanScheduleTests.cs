using Demeter.Services.Fishing.Ocean;

namespace Demeter.Tests;

public class OceanScheduleTests
{
    private static DateTime Utc(int hour, int minute, int second = 0) => new(2026, 11, 20, hour, minute, second, DateTimeKind.Utc);

    [Theory]
    [InlineData(14, 0, true)]
    [InlineData(14, 14, true)]
    [InlineData(14, 15, false)]
    [InlineData(15, 5, false)]   // an odd hour has no boat
    [InlineData(0, 3, true)]
    public void Sign_up_is_open_for_the_first_quarter_hour_of_every_even_hour(int hour, int minute, bool open)
        => Assert.Equal(open, OceanSchedule.SignUpOpen(Utc(hour, minute)));

    [Fact]
    public void The_next_sign_up_is_the_open_one_or_the_next_even_hour()
    {
        Assert.Equal(Utc(14, 0), OceanSchedule.NextSignUp(Utc(14, 10)));
        Assert.Equal(Utc(16, 0), OceanSchedule.NextSignUp(Utc(14, 15)));
        Assert.Equal(Utc(16, 0), OceanSchedule.NextSignUp(Utc(15, 59, 59)));
        Assert.Equal(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), OceanSchedule.NextSignUp(Utc(23, 30)));
    }

    [Theory]
    [InlineData(1u, OceanRoute.Indigo)]
    [InlineData(12u, OceanRoute.Indigo)]
    [InlineData(13u, OceanRoute.Ruby)]
    [InlineData(21u, OceanRoute.Ruby)]
    public void Routes_one_to_twelve_leave_from_Limsa_and_the_rest_from_Kugane(uint routeId, OceanRoute boat)
        => Assert.Equal(boat, OceanSchedule.RouteOf(routeId));
}
