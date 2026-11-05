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

        /// <summary>What a script says when the machine ends as soon as it's connected to it.</summary>
        static string? RunAgainstAMachineThatEnds(params string[] lines)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Task closing = Task.Run(() =>
            {
                using System.Net.Sockets.TcpClient accepted = listener.AcceptTcpClient();
                accepted.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);    // it's gone at once
            });
            try { return InputScript.Run(port, lines, () => "", CancellationToken.None); }
            finally
            {
                closing.Wait();
                listener.Stop();
            }
        }

        [Fact]
        public void AMachineThatEndsDuringTheLastLineIsNotAFailure()
        {
            Assert.Null(RunAgainstAMachineThatEnds("type abcdef"));
            Assert.Null(RunAgainstAMachineThatEnds("sendkey a", "# and nothing after it", ""));
        }

        [Fact]
        public void AMachineThatEndsWithLinesLeftSaysWhichOneItDidntGetTo()
        {
            string? why = RunAgainstAMachineThatEnds("type abcdef", "", "sendkey b", "sendkey c");
            Assert.Equal("the input script hadn't got to line 3: sendkey b", why);
        }
    }
}
