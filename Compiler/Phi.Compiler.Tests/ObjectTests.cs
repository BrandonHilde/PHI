using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    /// <summary>Objects: new T name, at, takes, empty, and is copying values.</summary>
    public class ObjectTests
    {
        static CompileResult Compile(string text) => PhiCompiler.Compile(new SourceFile("test.phi", text));

        static void AssertError(string text, string message)
        {
            CompileResult result = Compile(text);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains(message));
        }

        static string Kernel(string body) => Structs + "phi.K:Kernel\n{\n" + body + "\n}\n";

        const string Structs = "struct.Point\n{\n i16 x;\n i16 y;\n}\nstruct.Node\n{\n u32 value;\n Point at;\n new Node next;\n}\n";

        [Fact]
        public void NewDeclaresAnObject()
        {
            CompileResult result = Compile(Kernel("new Point p: 1 2;\np.x is 3;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            var decl = result.Syntax!.Classes.Single(c => c.Name == "K").Body.OfType<VarDecl>().Single();
            Assert.True(decl.IsNew);
            Assert.Equal(Ownership.Owned, decl.Symbol!.Ownership);
            Assert.True(decl.Symbol.Type.IsObject);
            Assert.Equal(2, decl.FieldValues.Count);
        }

        [Fact]
        public void ObjectsGetTheHeapAutomatically()
        {
            CompileResult result = Compile(Kernel("new Point p;"));
            Assert.Contains(result.Syntax!.Classes, c => c.Name == "Heap");
        }

        [Fact]
        public void AtCanStillBeAName()
        {
            CompileResult result = Compile("phi.B:Bootloader\n{\n int at: 3;\n at++;\n debug at;\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void AtWorksIn16BitCode()
        {
            CompileResult result = Compile(Structs + "phi.B:Bootloader\n{\n new Point p at 0xB8000;\n p.x is 1;\n}\n");
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Contains("[fs:ebx]", result.Units[0].Text);
        }

        [Fact]
        public void A16BitProgramCantAllocate() =>
            AssertError(Structs + "phi.B:Bootloader\n{\n new Point p;\n}\n", "new needs a heap");

        [Fact]
        public void A16BitProgramCantCopyStructs() =>
            AssertError(Structs + "phi.B:Bootloader\n{\n new Point a at 0xB8000;\n new Point b at 0xB8004;\n a is b;\n}\n", "needs 32-bit code");

        [Fact]
        public void UserProgramsCanBorrowAndCopyButNotPlaceAtAnAddress()
        {
            CompileResult ok = Compile("struct.Point\n{\n i16 x;\n}\nphi.P:Program\n{\n Point a;\n Point b;\n a.x is 2;\n b is a;\n call Show: b;\n [Show: Point p;]\n  log p.x;\n [end]\n}\n");
            Assert.True(ok.Success, string.Join("\n", ok.Diagnostics));

            AssertError("struct.Point\n{\n i16 x;\n}\nphi.P:Program\n{\n new Point a at 0x1000;\n}\n", "can't place an object at an address");
        }

        [Fact]
        public void OwnedLocalsAreFreedWhenTheMethodEnds()
        {
            string asm = Compile(Kernel("call M;\n[M]\n new Node n;\n[end]")).Units[0].Text;
            Assert.Contains("call phi_free_Node", asm);
            Assert.Contains("phi_free_Node:", asm);
        }

        [Fact]
        public void AReturnedObjectIsHandedOverNotFreed()
        {
            string asm = Compile(Kernel("new Point p: (Make);\n[Make]\n new Point made: 1 2;\n[end: made]")).Units[0].Text;
            Assert.Contains("handed to the caller", asm);
            Assert.DoesNotContain("phi_clone_Point:", asm);
        }

        [Fact]
        public void IsBetweenObjectsCopies()
        {
            CompileResult result = Compile(Kernel("new Node a;\nnew Node b;\na is b;\na.next is empty;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var body = result.Syntax!.Classes.Single(c => c.Name == "K").Body;
            var copies = body.OfType<ObjectAssignStmt>().ToList();
            Assert.Equal(ObjectAssignKind.Copy, copies[0].Kind);
            Assert.Equal(ObjectAssignKind.Clear, copies[1].Kind);
            Assert.True(copies[1].TargetOwned);
        }

        [Fact]
        public void FieldsThroughObjectsArePaths()
        {
            CompileResult result = Compile(Kernel("new Node a;\na.next.at.y is 4;"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var assign = result.Syntax!.Classes.Single(c => c.Name == "K").Body.OfType<AssignStmt>().Single();
            var path = Assert.IsType<PathSymbol>(((NameExpr)assign.Target).Symbol);
            Assert.Equal(new[] { 8, 6 }, path.Hops); // next is at 8; at.y is 4 + 2 into the next node
        }

        [Fact]
        public void ParametersBorrowWithoutNew() =>
            AssertError(Kernel("[M: new Point p;]\n[end]"), "written without new");

        [Fact]
        public void AStructInAnArrayCantOwnObjects() =>
            AssertError(Structs + "struct.List\n{\n Node nodes[4];\n}\nphi.K:Kernel\n{\n}\n", "can't be an array element");

        [Fact]
        public void EmptyOnlyComparesWithObjects() =>
            AssertError(Kernel("int n: 0;\nif n is empty\n;;"), "only an object can be empty");

        [Fact]
        public void TakesNeedsOwners() =>
            AssertError(Kernel("new Node a;\n[M: Node n;]\n a takes n;\n[end]"), "a parameter, which borrows its object");
    }
}
