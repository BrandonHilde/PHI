using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>How an input script's expect finds its text in the serial output.</summary>
    public class InputScriptTests
    {
        const string TwoLogins = "login: ok\nwelcome\nlogin: ok\nwelcome\n";

        [Fact]
        public void ExpectFindsTextAnywhereInTheOutput()
        {
            int first = InputScript.FindExpected(TwoLogins, "welcome", next: false, seen: 0);
            Assert.Equal(10, first);
            // after the second login was seen, a plain expect still finds the first welcome
            Assert.Equal(0, InputScript.FindExpected(TwoLogins, "login: ok", next: false, seen: TwoLogins.Length));
        }

        [Fact]
        public void ExpectNextOnlyLooksAfterWhatTheLastExpectSaw()
        {
            int seen = InputScript.FindExpected(TwoLogins, "welcome", next: false, seen: 0) + "welcome".Length;
            Assert.Equal(28, InputScript.FindExpected(TwoLogins, "welcome", next: true, seen: seen));
            Assert.Equal(-1, InputScript.FindExpected(TwoLogins, "welcome", next: true, seen: TwoLogins.Length));
            Assert.Equal(-1, InputScript.FindExpected("short", "welcome", next: true, seen: 100));
        }
    }
}
