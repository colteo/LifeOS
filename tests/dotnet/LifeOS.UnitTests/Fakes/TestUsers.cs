using LifeOS.Api.Authentication;

namespace LifeOS.UnitTests.Fakes;

// Fixed LifeOS user ids for Finance tests. A is the caller unless a test says otherwise;
// B is another user whose data must stay invisible to A.
internal static class TestUsers
{
    public static readonly Guid A = new("01a0ea76-0c00-7000-8000-00000000000a");
    public static readonly Guid B = new("01a0ea76-0c00-7000-8000-00000000000b");

    public static AuthenticatedUser AuthenticatedA => new(A);

    public static AuthenticatedUser AuthenticatedB => new(B);
}
