namespace Phi.Compiler.Tests
{
    /// <summary>What a struct owns is only known once it's been worked out, and asking before that isn't answered "nothing".</summary>
    public class OwnedSlotsTests
    {
        [Fact]
        public void AskingWhatAStructOwnsBeforeItIsWorkedOutStopsTheCompiler()
        {
            var early = new Semantics.StructSymbol { Name = "Request" };
            var error = Assert.Throws<InvalidOperationException>(() => early.OwnsObjects);
            Assert.Contains("Request", error.Message);

            early.OwnedSlotsFound = true;
            Assert.False(early.OwnsObjects);
        }

        [Fact]
        public void EveryStructOfAProgramHasItWorkedOut()
        {
            // structs that hold each other in every order, one that takes types, and one nothing uses
            const string source = """
                struct.Outer
                {
                	Inner inside;
                	new Line<Inner> lines[];
                }

                struct.Inner
                {
                	new str name;
                }

                struct.Line<Item>
                {
                	Item items[];
                }

                struct.Unused
                {
                	int x;
                }

                phi.Owned:Program
                {
                	new Outer outer;
                	outer.inside.name is 'in';
                	log outer.inside.name '\n';
                }

                """;
            CompileResult result = PhiCompiler.Compile(new SourceFile("owned.phi", source), host: Host.Linux64);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        }
    }
}
